/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

#include "GenericDispatcher.h"

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

        Coral::String InvokeStaticMethod(Coral::String className, Coral::String methodName, Coral::String argsJson)
        {
            AZ_UNUSED(argsJson);
            // TODO: Parse JSON args and invoke
            std::string classNameStr(className);
            std::string methodNameStr(methodName);
            return Coral::String::New(AZStd::string::format("{\"error\":\"Not fully implemented: %s.%s\"}", classNameStr.c_str(), methodNameStr.c_str()).c_str());
        }

        Coral::String InvokeInstanceMethod(Coral::String className, Coral::String methodName, int64_t instanceHandle, Coral::String argsJson)
        {
            AZ_UNUSED(instanceHandle);
            AZ_UNUSED(argsJson);
            std::string classNameStr(className);
            std::string methodNameStr(methodName);
            return Coral::String::New(AZStd::string::format("{\"error\":\"Not fully implemented: %s.%s\"}", classNameStr.c_str(), methodNameStr.c_str()).c_str());
        }

        Coral::String InvokeGlobalMethod(Coral::String methodName, Coral::String argsJson)
        {
            AZ_UNUSED(argsJson);
            std::string methodNameStr(methodName);
            return Coral::String::New(AZStd::string::format("{\"error\":\"Not fully implemented: %s\"}", methodNameStr.c_str()).c_str());
        }

        Coral::String GetProperty(Coral::String className, Coral::String propertyName, int64_t instanceHandle)
        {
            AZ_UNUSED(instanceHandle);
            std::string classNameStr(className);
            std::string propertyNameStr(propertyName);
            return Coral::String::New(AZStd::string::format("{\"error\":\"Not fully implemented: %s.%s\"}", classNameStr.c_str(), propertyNameStr.c_str()).c_str());
        }

        bool SetProperty(Coral::String className, Coral::String propertyName, int64_t instanceHandle, Coral::String valueJson)
        {
            AZ_UNUSED(className);
            AZ_UNUSED(propertyName);
            AZ_UNUSED(instanceHandle);
            AZ_UNUSED(valueJson);
            return false;
        }

        Coral::String GetGlobalProperty(Coral::String propertyName)
        {
            std::string propertyNameStr(propertyName);
            return Coral::String::New(AZStd::string::format("{\"error\":\"Not fully implemented: %s\"}", propertyNameStr.c_str()).c_str());
        }

        bool SetGlobalProperty(Coral::String propertyName, Coral::String valueJson)
        {
            AZ_UNUSED(propertyName);
            AZ_UNUSED(valueJson);
            return false;
        }

        Coral::String BroadcastEBusEvent(Coral::String busName, Coral::String eventName, Coral::String argsJson)
        {
            AZ_UNUSED(argsJson);
            std::string busNameStr(busName);
            std::string eventNameStr(eventName);
            return Coral::String::New(AZStd::string::format("{\"error\":\"Not fully implemented: %s.%s\"}", busNameStr.c_str(), eventNameStr.c_str()).c_str());
        }

        Coral::String SendEBusEvent(Coral::String busName, Coral::String eventName, int64_t address, Coral::String argsJson)
        {
            AZ_UNUSED(address);
            AZ_UNUSED(argsJson);
            std::string busNameStr(busName);
            std::string eventNameStr(eventName);
            return Coral::String::New(AZStd::string::format("{\"error\":\"Not fully implemented: %s.%s\"}", busNameStr.c_str(), eventNameStr.c_str()).c_str());
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
