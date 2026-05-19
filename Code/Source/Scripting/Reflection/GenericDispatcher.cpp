/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

#include "GenericDispatcher.h"

#include <AzCore/Component/ComponentApplicationBus.h>
#include <AzCore/Console/ILogger.h>
#include <AzCore/Math/Vector3.h>
#include <AzCore/Math/Quaternion.h>
#include <AzCore/Math/Transform.h>
#include <AzCore/Component/EntityId.h>
#include <AzCore/JSON/rapidjson.h>
#include <AzCore/JSON/document.h>
#include <AzCore/JSON/stringbuffer.h>
#include <AzCore/JSON/writer.h>

#include <Coral/Assembly.hpp>

#include <Scripting/Marshaling/BehaviorContextMarshaling.h>

namespace O3DESharp
{
    // Thread-local storage for marshalling
    thread_local AZStd::vector<AZStd::any> GenericDispatcher::s_marshalStorage;

    // Global dispatcher instance (singleton pattern for internal calls)
    static GenericDispatcher* s_dispatcherInstance = nullptr;

    // ============================================================
    // MarshalledValue Implementation
    // ============================================================

    MarshalledValue MarshalledValue::FromBool(bool value)
    {
        MarshalledValue mv;
        mv.type = ReflectedParameter::MarshalType::Bool;
        mv.boolValue = value;
        return mv;
    }

    MarshalledValue MarshalledValue::FromInt32(int32_t value)
    {
        MarshalledValue mv;
        mv.type = ReflectedParameter::MarshalType::Int32;
        mv.int32Value = value;
        return mv;
    }

    MarshalledValue MarshalledValue::FromInt64(int64_t value)
    {
        MarshalledValue mv;
        mv.type = ReflectedParameter::MarshalType::Int64;
        mv.int64Value = value;
        return mv;
    }

    MarshalledValue MarshalledValue::FromUInt64(uint64_t value)
    {
        MarshalledValue mv;
        mv.type = ReflectedParameter::MarshalType::UInt64;
        mv.uint64Value = value;
        return mv;
    }

    MarshalledValue MarshalledValue::FromFloat(float value)
    {
        MarshalledValue mv;
        mv.type = ReflectedParameter::MarshalType::Float;
        mv.floatValue = value;
        return mv;
    }

    MarshalledValue MarshalledValue::FromDouble(double value)
    {
        MarshalledValue mv;
        mv.type = ReflectedParameter::MarshalType::Double;
        mv.doubleValue = value;
        return mv;
    }

    MarshalledValue MarshalledValue::FromString(const AZStd::string& value)
    {
        MarshalledValue mv;
        mv.type = ReflectedParameter::MarshalType::String;
        mv.stringValue = value;
        return mv;
    }

    MarshalledValue MarshalledValue::FromVector3(float x, float y, float z)
    {
        MarshalledValue mv;
        mv.type = ReflectedParameter::MarshalType::Vector3;
        mv.vector3Value.x = x;
        mv.vector3Value.y = y;
        mv.vector3Value.z = z;
        return mv;
    }

    MarshalledValue MarshalledValue::FromQuaternion(float x, float y, float z, float w)
    {
        MarshalledValue mv;
        mv.type = ReflectedParameter::MarshalType::Quaternion;
        mv.quaternionValue.x = x;
        mv.quaternionValue.y = y;
        mv.quaternionValue.z = z;
        mv.quaternionValue.w = w;
        return mv;
    }

    MarshalledValue MarshalledValue::FromEntityId(AZ::u64 entityId)
    {
        MarshalledValue mv;
        mv.type = ReflectedParameter::MarshalType::EntityId;
        mv.uint64Value = entityId;
        return mv;
    }

    MarshalledValue MarshalledValue::FromObject(void* handle, const AZStd::string& typeName)
    {
        MarshalledValue mv;
        mv.type = ReflectedParameter::MarshalType::Object;
        mv.objectHandle = handle;
        mv.objectTypeName = typeName;
        return mv;
    }

    // ============================================================
    // DispatchResult Implementation
    // ============================================================

    DispatchResult DispatchResult::Success(const MarshalledValue& value)
    {
        DispatchResult result;
        result.success = true;
        result.returnValue = value;
        return result;
    }

    DispatchResult DispatchResult::Error(const AZStd::string& message)
    {
        DispatchResult result;
        result.success = false;
        result.errorMessage = message;
        return result;
    }

    // ============================================================
    // GenericDispatcher Implementation
    // ============================================================

    void GenericDispatcher::Initialize(BehaviorContextReflector* reflector)
    {
        if (m_initialized)
        {
            AZLOG_WARN("GenericDispatcher::Initialize - Already initialized");
            return;
        }

        m_reflector = reflector;
        m_initialized = true;
        s_dispatcherInstance = this;

        AZLOG_INFO("GenericDispatcher: Initialized successfully");
    }

    void GenericDispatcher::Shutdown()
    {
        if (!m_initialized)
        {
            return;
        }

        s_dispatcherInstance = nullptr;
        m_reflector = nullptr;
        m_initialized = false;

        AZLOG_INFO("GenericDispatcher: Shutdown complete");
    }

    DispatchResult GenericDispatcher::InvokeStaticMethod(
        const AZStd::string& className,
        const AZStd::string& methodName,
        const AZStd::vector<MarshalledValue>& arguments)
    {
        if (!m_initialized || !m_reflector)
        {
            return DispatchResult::Error("Dispatcher not initialized");
        }

        // Find the class
        const ReflectedClass* cls = m_reflector->GetClass(className);
        if (!cls)
        {
            return DispatchResult::Error(AZStd::string::format("Class not found: %s", className.c_str()));
        }

        // Find a matching static method
        const ReflectedMethod* method = FindMatchingMethod(cls, methodName, arguments, true);
        if (!method)
        {
            return DispatchResult::Error(AZStd::string::format("Static method not found: %s.%s", className.c_str(), methodName.c_str()));
        }

        if (!method->behaviorMethod)
        {
            return DispatchResult::Error(AZStd::string::format("Method has no behavior binding: %s.%s", className.c_str(), methodName.c_str()));
        }

        // Clear storage for this call
        s_marshalStorage.clear();

        // Marshal arguments
        AZStd::vector<AZ::BehaviorArgument> behaviorArgs;
        behaviorArgs.reserve(arguments.size());

        for (size_t i = 0; i < arguments.size(); ++i)
        {
            if (i >= method->parameters.size())
            {
                return DispatchResult::Error("Too many arguments provided");
            }

            AZ::BehaviorArgument arg;
            if (!MarshalToBehaviorArgument(arguments[i], method->parameters[i], arg, s_marshalStorage))
            {
                return DispatchResult::Error(AZStd::string::format("Failed to marshal argument %zu", i));
            }
            behaviorArgs.push_back(arg);
        }

        // Prepare result storage
        AZ::BehaviorArgument resultArg;
        AZStd::any resultStorage;
        
        bool hasResult = method->returnType.marshalType != ReflectedParameter::MarshalType::Void;
        if (hasResult)
        {
            // Allocate storage based on return type
            switch (method->returnType.marshalType)
            {
            case ReflectedParameter::MarshalType::Bool:
                resultStorage = false;
                resultArg.m_value = AZStd::any_cast<bool>(&resultStorage);
                resultArg.m_typeId = azrtti_typeid<bool>();
                break;
            case ReflectedParameter::MarshalType::Int32:
                resultStorage = int32_t(0);
                resultArg.m_value = AZStd::any_cast<int32_t>(&resultStorage);
                resultArg.m_typeId = azrtti_typeid<int32_t>();
                break;
            case ReflectedParameter::MarshalType::Int64:
                resultStorage = int64_t(0);
                resultArg.m_value = AZStd::any_cast<int64_t>(&resultStorage);
                resultArg.m_typeId = azrtti_typeid<int64_t>();
                break;
            case ReflectedParameter::MarshalType::Float:
                resultStorage = 0.0f;
                resultArg.m_value = AZStd::any_cast<float>(&resultStorage);
                resultArg.m_typeId = azrtti_typeid<float>();
                break;
            case ReflectedParameter::MarshalType::Double:
                resultStorage = 0.0;
                resultArg.m_value = AZStd::any_cast<double>(&resultStorage);
                resultArg.m_typeId = azrtti_typeid<double>();
                break;
            case ReflectedParameter::MarshalType::String:
                resultStorage = AZStd::string();
                resultArg.m_value = AZStd::any_cast<AZStd::string>(&resultStorage);
                resultArg.m_typeId = azrtti_typeid<AZStd::string>();
                break;
            case ReflectedParameter::MarshalType::Vector3:
                resultStorage = AZ::Vector3::CreateZero();
                resultArg.m_value = AZStd::any_cast<AZ::Vector3>(&resultStorage);
                resultArg.m_typeId = azrtti_typeid<AZ::Vector3>();
                break;
            case ReflectedParameter::MarshalType::Quaternion:
                resultStorage = AZ::Quaternion::CreateIdentity();
                resultArg.m_value = AZStd::any_cast<AZ::Quaternion>(&resultStorage);
                resultArg.m_typeId = azrtti_typeid<AZ::Quaternion>();
                break;
            case ReflectedParameter::MarshalType::EntityId:
                resultStorage = AZ::EntityId();
                resultArg.m_value = AZStd::any_cast<AZ::EntityId>(&resultStorage);
                resultArg.m_typeId = azrtti_typeid<AZ::EntityId>();
                break;
            default:
                // For unknown types, we still need to allocate something
                break;
            }
        }

        // Invoke the method
        bool invokeSuccess = false;
        if (behaviorArgs.empty())
        {
            invokeSuccess = method->behaviorMethod->Call(nullptr, 0, hasResult ? &resultArg : nullptr);
        }
        else
        {
            invokeSuccess = method->behaviorMethod->Call(behaviorArgs.data(), static_cast<unsigned int>(behaviorArgs.size()), hasResult ? &resultArg : nullptr);
        }

        if (!invokeSuccess)
        {
            return DispatchResult::Error(AZStd::string::format("Method invocation failed: %s.%s", className.c_str(), methodName.c_str()));
        }

        // Marshal return value
        if (hasResult)
        {
            MarshalledValue returnValue = MarshalFromBehaviorResult(resultArg, method->returnType);
            return DispatchResult::Success(returnValue);
        }

        return DispatchResult::Success();
    }

    DispatchResult GenericDispatcher::InvokeInstanceMethod(
        const AZStd::string& className,
        const AZStd::string& methodName,
        void* instanceHandle,
        const AZStd::vector<MarshalledValue>& arguments)
    {
        if (!m_initialized || !m_reflector)
        {
            return DispatchResult::Error("Dispatcher not initialized");
        }

        if (!instanceHandle)
        {
            return DispatchResult::Error("Instance handle is null");
        }

        // Find the class
        const ReflectedClass* cls = m_reflector->GetClass(className);
        if (!cls)
        {
            return DispatchResult::Error(AZStd::string::format("Class not found: %s", className.c_str()));
        }

        // Find a matching instance method
        const ReflectedMethod* method = FindMatchingMethod(cls, methodName, arguments, false);
        if (!method)
        {
            return DispatchResult::Error(AZStd::string::format("Instance method not found: %s.%s", className.c_str(), methodName.c_str()));
        }

        if (!method->behaviorMethod)
        {
            return DispatchResult::Error(AZStd::string::format("Method has no behavior binding: %s.%s", className.c_str(), methodName.c_str()));
        }

        // Clear storage for this call
        s_marshalStorage.clear();

        // Build argument list with 'this' as first argument
        AZStd::vector<AZ::BehaviorArgument> behaviorArgs;
        behaviorArgs.reserve(arguments.size() + 1);

        // Add 'this' pointer
        AZ::BehaviorArgument thisArg;
        thisArg.m_value = instanceHandle;
        thisArg.m_typeId = cls->typeId;
        thisArg.m_traits = AZ::BehaviorParameter::TR_POINTER;
        behaviorArgs.push_back(thisArg);

        // Marshal remaining arguments
        for (size_t i = 0; i < arguments.size(); ++i)
        {
            if (i >= method->parameters.size())
            {
                return DispatchResult::Error("Too many arguments provided");
            }

            AZ::BehaviorArgument arg;
            if (!MarshalToBehaviorArgument(arguments[i], method->parameters[i], arg, s_marshalStorage))
            {
                return DispatchResult::Error(AZStd::string::format("Failed to marshal argument %zu", i));
            }
            behaviorArgs.push_back(arg);
        }

        // Prepare result storage
        AZ::BehaviorArgument resultArg;
        AZStd::any resultStorage;
        
        bool hasResult = method->returnType.marshalType != ReflectedParameter::MarshalType::Void;
        if (hasResult)
        {
            switch (method->returnType.marshalType)
            {
            case ReflectedParameter::MarshalType::Bool:
                resultStorage = false;
                resultArg.m_value = AZStd::any_cast<bool>(&resultStorage);
                resultArg.m_typeId = azrtti_typeid<bool>();
                break;
            case ReflectedParameter::MarshalType::Int32:
                resultStorage = int32_t(0);
                resultArg.m_value = AZStd::any_cast<int32_t>(&resultStorage);
                resultArg.m_typeId = azrtti_typeid<int32_t>();
                break;
            case ReflectedParameter::MarshalType::Float:
                resultStorage = 0.0f;
                resultArg.m_value = AZStd::any_cast<float>(&resultStorage);
                resultArg.m_typeId = azrtti_typeid<float>();
                break;
            case ReflectedParameter::MarshalType::Vector3:
                resultStorage = AZ::Vector3::CreateZero();
                resultArg.m_value = AZStd::any_cast<AZ::Vector3>(&resultStorage);
                resultArg.m_typeId = azrtti_typeid<AZ::Vector3>();
                break;
            default:
                break;
            }
        }

        // Invoke the method
        bool invokeSuccess = method->behaviorMethod->Call(
            behaviorArgs.data(), 
            static_cast<unsigned int>(behaviorArgs.size()), 
            hasResult ? &resultArg : nullptr);

        if (!invokeSuccess)
        {
            return DispatchResult::Error(AZStd::string::format("Method invocation failed: %s.%s", className.c_str(), methodName.c_str()));
        }

        // Marshal return value
        if (hasResult)
        {
            MarshalledValue returnValue = MarshalFromBehaviorResult(resultArg, method->returnType);
            return DispatchResult::Success(returnValue);
        }

        return DispatchResult::Success();
    }

    DispatchResult GenericDispatcher::InvokeGlobalMethod(
        const AZStd::string& methodName,
        const AZStd::vector<MarshalledValue>& arguments)
    {
        if (!m_initialized || !m_reflector)
        {
            return DispatchResult::Error("Dispatcher not initialized");
        }

        // Find the global method
        const ReflectedMethod* method = nullptr;
        for (const auto& globalMethod : m_reflector->GetGlobalMethods())
        {
            if (globalMethod.name == methodName && globalMethod.parameters.size() == arguments.size())
            {
                method = &globalMethod;
                break;
            }
        }

        if (!method)
        {
            return DispatchResult::Error(AZStd::string::format("Global method not found: %s", methodName.c_str()));
        }

        if (!method->behaviorMethod)
        {
            return DispatchResult::Error(AZStd::string::format("Method has no behavior binding: %s", methodName.c_str()));
        }

        // Clear storage
        s_marshalStorage.clear();

        // Marshal arguments
        AZStd::vector<AZ::BehaviorArgument> behaviorArgs;
        behaviorArgs.reserve(arguments.size());

        for (size_t i = 0; i < arguments.size(); ++i)
        {
            AZ::BehaviorArgument arg;
            if (!MarshalToBehaviorArgument(arguments[i], method->parameters[i], arg, s_marshalStorage))
            {
                return DispatchResult::Error(AZStd::string::format("Failed to marshal argument %zu", i));
            }
            behaviorArgs.push_back(arg);
        }

        // Prepare result
        AZ::BehaviorArgument resultArg;
        AZStd::any resultStorage;
        bool hasResult = method->returnType.marshalType != ReflectedParameter::MarshalType::Void;

        // Invoke
        bool invokeSuccess = false;
        if (behaviorArgs.empty())
        {
            invokeSuccess = method->behaviorMethod->Call(nullptr, 0, hasResult ? &resultArg : nullptr);
        }
        else
        {
            invokeSuccess = method->behaviorMethod->Call(behaviorArgs.data(), static_cast<unsigned int>(behaviorArgs.size()), hasResult ? &resultArg : nullptr);
        }

        if (!invokeSuccess)
        {
            return DispatchResult::Error(AZStd::string::format("Global method invocation failed: %s", methodName.c_str()));
        }

        if (hasResult)
        {
            return DispatchResult::Success(MarshalFromBehaviorResult(resultArg, method->returnType));
        }

        return DispatchResult::Success();
    }

    DispatchResult GenericDispatcher::GetProperty(
        const AZStd::string& className,
        const AZStd::string& propertyName,
        void* instanceHandle)
    {
        if (!m_initialized || !m_reflector)
        {
            return DispatchResult::Error("Dispatcher not initialized");
        }

        const ReflectedClass* cls = m_reflector->GetClass(className);
        if (!cls)
        {
            return DispatchResult::Error(AZStd::string::format("Class not found: %s", className.c_str()));
        }

        const ReflectedProperty* prop = cls->FindProperty(propertyName);
        if (!prop)
        {
            return DispatchResult::Error(AZStd::string::format("Property not found: %s.%s", className.c_str(), propertyName.c_str()));
        }

        if (!prop->hasGetter)
        {
            return DispatchResult::Error(AZStd::string::format("Property is write-only: %s.%s", className.c_str(), propertyName.c_str()));
        }

        if (!prop->behaviorProperty || !prop->behaviorProperty->m_getter)
        {
            return DispatchResult::Error(AZStd::string::format("Property has no getter binding: %s.%s", className.c_str(), propertyName.c_str()));
        }

        // Prepare result storage
        AZ::BehaviorArgument resultArg;
        AZStd::any resultStorage;

        switch (prop->valueType.marshalType)
        {
        case ReflectedParameter::MarshalType::Bool:
            resultStorage = false;
            resultArg.m_value = AZStd::any_cast<bool>(&resultStorage);
            resultArg.m_typeId = azrtti_typeid<bool>();
            break;
        case ReflectedParameter::MarshalType::Int32:
            resultStorage = int32_t(0);
            resultArg.m_value = AZStd::any_cast<int32_t>(&resultStorage);
            resultArg.m_typeId = azrtti_typeid<int32_t>();
            break;
        case ReflectedParameter::MarshalType::Float:
            resultStorage = 0.0f;
            resultArg.m_value = AZStd::any_cast<float>(&resultStorage);
            resultArg.m_typeId = azrtti_typeid<float>();
            break;
        case ReflectedParameter::MarshalType::String:
            resultStorage = AZStd::string();
            resultArg.m_value = AZStd::any_cast<AZStd::string>(&resultStorage);
            resultArg.m_typeId = azrtti_typeid<AZStd::string>();
            break;
        case ReflectedParameter::MarshalType::Vector3:
            resultStorage = AZ::Vector3::CreateZero();
            resultArg.m_value = AZStd::any_cast<AZ::Vector3>(&resultStorage);
            resultArg.m_typeId = azrtti_typeid<AZ::Vector3>();
            break;
        default:
            return DispatchResult::Error("Unsupported property type for get");
        }

        // Build arguments (instance for member property)
        AZStd::vector<AZ::BehaviorArgument> args;
        if (instanceHandle && prop->behaviorProperty->m_getter->IsMember())
        {
            AZ::BehaviorArgument thisArg;
            thisArg.m_value = instanceHandle;
            thisArg.m_typeId = cls->typeId;
            thisArg.m_traits = AZ::BehaviorParameter::TR_POINTER;
            args.push_back(thisArg);
        }

        bool success = prop->behaviorProperty->m_getter->Call(
            args.empty() ? nullptr : args.data(),
            static_cast<unsigned int>(args.size()),
            &resultArg);

        if (!success)
        {
            return DispatchResult::Error("Property getter invocation failed");
        }

        return DispatchResult::Success(MarshalFromBehaviorResult(resultArg, prop->valueType));
    }

    DispatchResult GenericDispatcher::SetProperty(
        const AZStd::string& className,
        const AZStd::string& propertyName,
        void* instanceHandle,
        const MarshalledValue& value)
    {
        if (!m_initialized || !m_reflector)
        {
            return DispatchResult::Error("Dispatcher not initialized");
        }

        const ReflectedClass* cls = m_reflector->GetClass(className);
        if (!cls)
        {
            return DispatchResult::Error(AZStd::string::format("Class not found: %s", className.c_str()));
        }

        const ReflectedProperty* prop = cls->FindProperty(propertyName);
        if (!prop)
        {
            return DispatchResult::Error(AZStd::string::format("Property not found: %s.%s", className.c_str(), propertyName.c_str()));
        }

        if (!prop->hasSetter)
        {
            return DispatchResult::Error(AZStd::string::format("Property is read-only: %s.%s", className.c_str(), propertyName.c_str()));
        }

        if (!prop->behaviorProperty || !prop->behaviorProperty->m_setter)
        {
            return DispatchResult::Error(AZStd::string::format("Property has no setter binding: %s.%s", className.c_str(), propertyName.c_str()));
        }

        s_marshalStorage.clear();

        AZStd::vector<AZ::BehaviorArgument> args;

        // Add 'this' for member properties
        if (instanceHandle && prop->behaviorProperty->m_setter->IsMember())
        {
            AZ::BehaviorArgument thisArg;
            thisArg.m_value = instanceHandle;
            thisArg.m_typeId = cls->typeId;
            thisArg.m_traits = AZ::BehaviorParameter::TR_POINTER;
            args.push_back(thisArg);
        }

        // Marshal the value
        AZ::BehaviorArgument valueArg;
        if (!MarshalToBehaviorArgument(value, prop->valueType, valueArg, s_marshalStorage))
        {
            return DispatchResult::Error("Failed to marshal property value");
        }
        args.push_back(valueArg);

        bool success = prop->behaviorProperty->m_setter->Call(
            args.data(),
            static_cast<unsigned int>(args.size()),
            nullptr);

        if (!success)
        {
            return DispatchResult::Error("Property setter invocation failed");
        }

        return DispatchResult::Success();
    }

    DispatchResult GenericDispatcher::GetGlobalProperty(const AZStd::string& propertyName)
    {
        if (!m_initialized || !m_reflector)
        {
            return DispatchResult::Error("Dispatcher not initialized");
        }

        const ReflectedProperty* prop = nullptr;
        for (const auto& globalProp : m_reflector->GetGlobalProperties())
        {
            if (globalProp.name == propertyName)
            {
                prop = &globalProp;
                break;
            }
        }

        if (!prop)
        {
            return DispatchResult::Error(AZStd::string::format("Global property not found: %s", propertyName.c_str()));
        }

        return GetProperty("", propertyName, nullptr);
    }

    DispatchResult GenericDispatcher::SetGlobalProperty(
        const AZStd::string& propertyName,
        const MarshalledValue& value)
    {
        if (!m_initialized || !m_reflector)
        {
            return DispatchResult::Error("Dispatcher not initialized");
        }

        return SetProperty("", propertyName, nullptr, value);
    }

    DispatchResult GenericDispatcher::BroadcastEBusEvent(
        const AZStd::string& busName,
        const AZStd::string& eventName,
        const AZStd::vector<MarshalledValue>& arguments)
    {
        // TODO(Mikael A.): Send this over.
        AZ_UNUSED(arguments);

        if (!m_initialized || !m_reflector)
        {
            return DispatchResult::Error("Dispatcher not initialized");
        }

        const ReflectedEBus* bus = m_reflector->GetEBus(busName);
        if (!bus)
        {
            return DispatchResult::Error(AZStd::string::format("EBus not found: %s", busName.c_str()));
        }

        // Find the event
        const ReflectedEBusEvent* event = nullptr;
        for (const auto& e : bus->events)
        {
            if (e.name == eventName)
            {
                event = &e;
                break;
            }
        }

        if (!event)
        {
            return DispatchResult::Error(AZStd::string::format("EBus event not found: %s.%s", busName.c_str(), eventName.c_str()));
        }

        // For now, return not implemented - EBus invocation is complex
        return DispatchResult::Error("EBus broadcast not yet implemented");
    }

    DispatchResult GenericDispatcher::SendEBusEvent(
        const AZStd::string& busName,
        const AZStd::string& eventName,
        const MarshalledValue& address,
        const AZStd::vector<MarshalledValue>& arguments)
    {
        AZ_UNUSED(busName);
        AZ_UNUSED(eventName);
        AZ_UNUSED(address);
        AZ_UNUSED(arguments);

        // For now, return not implemented
        return DispatchResult::Error("EBus send not yet implemented");
    }

    DispatchResult GenericDispatcher::CreateInstance(
        const AZStd::string& className,
        const AZStd::vector<MarshalledValue>& constructorArgs)
    {
        if (!m_initialized || !m_reflector)
        {
            return DispatchResult::Error("Dispatcher not initialized");
        }

        const ReflectedClass* cls = m_reflector->GetClass(className);
        if (!cls)
        {
            return DispatchResult::Error(AZStd::string::format("Class not found: %s", className.c_str()));
        }

        if (!cls->behaviorClass)
        {
            return DispatchResult::Error(AZStd::string::format("Class has no behavior binding: %s", className.c_str()));
        }

        // Find matching constructor
        const ReflectedMethod* constructor = FindMatchingConstructor(cls, constructorArgs);
        
        // Try default constructor if no args and no specific constructor found
        if (!constructor && constructorArgs.empty())
        {
            // Use the behavior class's default allocator
            if (cls->behaviorClass->m_defaultConstructor)
            {
                void* instance = cls->behaviorClass->Allocate();
                if (instance)
                {
                    cls->behaviorClass->m_defaultConstructor(instance, cls->behaviorClass->m_userData);
                    return DispatchResult::Success(MarshalledValue::FromObject(instance, className));
                }
            }
        }

        return DispatchResult::Error("Failed to create instance - no suitable constructor found");
    }

    DispatchResult GenericDispatcher::DestroyInstance(
        const AZStd::string& className,
        void* instanceHandle)
    {
        if (!m_initialized || !m_reflector)
        {
            return DispatchResult::Error("Dispatcher not initialized");
        }

        if (!instanceHandle)
        {
            return DispatchResult::Error("Instance handle is null");
        }

        const ReflectedClass* cls = m_reflector->GetClass(className);
        if (!cls || !cls->behaviorClass)
        {
            return DispatchResult::Error(AZStd::string::format("Class not found: %s", className.c_str()));
        }

        // Use the behavior class's destructor and deallocator
        if (cls->behaviorClass->m_destructor)
        {
            cls->behaviorClass->m_destructor(instanceHandle, cls->behaviorClass->m_userData);
        }
        cls->behaviorClass->Deallocate(instanceHandle);

        return DispatchResult::Success();
    }

    void GenericDispatcher::RegisterInternalCalls(Coral::ManagedAssembly* assembly)
    {
        if (!assembly)
        {
            AZLOG_ERROR("GenericDispatcher::RegisterInternalCalls - Assembly is null");
            return;
        }

        AZLOG_INFO("GenericDispatcher: Registering internal calls for generic dispatch...");

        // Reflection queries - register to ReflectionInternalCalls class with Reflection_ prefix
        assembly->AddInternalCall("O3DE.Reflection.ReflectionInternalCalls", "Reflection_GetClassNames", reinterpret_cast<void*>(&GenericDispatcherInternalCalls::Reflection_GetClassNames));
        assembly->AddInternalCall("O3DE.Reflection.ReflectionInternalCalls", "Reflection_GetMethodNames", reinterpret_cast<void*>(&GenericDispatcherInternalCalls::Reflection_GetMethodNames));
        assembly->AddInternalCall("O3DE.Reflection.ReflectionInternalCalls", "Reflection_GetPropertyNames", reinterpret_cast<void*>(&GenericDispatcherInternalCalls::Reflection_GetPropertyNames));
        assembly->AddInternalCall("O3DE.Reflection.ReflectionInternalCalls", "Reflection_GetEBusNames", reinterpret_cast<void*>(&GenericDispatcherInternalCalls::Reflection_GetEBusNames));
        assembly->AddInternalCall("O3DE.Reflection.ReflectionInternalCalls", "Reflection_GetEBusEventNames", reinterpret_cast<void*>(&GenericDispatcherInternalCalls::Reflection_GetEBusEventNames));
        assembly->AddInternalCall("O3DE.Reflection.ReflectionInternalCalls", "Reflection_ClassExists", reinterpret_cast<void*>(&GenericDispatcherInternalCalls::Reflection_ClassExists));
        assembly->AddInternalCall("O3DE.Reflection.ReflectionInternalCalls", "Reflection_MethodExists", reinterpret_cast<void*>(&GenericDispatcherInternalCalls::Reflection_MethodExists));

        // Method invocation
        assembly->AddInternalCall("O3DE.Reflection.ReflectionInternalCalls", "Reflection_InvokeStaticMethod", reinterpret_cast<void*>(&GenericDispatcherInternalCalls::InvokeStaticMethod));
        assembly->AddInternalCall("O3DE.Reflection.ReflectionInternalCalls", "Reflection_InvokeInstanceMethod", reinterpret_cast<void*>(&GenericDispatcherInternalCalls::InvokeInstanceMethod));
        assembly->AddInternalCall("O3DE.Reflection.ReflectionInternalCalls", "Reflection_InvokeGlobalMethod", reinterpret_cast<void*>(&GenericDispatcherInternalCalls::InvokeGlobalMethod));

        // Property access
        assembly->AddInternalCall("O3DE.Reflection.ReflectionInternalCalls", "Reflection_GetProperty", reinterpret_cast<void*>(&GenericDispatcherInternalCalls::GetProperty));
        assembly->AddInternalCall("O3DE.Reflection.ReflectionInternalCalls", "Reflection_SetProperty", reinterpret_cast<void*>(&GenericDispatcherInternalCalls::SetProperty));
        assembly->AddInternalCall("O3DE.Reflection.ReflectionInternalCalls", "Reflection_GetGlobalProperty", reinterpret_cast<void*>(&GenericDispatcherInternalCalls::GetGlobalProperty));
        assembly->AddInternalCall("O3DE.Reflection.ReflectionInternalCalls", "Reflection_SetGlobalProperty", reinterpret_cast<void*>(&GenericDispatcherInternalCalls::SetGlobalProperty));

        // EBus
        assembly->AddInternalCall("O3DE.Reflection.ReflectionInternalCalls", "Reflection_BroadcastEBusEvent", reinterpret_cast<void*>(&GenericDispatcherInternalCalls::BroadcastEBusEvent));
        assembly->AddInternalCall("O3DE.Reflection.ReflectionInternalCalls", "Reflection_SendEBusEvent", reinterpret_cast<void*>(&GenericDispatcherInternalCalls::SendEBusEvent));

        // Object lifecycle
        assembly->AddInternalCall("O3DE.Reflection.ReflectionInternalCalls", "Reflection_CreateInstance", reinterpret_cast<void*>(&GenericDispatcherInternalCalls::CreateInstance));
        assembly->AddInternalCall("O3DE.Reflection.ReflectionInternalCalls", "Reflection_DestroyInstance", reinterpret_cast<void*>(&GenericDispatcherInternalCalls::DestroyInstance));

        assembly->UploadInternalCalls();

        AZLOG_INFO("GenericDispatcher: Internal calls registered");
    }

    bool GenericDispatcher::MarshalToBehaviorArgument(
        const MarshalledValue& value,
        const ReflectedParameter& expectedType,
        AZ::BehaviorArgument& outArg,
        AZStd::vector<AZStd::any>& storageBuffer)
    {
        switch (value.type)
        {
        case ReflectedParameter::MarshalType::Bool:
            storageBuffer.emplace_back(AZStd::in_place_type<bool>, value.boolValue);
            outArg.m_value = AZStd::any_cast<bool>(&storageBuffer.back());
            outArg.m_typeId = azrtti_typeid<bool>();
            break;

        case ReflectedParameter::MarshalType::Int32:
            storageBuffer.emplace_back(AZStd::in_place_type<int32_t>, value.int32Value);
            outArg.m_value = AZStd::any_cast<int32_t>(&storageBuffer.back());
            outArg.m_typeId = azrtti_typeid<int32_t>();
            break;

        case ReflectedParameter::MarshalType::Int64:
            storageBuffer.emplace_back(AZStd::in_place_type<int64_t>, value.int64Value);
            outArg.m_value = AZStd::any_cast<int64_t>(&storageBuffer.back());
            outArg.m_typeId = azrtti_typeid<int64_t>();
            break;

        case ReflectedParameter::MarshalType::UInt64:
            storageBuffer.emplace_back(AZStd::in_place_type<uint64_t>, value.uint64Value);
            outArg.m_value = AZStd::any_cast<uint64_t>(&storageBuffer.back());
            outArg.m_typeId = azrtti_typeid<uint64_t>();
            break;

        case ReflectedParameter::MarshalType::Float:
            storageBuffer.emplace_back(AZStd::in_place_type<float>, value.floatValue);
            outArg.m_value = AZStd::any_cast<float>(&storageBuffer.back());
            outArg.m_typeId = azrtti_typeid<float>();
            break;

        case ReflectedParameter::MarshalType::Double:
            storageBuffer.emplace_back(AZStd::in_place_type<double>, value.doubleValue);
            outArg.m_value = AZStd::any_cast<double>(&storageBuffer.back());
            outArg.m_typeId = azrtti_typeid<double>();
            break;

        case ReflectedParameter::MarshalType::String:
            storageBuffer.emplace_back(AZStd::in_place_type<AZStd::string>, value.stringValue);
            outArg.m_value = AZStd::any_cast<AZStd::string>(&storageBuffer.back());
            outArg.m_typeId = azrtti_typeid<AZStd::string>();
            break;

        case ReflectedParameter::MarshalType::Vector3:
            {
                AZ::Vector3 vec(value.vector3Value.x, value.vector3Value.y, value.vector3Value.z);
                storageBuffer.emplace_back(AZStd::in_place_type<AZ::Vector3>, vec);
                outArg.m_value = AZStd::any_cast<AZ::Vector3>(&storageBuffer.back());
                outArg.m_typeId = azrtti_typeid<AZ::Vector3>();
            }
            break;

        case ReflectedParameter::MarshalType::Quaternion:
            {
                AZ::Quaternion quat(value.quaternionValue.x, value.quaternionValue.y, value.quaternionValue.z, value.quaternionValue.w);
                storageBuffer.emplace_back(AZStd::in_place_type<AZ::Quaternion>, quat);
                outArg.m_value = AZStd::any_cast<AZ::Quaternion>(&storageBuffer.back());
                outArg.m_typeId = azrtti_typeid<AZ::Quaternion>();
            }
            break;

        case ReflectedParameter::MarshalType::EntityId:
            {
                AZ::EntityId entityId(value.uint64Value);
                storageBuffer.emplace_back(AZStd::in_place_type<AZ::EntityId>, entityId);
                outArg.m_value = AZStd::any_cast<AZ::EntityId>(&storageBuffer.back());
                outArg.m_typeId = azrtti_typeid<AZ::EntityId>();
            }
            break;

        case ReflectedParameter::MarshalType::Object:
            // For objects, we pass the handle directly
            outArg.m_value = value.objectHandle;
            outArg.m_typeId = expectedType.typeId;
            outArg.m_traits = AZ::BehaviorParameter::TR_POINTER;
            break;

        default:
            return false;
        }

        return true;
    }

    MarshalledValue GenericDispatcher::MarshalFromBehaviorResult(
        const AZ::BehaviorArgument& result,
        const ReflectedParameter& resultType)
    {
        MarshalledValue value;

        if (!result.m_value)
        {
            return value;
        }

        switch (resultType.marshalType)
        {
        case ReflectedParameter::MarshalType::Bool:
            value = MarshalledValue::FromBool(*static_cast<bool*>(result.m_value));
            break;

        case ReflectedParameter::MarshalType::Int32:
            value = MarshalledValue::FromInt32(*static_cast<int32_t*>(result.m_value));
            break;

        case ReflectedParameter::MarshalType::Int64:
            value = MarshalledValue::FromInt64(*static_cast<int64_t*>(result.m_value));
            break;

        case ReflectedParameter::MarshalType::UInt64:
            value = MarshalledValue::FromUInt64(*static_cast<uint64_t*>(result.m_value));
            break;

        case ReflectedParameter::MarshalType::Float:
            value = MarshalledValue::FromFloat(*static_cast<float*>(result.m_value));
            break;

        case ReflectedParameter::MarshalType::Double:
            value = MarshalledValue::FromDouble(*static_cast<double*>(result.m_value));
            break;

        case ReflectedParameter::MarshalType::String:
            value = MarshalledValue::FromString(*static_cast<AZStd::string*>(result.m_value));
            break;

        case ReflectedParameter::MarshalType::Vector3:
            {
                AZ::Vector3* vec = static_cast<AZ::Vector3*>(result.m_value);
                value = MarshalledValue::FromVector3(vec->GetX(), vec->GetY(), vec->GetZ());
            }
            break;

        case ReflectedParameter::MarshalType::Quaternion:
            {
                AZ::Quaternion* quat = static_cast<AZ::Quaternion*>(result.m_value);
                value = MarshalledValue::FromQuaternion(quat->GetX(), quat->GetY(), quat->GetZ(), quat->GetW());
            }
            break;

        case ReflectedParameter::MarshalType::EntityId:
            {
                AZ::EntityId* entityId = static_cast<AZ::EntityId*>(result.m_value);
                value = MarshalledValue::FromEntityId(static_cast<AZ::u64>(*entityId));
            }
            break;

        case ReflectedParameter::MarshalType::Object:
            value = MarshalledValue::FromObject(result.m_value, resultType.typeName);
            break;

        default:
            break;
        }

        return value;
    }

    const ReflectedMethod* GenericDispatcher::FindMatchingMethod(
        const ReflectedClass* cls,
        const AZStd::string& methodName,
        const AZStd::vector<MarshalledValue>& arguments,
        bool isStatic)
    {
        if (!cls)
        {
            return nullptr;
        }

        for (const auto& method : cls->methods)
        {
            if (method.name == methodName && method.isStatic == isStatic)
            {
                // Check argument count
                if (method.parameters.size() == arguments.size())
                {
                    // TODO: Check type compatibility
                    return &method;
                }
            }
        }

        return nullptr;
    }

    const ReflectedMethod* GenericDispatcher::FindMatchingConstructor(
        const ReflectedClass* cls,
        const AZStd::vector<MarshalledValue>& arguments)
    {
        if (!cls)
        {
            return nullptr;
        }

        for (const auto& constructor : cls->constructors)
        {
            if (constructor.parameters.size() == arguments.size())
            {
                return &constructor;
            }
        }

        return nullptr;
    }

    // ============================================================
    // Internal Call Implementations
    // ============================================================

    namespace GenericDispatcherInternalCalls
    {
        Coral::String Reflection_GetClassNames()
        {
            if (!s_dispatcherInstance || !s_dispatcherInstance->GetReflector())
            {
                return Coral::String::New("[]");
            }

            AZStd::vector<AZStd::string> names = s_dispatcherInstance->GetReflector()->GetClassNames();
            
            // Build JSON array
            rapidjson::StringBuffer buffer;
            rapidjson::Writer<rapidjson::StringBuffer> writer(buffer);
            writer.StartArray();
            for (const auto& name : names)
            {
                writer.String(name.c_str(), static_cast<rapidjson::SizeType>(name.length()));
            }
            writer.EndArray();

            return Coral::String::New(buffer.GetString());
        }

        Coral::String Reflection_GetMethodNames(Coral::String className)
        {
            if (!s_dispatcherInstance || !s_dispatcherInstance->GetReflector())
            {
                return Coral::String::New("[]");
            }

            std::string classNameStr(className);
            const ReflectedClass* cls = s_dispatcherInstance->GetReflector()->GetClass(classNameStr.c_str());
            if (!cls)
            {
                return Coral::String::New("[]");
            }

            rapidjson::StringBuffer buffer;
            rapidjson::Writer<rapidjson::StringBuffer> writer(buffer);
            writer.StartArray();
            for (const auto& method : cls->methods)
            {
                writer.String(method.name.c_str(), static_cast<rapidjson::SizeType>(method.name.length()));
            }
            writer.EndArray();

            return Coral::String::New(buffer.GetString());
        }

        Coral::String Reflection_GetPropertyNames(Coral::String className)
        {
            if (!s_dispatcherInstance || !s_dispatcherInstance->GetReflector())
            {
                return Coral::String::New("[]");
            }

            std::string classNameStr(className);
            const ReflectedClass* cls = s_dispatcherInstance->GetReflector()->GetClass(classNameStr.c_str());
            if (!cls)
            {
                return Coral::String::New("[]");
            }

            rapidjson::StringBuffer buffer;
            rapidjson::Writer<rapidjson::StringBuffer> writer(buffer);
            writer.StartArray();
            for (const auto& prop : cls->properties)
            {
                writer.String(prop.name.c_str(), static_cast<rapidjson::SizeType>(prop.name.length()));
            }
            writer.EndArray();

            return Coral::String::New(buffer.GetString());
        }

        Coral::String Reflection_GetEBusNames()
        {
            if (!s_dispatcherInstance || !s_dispatcherInstance->GetReflector())
            {
                return Coral::String::New("[]");
            }

            AZStd::vector<AZStd::string> names = s_dispatcherInstance->GetReflector()->GetEBusNames();

            rapidjson::StringBuffer buffer;
            rapidjson::Writer<rapidjson::StringBuffer> writer(buffer);
            writer.StartArray();
            for (const auto& name : names)
            {
                writer.String(name.c_str(), static_cast<rapidjson::SizeType>(name.length()));
            }
            writer.EndArray();

            return Coral::String::New(buffer.GetString());
        }

        Coral::String Reflection_GetEBusEventNames(Coral::String busName)
        {
            if (!s_dispatcherInstance || !s_dispatcherInstance->GetReflector())
            {
                return Coral::String::New("[]");
            }

            std::string busNameStr(busName);
            const ReflectedEBus* bus = s_dispatcherInstance->GetReflector()->GetEBus(busNameStr.c_str());
            if (!bus)
            {
                return Coral::String::New("[]");
            }

            rapidjson::StringBuffer buffer;
            rapidjson::Writer<rapidjson::StringBuffer> writer(buffer);
            writer.StartArray();
            for (const auto& event : bus->events)
            {
                writer.String(event.name.c_str(), static_cast<rapidjson::SizeType>(event.name.length()));
            }
            writer.EndArray();

            return Coral::String::New(buffer.GetString());
        }

        bool Reflection_ClassExists(Coral::String className)
        {
            if (!s_dispatcherInstance || !s_dispatcherInstance->GetReflector())
            {
                return false;
            }

            std::string classNameStr(className);
            return s_dispatcherInstance->GetReflector()->HasClass(classNameStr.c_str());
        }

        bool Reflection_MethodExists(Coral::String className, Coral::String methodName)
        {
            if (!s_dispatcherInstance || !s_dispatcherInstance->GetReflector())
            {
                return false;
            }

            std::string classNameStr(className);
            std::string methodNameStr(methodName);
            
            const ReflectedClass* cls = s_dispatcherInstance->GetReflector()->GetClass(classNameStr.c_str());
            if (!cls)
            {
                return false;
            }

            return cls->FindMethod(methodNameStr.c_str()) != nullptr;
        }

        // Real implementations of these moved below DispatchEBusEvent so
        // they can share the CallBehaviorMethodAndMarshalResult helper.
        // The stubs that used to return "Not fully implemented" were
        // pre-Phase-18 placeholders; every reflection-backend generated
        // wrapper for a class method / global function / property now
        // routes through one of the implementations below.
        Coral::String InvokeStaticMethod(Coral::String className, Coral::String methodName, Coral::String argsJson);
        Coral::String InvokeInstanceMethod(Coral::String className, Coral::String methodName, int64_t instanceHandle, Coral::String argsJson);
        Coral::String InvokeGlobalMethod(Coral::String methodName, Coral::String argsJson);
        Coral::String GetProperty(Coral::String className, Coral::String propertyName, int64_t instanceHandle);
        bool SetProperty(Coral::String className, Coral::String propertyName, int64_t instanceHandle, Coral::String valueJson);
        Coral::String GetGlobalProperty(Coral::String propertyName);
        bool SetGlobalProperty(Coral::String propertyName, Coral::String valueJson);

        namespace
        {
            // Phase 18-A: shared helper used by BroadcastEBusEvent and
            // SendEBusEvent. Both flows look up the bus + event, marshal
            // args, dispatch through the BehaviorMethod, marshal the
            // result back to JSON. The only difference between them is
            // whether they use the addressed (m_event) or unaddressed
            // (m_broadcast) variant of the event sender, and whether the
            // caller-supplied bus id is consumed as a leading argument.
            Coral::String DispatchEBusEvent(
                Coral::String busName,
                Coral::String eventName,
                Coral::String argsJson,
                bool addressed,
                AZ::u64 busIdAsU64)
            {
                std::string busNameStr(busName);
                std::string eventNameStr(eventName);
                std::string argsJsonStr(argsJson);

                auto makeError = [&](const char* fmt, ...) -> Coral::String
                {
                    va_list args;
                    va_start(args, fmt);
                    char buf[1024] = {};
                    azvsnprintf(buf, sizeof(buf), fmt, args);
                    va_end(args);
                    AZ_Warning(
                        "O3DESharp",
                        false,
                        "BroadcastEBusEvent('%s', '%s'): %s",
                        busNameStr.c_str(), eventNameStr.c_str(), buf);
                    AZStd::string msg = AZStd::string::format("{\"error\":\"%s\"}", buf);
                    return Coral::String::New(msg.c_str());
                };

                // 1. Find the BehaviorContext.
                AZ::BehaviorContext* behaviorContext = nullptr;
                AZ::ComponentApplicationBus::BroadcastResult(
                    behaviorContext,
                    &AZ::ComponentApplicationRequests::GetBehaviorContext);
                if (behaviorContext == nullptr)
                {
                    return makeError("no BehaviorContext available");
                }

                // 2. Look up the bus by name.
                auto busIt = behaviorContext->m_ebuses.find(busNameStr.c_str());
                if (busIt == behaviorContext->m_ebuses.end())
                {
                    return makeError("bus '%s' not reflected in BehaviorContext",
                                     busNameStr.c_str());
                }
                AZ::BehaviorEBus* bus = busIt->second;

                // 3. Look up the event on the bus.
                auto evtIt = bus->m_events.find(eventNameStr.c_str());
                if (evtIt == bus->m_events.end())
                {
                    return makeError("event '%s' not found on bus '%s'",
                                     eventNameStr.c_str(), busNameStr.c_str());
                }
                AZ::BehaviorEBusEventSender& sender = evtIt->second;

                // Pick broadcast vs addressed dispatch.
                AZ::BehaviorMethod* method = addressed
                    ? (sender.m_event != nullptr ? sender.m_event : sender.m_broadcast)
                    : (sender.m_broadcast != nullptr ? sender.m_broadcast : sender.m_event);
                if (method == nullptr)
                {
                    return makeError(
                        "bus '%s' event '%s' has no %s dispatcher reflected",
                        busNameStr.c_str(), eventNameStr.c_str(),
                        addressed ? "Event" : "Broadcast");
                }

                // 4. Parse args JSON and marshal into BehaviorArguments.
                rapidjson::Document argsDoc;
                if (argsJsonStr.empty())
                {
                    argsDoc.SetArray(); // no-arg event
                }
                else
                {
                    argsDoc.Parse(argsJsonStr.c_str());
                    if (argsDoc.HasParseError())
                    {
                        return makeError("args JSON parse error at offset %zu",
                                         argsDoc.GetErrorOffset());
                    }
                }

                // Addressed dispatch has the bus id as the leading
                // argument in BehaviorMethod's signature; we synthesize
                // it from the busIdAsU64 the caller supplied.
                Marshaling::StackAllocator marshalAlloc;
                AZStd::vector<AZ::BehaviorArgument> dispatchArgs;
                AZStd::string marshalError;
                const size_t skipFront = addressed ? 1 : 0;
                if (!Marshaling::MarshalJsonArrayToArguments(
                        argsDoc, *method, dispatchArgs,
                        marshalAlloc, marshalError, skipFront))
                {
                    return makeError("arg marshal: %s", marshalError.c_str());
                }
                AZ::BehaviorArgument busIdArg;
                if (addressed)
                {
                    // Bus id types vary - EntityId, integers, strings.
                    // In v1 we accept any integer-compatible id by
                    // populating a u64 cast; the BehaviorMethod's expected
                    // parameter type does the final coercion via the
                    // BehaviorContext type system.
                    const AZ::BehaviorParameter* idParam = method->GetArgument(0);
                    if (idParam == nullptr)
                    {
                        return makeError("addressed event has no bus-id parameter");
                    }
                    rapidjson::Document idDoc;
                    idDoc.SetUint64(busIdAsU64);
                    AZStd::string idError;
                    if (!Marshaling::JsonValueToBehaviorParameter(
                            idDoc, *idParam, busIdArg, marshalAlloc, idError))
                    {
                        return makeError("bus id marshal: %s", idError.c_str());
                    }
                }

                // Build the final argument array. Addressed: [busId, args...].
                AZStd::vector<AZ::BehaviorArgument*> argPtrs;
                argPtrs.reserve(dispatchArgs.size() + (addressed ? 1u : 0u));
                if (addressed)
                {
                    argPtrs.push_back(&busIdArg);
                }
                for (auto& a : dispatchArgs)
                {
                    argPtrs.push_back(&a);
                }

                // 5. Call. BehaviorMethod::Call signature is (args[],
                // numArgs, result*). The args parameter is a flat
                // BehaviorArgument array, not a pointer array - rebuild.
                AZStd::vector<AZ::BehaviorArgument> flatArgs;
                flatArgs.reserve(argPtrs.size());
                for (auto* p : argPtrs)
                {
                    flatArgs.push_back(*p);
                }

                AZ::BehaviorArgument result;
                const bool hasReturn = method->HasResult();
                if (hasReturn)
                {
                    // Pre-populate the result BehaviorArgument's type
                    // metadata from the reflected return-type parameter.
                    // BehaviorMethod::Call writes the return value into
                    // result's storage but doesn't always set m_typeId,
                    // m_name, or m_traits - and BehaviorArgumentToJsonValue
                    // dispatches on m_typeId. Without this, primitives
                    // like TickRequestBus::GetTickDeltaTime (float return)
                    // come out with m_typeId = AZ::TypeId::CreateNull(),
                    // hit the marshaler's catch-all error path, and
                    // surface as
                    //   "result marshal: unsupported return type 0x{00000000-...}"
                    // even though the storage actually does contain the
                    // float result.
                    if (const auto* resultParam = method->GetResult())
                    {
                        result.m_typeId = resultParam->m_typeId;
                        result.m_name = resultParam->m_name;
                        result.m_traits = resultParam->m_traits;
                    }
                }
                const bool ok = method->Call(
                    flatArgs.data(),
                    static_cast<unsigned int>(flatArgs.size()),
                    hasReturn ? &result : nullptr);
                if (!ok)
                {
                    return makeError("BehaviorMethod::Call returned false");
                }
                // Defensive: if Call did set m_typeId itself, we use that;
                // if it didn't, our pre-populated value wins. Either way
                // the marshaler now has a valid TypeId to dispatch on.
                if (hasReturn && result.m_typeId.IsNull())
                {
                    if (const auto* resultParam = method->GetResult())
                    {
                        result.m_typeId = resultParam->m_typeId;
                    }
                }

                // 6. Marshal the result back to JSON.
                if (!hasReturn)
                {
                    return Coral::String::New("{\"ok\":true}");
                }
                rapidjson::Document resultDoc;
                resultDoc.SetObject();
                rapidjson::Value resultValue;
                AZStd::string resultError;
                if (!Marshaling::BehaviorArgumentToJsonValue(
                        result, resultValue, resultDoc.GetAllocator(), resultError))
                {
                    return makeError("result marshal: %s", resultError.c_str());
                }
                resultDoc.AddMember("result", resultValue, resultDoc.GetAllocator());
                rapidjson::StringBuffer buffer;
                rapidjson::Writer<rapidjson::StringBuffer> writer(buffer);
                resultDoc.Accept(writer);
                return Coral::String::New(buffer.GetString());
            }
        } // namespace

        namespace
        {
            // Phase 18-B: factor the "marshal args, call, marshal result"
            // tail of DispatchEBusEvent into a reusable helper so the
            // new class-method / global-method / property entry points
            // don't have to duplicate it. Used by InvokeStaticMethod,
            // InvokeGlobalMethod, GetGlobalProperty, SetGlobalProperty
            // (and eventually the instance-method variants once we have
            // a NativeObject handle table).
            //
            // contextLabel is used purely for the error-log prefix
            // ("InvokeStaticMethod('AZ::Vector3', 'Length'): ...") so
            // failures point at the right call site.
            Coral::String CallBehaviorMethodAndMarshalResult(
                AZ::BehaviorMethod* method,
                const char* contextLabel,
                Coral::String argsJson)
            {
                std::string argsJsonStr(argsJson);

                auto makeError = [&](const char* fmt, ...) -> Coral::String
                {
                    va_list args;
                    va_start(args, fmt);
                    char buf[1024] = {};
                    azvsnprintf(buf, sizeof(buf), fmt, args);
                    va_end(args);
                    AZ_Warning("O3DESharp", false, "%s: %s", contextLabel, buf);
                    AZStd::string msg = AZStd::string::format("{\"error\":\"%s\"}", buf);
                    return Coral::String::New(msg.c_str());
                };

                if (method == nullptr)
                {
                    return makeError("method is null");
                }

                // Parse the args array (empty when no parameters).
                rapidjson::Document argsDoc;
                if (argsJsonStr.empty())
                {
                    argsDoc.SetArray();
                }
                else
                {
                    argsDoc.Parse(argsJsonStr.c_str());
                    if (argsDoc.HasParseError())
                    {
                        return makeError("args JSON parse error at offset %zu",
                                         argsDoc.GetErrorOffset());
                    }
                }

                Marshaling::StackAllocator marshalAlloc;
                AZStd::vector<AZ::BehaviorArgument> dispatchArgs;
                AZStd::string marshalError;
                if (!Marshaling::MarshalJsonArrayToArguments(
                        argsDoc, *method, dispatchArgs,
                        marshalAlloc, marshalError, /*skipFront=*/0))
                {
                    return makeError("arg marshal: %s", marshalError.c_str());
                }

                AZ::BehaviorArgument result;
                const bool hasReturn = method->HasResult();
                if (hasReturn)
                {
                    // Same pre-population trick as DispatchEBusEvent uses -
                    // see commit a2759ff for the rationale (some
                    // BehaviorMethod subclasses don't write m_typeId on the
                    // result, so the marshaler can't dispatch on it).
                    if (const auto* resultParam = method->GetResult())
                    {
                        result.m_typeId = resultParam->m_typeId;
                        result.m_name = resultParam->m_name;
                        result.m_traits = resultParam->m_traits;
                    }
                }
                const bool ok = method->Call(
                    dispatchArgs.data(),
                    static_cast<unsigned int>(dispatchArgs.size()),
                    hasReturn ? &result : nullptr);
                if (!ok)
                {
                    return makeError("BehaviorMethod::Call returned false");
                }
                if (hasReturn && result.m_typeId.IsNull())
                {
                    if (const auto* resultParam = method->GetResult())
                    {
                        result.m_typeId = resultParam->m_typeId;
                    }
                }

                if (!hasReturn)
                {
                    return Coral::String::New("{\"ok\":true}");
                }
                rapidjson::Document resultDoc;
                resultDoc.SetObject();
                rapidjson::Value resultValue;
                AZStd::string resultError;
                if (!Marshaling::BehaviorArgumentToJsonValue(
                        result, resultValue, resultDoc.GetAllocator(), resultError))
                {
                    return makeError("result marshal: %s", resultError.c_str());
                }
                resultDoc.AddMember("result", resultValue, resultDoc.GetAllocator());
                rapidjson::StringBuffer buffer;
                rapidjson::Writer<rapidjson::StringBuffer> writer(buffer);
                resultDoc.Accept(writer);
                return Coral::String::New(buffer.GetString());
            }

            // Helper to get the global BehaviorContext, returning nullptr
            // if it isn't up yet (called during ComponentApplication
            // teardown, etc.).
            AZ::BehaviorContext* GetBehaviorContext()
            {
                AZ::BehaviorContext* ctx = nullptr;
                AZ::ComponentApplicationBus::BroadcastResult(
                    ctx, &AZ::ComponentApplicationRequests::GetBehaviorContext);
                return ctx;
            }
        } // anonymous namespace

        // -------------------------------------------------------------
        // Real implementations of the Phase 18 entry points.
        // -------------------------------------------------------------

        Coral::String InvokeStaticMethod(Coral::String className, Coral::String methodName, Coral::String argsJson)
        {
            std::string classNameStr(className);
            std::string methodNameStr(methodName);
            const AZStd::string contextLabel = AZStd::string::format(
                "InvokeStaticMethod('%s', '%s')", classNameStr.c_str(), methodNameStr.c_str());

            auto* ctx = GetBehaviorContext();
            if (ctx == nullptr)
            {
                AZStd::string msg = AZStd::string::format(
                    "{\"error\":\"%s: no BehaviorContext\"}", contextLabel.c_str());
                return Coral::String::New(msg.c_str());
            }

            auto classIt = ctx->m_classes.find(classNameStr.c_str());
            if (classIt == ctx->m_classes.end())
            {
                AZStd::string msg = AZStd::string::format(
                    "{\"error\":\"%s: class not reflected\"}", contextLabel.c_str());
                return Coral::String::New(msg.c_str());
            }
            AZ::BehaviorClass* cls = classIt->second;

            auto methodIt = cls->m_methods.find(methodNameStr.c_str());
            if (methodIt == cls->m_methods.end())
            {
                AZStd::string msg = AZStd::string::format(
                    "{\"error\":\"%s: method not reflected on class\"}", contextLabel.c_str());
                return Coral::String::New(msg.c_str());
            }
            AZ::BehaviorMethod* method = methodIt->second;
            if (method->IsMember())
            {
                AZStd::string msg = AZStd::string::format(
                    "{\"error\":\"%s: method is an instance member; use InvokeInstanceMethod\"}",
                    contextLabel.c_str());
                return Coral::String::New(msg.c_str());
            }

            return CallBehaviorMethodAndMarshalResult(method, contextLabel.c_str(), argsJson);
        }

        Coral::String InvokeInstanceMethod(Coral::String className, Coral::String methodName, int64_t instanceHandle, Coral::String argsJson)
        {
            AZ_UNUSED(argsJson);  // consumed once the handle table lands
            std::string classNameStr(className);
            std::string methodNameStr(methodName);
            // Instance dispatch requires a live BehaviorObject pointer
            // identified by instanceHandle. Phase 18 doesn't ship the
            // handle->BehaviorObject map yet (CreateInstance + a
            // dispatcher-owned table), so this returns a clearer error
            // than the pre-18 stub did. Once the handle table lands,
            // this becomes:
            //   1) look up BehaviorObject by instanceHandle
            //   2) prepend it as the implicit `this` arg
            //   3) call CallBehaviorMethodAndMarshalResult
            // Until then, callers should drive instance methods through
            // EBus events (which carry the entity id), or wait for the
            // handle table to land.
            AZStd::string msg = AZStd::string::format(
                "{\"error\":\"InvokeInstanceMethod('%s', '%s', handle=%lld): "
                "instance handle table not yet implemented - drive through EBus events instead\"}",
                classNameStr.c_str(), methodNameStr.c_str(),
                static_cast<long long>(instanceHandle));
            return Coral::String::New(msg.c_str());
        }

        Coral::String InvokeGlobalMethod(Coral::String methodName, Coral::String argsJson)
        {
            std::string methodNameStr(methodName);
            const AZStd::string contextLabel = AZStd::string::format(
                "InvokeGlobalMethod('%s')", methodNameStr.c_str());

            auto* ctx = GetBehaviorContext();
            if (ctx == nullptr)
            {
                AZStd::string msg = AZStd::string::format(
                    "{\"error\":\"%s: no BehaviorContext\"}", contextLabel.c_str());
                return Coral::String::New(msg.c_str());
            }

            auto methodIt = ctx->m_methods.find(methodNameStr.c_str());
            if (methodIt == ctx->m_methods.end())
            {
                AZStd::string msg = AZStd::string::format(
                    "{\"error\":\"%s: global method not reflected\"}", contextLabel.c_str());
                return Coral::String::New(msg.c_str());
            }
            AZ::BehaviorMethod* method = methodIt->second;
            return CallBehaviorMethodAndMarshalResult(method, contextLabel.c_str(), argsJson);
        }

        Coral::String GetProperty(Coral::String className, Coral::String propertyName, int64_t instanceHandle)
        {
            std::string classNameStr(className);
            std::string propertyNameStr(propertyName);
            AZStd::string msg = AZStd::string::format(
                "{\"error\":\"GetProperty('%s.%s', handle=%lld): "
                "instance handle table not yet implemented\"}",
                classNameStr.c_str(), propertyNameStr.c_str(),
                static_cast<long long>(instanceHandle));
            return Coral::String::New(msg.c_str());
        }

        bool SetProperty(Coral::String className, Coral::String propertyName, int64_t instanceHandle, Coral::String valueJson)
        {
            std::string classNameStr(className);
            std::string propertyNameStr(propertyName);
            AZ_UNUSED(valueJson);
            AZ_Warning("O3DESharp", false,
                "SetProperty('%s.%s', handle=%lld): instance handle table not yet implemented",
                classNameStr.c_str(), propertyNameStr.c_str(),
                static_cast<long long>(instanceHandle));
            return false;
        }

        Coral::String GetGlobalProperty(Coral::String propertyName)
        {
            std::string propertyNameStr(propertyName);
            const AZStd::string contextLabel = AZStd::string::format(
                "GetGlobalProperty('%s')", propertyNameStr.c_str());

            auto* ctx = GetBehaviorContext();
            if (ctx == nullptr)
            {
                AZStd::string msg = AZStd::string::format(
                    "{\"error\":\"%s: no BehaviorContext\"}", contextLabel.c_str());
                return Coral::String::New(msg.c_str());
            }

            auto propIt = ctx->m_properties.find(propertyNameStr.c_str());
            if (propIt == ctx->m_properties.end())
            {
                AZStd::string msg = AZStd::string::format(
                    "{\"error\":\"%s: global property not reflected\"}", contextLabel.c_str());
                return Coral::String::New(msg.c_str());
            }
            AZ::BehaviorProperty* prop = propIt->second;
            if (prop->m_getter == nullptr)
            {
                AZStd::string msg = AZStd::string::format(
                    "{\"error\":\"%s: property has no getter\"}", contextLabel.c_str());
                return Coral::String::New(msg.c_str());
            }
            // No args for a property getter; the empty argsJson string
            // exercises the no-args path inside the helper.
            return CallBehaviorMethodAndMarshalResult(prop->m_getter, contextLabel.c_str(), Coral::String::New(""));
        }

        bool SetGlobalProperty(Coral::String propertyName, Coral::String valueJson)
        {
            std::string propertyNameStr(propertyName);

            auto* ctx = GetBehaviorContext();
            if (ctx == nullptr)
            {
                AZ_Warning("O3DESharp", false,
                    "SetGlobalProperty('%s'): no BehaviorContext", propertyNameStr.c_str());
                return false;
            }
            auto propIt = ctx->m_properties.find(propertyNameStr.c_str());
            if (propIt == ctx->m_properties.end())
            {
                AZ_Warning("O3DESharp", false,
                    "SetGlobalProperty('%s'): not reflected", propertyNameStr.c_str());
                return false;
            }
            AZ::BehaviorProperty* prop = propIt->second;
            if (prop->m_setter == nullptr)
            {
                AZ_Warning("O3DESharp", false,
                    "SetGlobalProperty('%s'): no setter (readonly)", propertyNameStr.c_str());
                return false;
            }

            // Setter signature is (newValue) - wrap the JSON value in an
            // array so MarshalJsonArrayToArguments treats it as one arg.
            const AZStd::string contextLabel = AZStd::string::format(
                "SetGlobalProperty('%s')", propertyNameStr.c_str());
            std::string valueJsonStr(valueJson);
            AZStd::string wrappedJson = AZStd::string::format("[%s]",
                valueJsonStr.empty() ? "null" : valueJsonStr.c_str());
            Coral::String wrapped = Coral::String::New(wrappedJson.c_str());
            Coral::String result = CallBehaviorMethodAndMarshalResult(
                prop->m_setter, contextLabel.c_str(), wrapped);
            // Setter is void-returning; "ok":true on success. We treat
            // any error envelope as failure.
            std::string resultStr(result);
            return resultStr.find("\"ok\"") != std::string::npos;
        }

        Coral::String BroadcastEBusEvent(Coral::String busName, Coral::String eventName, Coral::String argsJson)
        {
            return DispatchEBusEvent(busName, eventName, argsJson, /*addressed*/ false, /*busId*/ 0u);
        }

        Coral::String SendEBusEvent(Coral::String busName, Coral::String eventName, int64_t address, Coral::String argsJson)
        {
            return DispatchEBusEvent(
                busName, eventName, argsJson,
                /*addressed*/ true,
                static_cast<AZ::u64>(address));
        }

        int64_t CreateInstance(Coral::String className, Coral::String argsJson)
        {
            AZ_UNUSED(argsJson);
            
            if (!s_dispatcherInstance)
            {
                return 0;
            }

            std::string classNameStr(className);
            AZStd::vector<MarshalledValue> args; // Empty for default constructor
            
            DispatchResult result = s_dispatcherInstance->CreateInstance(classNameStr.c_str(), args);
            if (result.success && result.returnValue.type == ReflectedParameter::MarshalType::Object)
            {
                return reinterpret_cast<int64_t>(result.returnValue.objectHandle);
            }

            return 0;
        }

        void DestroyInstance(Coral::String className, int64_t instanceHandle)
        {
            if (!s_dispatcherInstance || instanceHandle == 0)
            {
                return;
            }

            std::string classNameStr(className);
            s_dispatcherInstance->DestroyInstance(classNameStr.c_str(), reinterpret_cast<void*>(instanceHandle));
        }
    }

} // namespace O3DESharp
