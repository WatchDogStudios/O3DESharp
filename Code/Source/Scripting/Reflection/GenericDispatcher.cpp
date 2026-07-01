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
#include <AzCore/Math/Vector2.h>
#include <AzCore/Math/Vector3.h>
#include <AzCore/Math/Vector4.h>
#include <AzCore/Math/Quaternion.h>
#include <AzCore/Math/Transform.h>
#include <AzCore/Math/Color.h>
#include <AzCore/Math/Aabb.h>
#include <AzCore/Math/Matrix3x3.h>
#include <AzCore/Math/Matrix4x4.h>
#include <AzCore/Math/Crc.h>
#include <AzCore/Math/Uuid.h>
#include <AzCore/Component/EntityId.h>
#include <AzCore/JSON/rapidjson.h>
#include <AzCore/JSON/document.h>
#include <AzCore/JSON/stringbuffer.h>
#include <AzCore/JSON/writer.h>
#include <AzCore/std/parallel/mutex.h>
#include <AzCore/std/containers/unordered_map.h>
#include <AzCore/std/smart_ptr/unique_ptr.h>
#include <AzCore/std/smart_ptr/make_shared.h>
#include <AzCore/RTTI/BehaviorContext.h>

#include <Scripting/CoralHostManager.h>
#include <Coral/Type.hpp>

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

        // Phase 18-E2: managed EBus handler authoring. RegisterEBusHandler
        // spins up a BehaviorEBusHandler with a generic hook that forwards
        // every event back into managed via Coral.
        assembly->AddInternalCall("O3DE.Reflection.ReflectionInternalCalls", "Reflection_RegisterEBusHandler", reinterpret_cast<void*>(&GenericDispatcherInternalCalls::RegisterEBusHandler));
        assembly->AddInternalCall("O3DE.Reflection.ReflectionInternalCalls", "Reflection_UnregisterEBusHandler", reinterpret_cast<void*>(&GenericDispatcherInternalCalls::UnregisterEBusHandler));
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

        // Phase 18-E2: managed EBus handler bridge entry points.
        int64_t RegisterEBusHandler(Coral::String busName, uint64_t address, int64_t managedToken);
        void UnregisterEBusHandler(int64_t managedToken);

        namespace
        {
            // Storage requirements for a reflected BehaviorContext type.
            // BehaviorMethod::Call(result*) writes the return value
            // through result->m_value via operator=, which assumes
            // m_value already points at storage sized to fit the actual
            // return type. A default-constructed BehaviorArgument has
            // m_value == nullptr, so the first dispatcher that tries
            // to write into it crashes with a null-deref. Phase 18-A's
            // result-marshal path now allocates storage explicitly,
            // and this helper is how we know how much to allocate.
            struct ResultStorageRequirements
            {
                size_t size = 0;
                size_t alignment = 0;
            };

            // Returns size + alignment for any TypeId BehaviorContext
            // can return. Hardcoded primitive table first because
            // primitives are not always registered as BehaviorClasses;
            // falls back to ctx->m_typeToClassMap for everything
            // user-reflected. Returns {0, 0} only for types neither
            // we nor BehaviorContext recognize - the caller fails
            // the dispatch with a clear error in that case rather than
            // guess at a fallback size.
            static ResultStorageRequirements ComputeResultStorageRequirements(
                const AZ::Uuid& typeId, AZ::BehaviorContext* ctx)
            {
                ResultStorageRequirements r;
                auto match = [&](const AZ::Uuid& t, size_t sz, size_t al)
                {
                    if (typeId == t) { r.size = sz; r.alignment = al; return true; }
                    return false;
                };

                // Primitives - shape mirrors BehaviorContextMarshaling.cpp.
                if (match(azrtti_typeid<bool>(),    sizeof(bool),    alignof(bool)))    return r;
                if (match(azrtti_typeid<AZ::s8>(),  sizeof(AZ::s8),  alignof(AZ::s8)))  return r;
                if (match(azrtti_typeid<AZ::u8>(),  sizeof(AZ::u8),  alignof(AZ::u8)))  return r;
                if (match(azrtti_typeid<AZ::s16>(), sizeof(AZ::s16), alignof(AZ::s16))) return r;
                if (match(azrtti_typeid<AZ::u16>(), sizeof(AZ::u16), alignof(AZ::u16))) return r;
                if (match(azrtti_typeid<AZ::s32>(), sizeof(AZ::s32), alignof(AZ::s32))) return r;
                if (match(azrtti_typeid<AZ::u32>(), sizeof(AZ::u32), alignof(AZ::u32))) return r;
                if (match(azrtti_typeid<AZ::s64>(), sizeof(AZ::s64), alignof(AZ::s64))) return r;
                if (match(azrtti_typeid<AZ::u64>(), sizeof(AZ::u64), alignof(AZ::u64))) return r;
                if (match(azrtti_typeid<float>(),   sizeof(float),   alignof(float)))   return r;
                if (match(azrtti_typeid<double>(),  sizeof(double),  alignof(double)))  return r;

                // O3DE math + identifier types - all trivially
                // copyable, so operator= into zero-initialised
                // storage is safe.
                if (match(azrtti_typeid<AZ::Vector2>(),    sizeof(AZ::Vector2),    alignof(AZ::Vector2)))    return r;
                if (match(azrtti_typeid<AZ::Vector3>(),    sizeof(AZ::Vector3),    alignof(AZ::Vector3)))    return r;
                if (match(azrtti_typeid<AZ::Vector4>(),    sizeof(AZ::Vector4),    alignof(AZ::Vector4)))    return r;
                if (match(azrtti_typeid<AZ::Quaternion>(), sizeof(AZ::Quaternion), alignof(AZ::Quaternion))) return r;
                if (match(azrtti_typeid<AZ::Color>(),      sizeof(AZ::Color),      alignof(AZ::Color)))      return r;
                if (match(azrtti_typeid<AZ::Aabb>(),       sizeof(AZ::Aabb),       alignof(AZ::Aabb)))       return r;
                if (match(azrtti_typeid<AZ::Matrix3x3>(),  sizeof(AZ::Matrix3x3),  alignof(AZ::Matrix3x3)))  return r;
                if (match(azrtti_typeid<AZ::Matrix4x4>(),  sizeof(AZ::Matrix4x4),  alignof(AZ::Matrix4x4)))  return r;
                if (match(azrtti_typeid<AZ::Transform>(),  sizeof(AZ::Transform),  alignof(AZ::Transform)))  return r;
                if (match(azrtti_typeid<AZ::EntityId>(),   sizeof(AZ::EntityId),   alignof(AZ::EntityId)))   return r;
                if (match(azrtti_typeid<AZ::Crc32>(),      sizeof(AZ::Crc32),      alignof(AZ::Crc32)))      return r;
                if (match(azrtti_typeid<AZ::Uuid>(),       sizeof(AZ::Uuid),       alignof(AZ::Uuid)))       return r;

                // Strings and user-reflected types we look up in the
                // BehaviorContext registry. Note non-trivial types
                // (AZStd::string) will need destructor-after-marshal
                // tracking when we add return-marshaling for them;
                // for now, this branch is reached only for trivially
                // copyable user POD types reflected with size in
                // BehaviorClass.
                if (ctx != nullptr)
                {
                    auto it = ctx->m_typeToClassMap.find(typeId);
                    if (it != ctx->m_typeToClassMap.end() && it->second != nullptr)
                    {
                        r.size = it->second->m_size;
                        r.alignment = it->second->m_alignment;
                    }
                }
                return r;
            }

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

                // Heap fallback for return values that don't fit in
                // BehaviorArgument::m_tempData (a 32-byte static
                // buffer). Sized below once we know the return type.
                AZStd::vector<AZ::u8> resultHeapBuffer;

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
                    const AZ::BehaviorParameter* resultParam = method->GetResult();
                    if (resultParam == nullptr)
                    {
                        return makeError(
                            "method '%s.%s' has HasResult()==true but GetResult() returned null",
                            busNameStr.c_str(), eventNameStr.c_str());
                    }
                    result.m_typeId = resultParam->m_typeId;
                    result.m_name = resultParam->m_name;
                    result.m_traits = resultParam->m_traits;
                    result.m_azRtti = resultParam->m_azRtti;

                    // Allocate storage for the return value. Without
                    // this step the dispatcher's operator= path
                    // (AZ::SetResult::Set in BehaviorContext.h) writes
                    // to result.m_value, which is null on a default-
                    // constructed BehaviorArgument - crash inside
                    // AzCore.dll instead of a clean error here.
                    const auto storage = ComputeResultStorageRequirements(
                        resultParam->m_typeId, behaviorContext);
                    if (storage.size == 0)
                    {
                        return makeError(
                            "result type 0x%s for '%s.%s' has unknown storage requirements; "
                            "neither a primitive nor a reflected BehaviorClass",
                            resultParam->m_typeId.ToString<AZStd::string>().c_str(),
                            busNameStr.c_str(), eventNameStr.c_str());
                    }

                    if (storage.size <= 32)
                    {
                        // Fits in BehaviorArgument's inline 32-byte
                        // m_tempData buffer - free path, no heap.
                        result.m_value = result.m_tempData.allocate(
                            storage.size, storage.alignment, /*flags*/ 0);
                    }
                    else
                    {
                        // Heap fallback for AZ::Matrix3x3 / Matrix4x4 /
                        // Transform / large user types. Over-allocate
                        // by alignment so we can align up inside the
                        // buffer; resultHeapBuffer outlives the Call
                        // so the storage stays valid through the
                        // marshal-to-JSON step below.
                        resultHeapBuffer.resize(storage.size + storage.alignment);
                        result.m_value = AZ::PointerAlignUp(
                            resultHeapBuffer.data(), storage.alignment);
                    }
                    if (result.m_value == nullptr)
                    {
                        return makeError(
                            "result storage allocation returned null for type 0x%s ('%s.%s', size=%zu align=%zu)",
                            resultParam->m_typeId.ToString<AZStd::string>().c_str(),
                            busNameStr.c_str(), eventNameStr.c_str(),
                            storage.size, storage.alignment);
                    }

                    // Zero the buffer so a result-bus event with no
                    // connected handlers (the dispatcher skips writing
                    // in that case) marshals back a deterministic
                    // zero value instead of stack garbage. For trivially
                    // copyable types (every primitive + every math type
                    // in the table above), zero is a valid initial state
                    // that operator= can safely overwrite.
                    memset(result.m_value, 0, storage.size);
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
            // ============================================================
            // Phase 18-C: NativeObject handle table.
            // ============================================================
            // Maps opaque int64 handles (the int64_t the C# NativeObject
            // wrapper carries) to the live (void*, AZ::BehaviorClass*)
            // pair the BehaviorContext machinery needs to call an
            // instance method, get/set a property, or destruct.
            //
            // Why a table instead of "pointer-as-int64" (the pre-Phase-
            // 18-C trick): pointer-as-int64 has no validation - a stale
            // handle from a destroyed object dereferences garbage and
            // either silently corrupts memory or crashes. The table
            // gives us:
            //   - monotonic ids (handle reuse only happens after the
            //     u64 counter wraps, i.e. never in practice)
            //   - typed lookups (we know the class behind every handle)
            //   - explicit Release so destruction is observable
            //   - thread-safety via a single shared mutex
            //
            // Handle 0 is reserved for "invalid" (mirrors the C#
            // NativeObject.IsValid convention).
            struct InstanceEntry
            {
                void* address = nullptr;           // raw pointer to the constructed object
                AZ::BehaviorClass* behaviorClass = nullptr; // for method/property lookup + dtor
                AZStd::string className;           // mirrors what C# carries for error messages
            };

            class InstanceHandleTable
            {
            public:
                int64_t Register(void* address, AZ::BehaviorClass* cls, const AZStd::string& className)
                {
                    AZStd::lock_guard<AZStd::mutex> lock(m_mutex);
                    const int64_t handle = ++m_nextHandle;
                    m_entries.emplace(handle, InstanceEntry{address, cls, className});
                    return handle;
                }

                // Returns a copy so the caller doesn't have to hold the
                // mutex past the lookup. The returned address remains
                // valid until someone calls Release(handle).
                bool Lookup(int64_t handle, InstanceEntry& outEntry) const
                {
                    AZStd::lock_guard<AZStd::mutex> lock(m_mutex);
                    auto it = m_entries.find(handle);
                    if (it == m_entries.end()) return false;
                    outEntry = it->second;
                    return true;
                }

                // Removes the entry and returns the snapshot so the
                // caller can run the C++ destructor + deallocator
                // outside the lock (those may re-enter the dispatcher).
                bool Release(int64_t handle, InstanceEntry& outEntry)
                {
                    AZStd::lock_guard<AZStd::mutex> lock(m_mutex);
                    auto it = m_entries.find(handle);
                    if (it == m_entries.end()) return false;
                    outEntry = it->second;
                    m_entries.erase(it);
                    return true;
                }

                void Clear()
                {
                    AZStd::lock_guard<AZStd::mutex> lock(m_mutex);
                    m_entries.clear();
                }

                size_t Size() const
                {
                    AZStd::lock_guard<AZStd::mutex> lock(m_mutex);
                    return m_entries.size();
                }

            private:
                mutable AZStd::mutex m_mutex;
                AZStd::unordered_map<int64_t, InstanceEntry> m_entries;
                int64_t m_nextHandle = 0;  // first allocated is 1; 0 = invalid
            };

            // Single global table - the dispatcher is a process-wide
            // singleton (via s_dispatcherInstance) and the handle space
            // doesn't need to be per-context.
            static InstanceHandleTable s_instanceTable;

            // ============================================================
            // Phase 18-E2: Managed EBus handler bridge.
            // ============================================================
            // A ManagedEBusProxy wraps an AZ::BehaviorEBusHandler bound to
            // a specific bus and forwards every event the bus fires back
            // into managed code via Coral. The managed side keys events
            // by an opaque int64 token (the value
            // EBusHandlerRegistry.Register handed out); when an event
            // fires here, we marshal the event's BehaviorArguments into
            // a JSON args array, then invoke
            // O3DE.Reflection.EBusHandlerRegistry.DispatchEvent(token,
            // eventName, argsJson) through Coral's Coral::Type::
            // InvokeStaticMethod with the token as the first parameter.
            //
            // Lifecycle:
            //   - RegisterEBusHandler(busName, address, token):
            //       1. Look up bus in BehaviorContext.
            //       2. Call bus->m_createHandler to spawn a
            //          BehaviorEBusHandler.
            //       3. For each event on the handler, InstallGenericHook
            //          with our ForwardEvent thunk.
            //       4. Connect to the bus (with address for addressed
            //          buses, no-arg for broadcast-only).
            //       5. Stash the proxy in s_managedHandlers keyed by
            //          token.
            //   - UnregisterEBusHandler(token):
            //       1. Look up the proxy.
            //       2. Disconnect from bus.
            //       3. Call bus->m_destroyHandler to free.
            //       4. Erase from s_managedHandlers.
            //
            // Mirrors EditorPythonBindings' PythonProxyBus pattern -
            // we essentially have a "managed proxy bus" indexed by the
            // managed token rather than a Python object.
            struct ManagedEBusProxy
            {
                AZ::BehaviorEBus* bus = nullptr;
                AZ::BehaviorEBusHandler* handler = nullptr;
                int64_t managedToken = 0;

                // Perf: resolved once (lazily, on first event fire) and
                // reused for every subsequent event on this handler -
                // avoids re-doing CoralHostManager's string-keyed
                // m_coreTypeCache lookup (plus a fresh AZStd::string
                // construction from the literal) on every single event
                // dispatch. Populated by ForwardEventToManaged the first
                // time this proxy forwards an event; nullptr until then.
                Coral::Type* cachedRegistryType = nullptr;
            };

            class ManagedHandlerTable
            {
            public:
                bool Register(int64_t token, AZStd::unique_ptr<ManagedEBusProxy> proxy)
                {
                    AZStd::lock_guard<AZStd::mutex> lock(m_mutex);
                    if (m_entries.find(token) != m_entries.end()) return false;
                    m_entries.emplace(token, AZStd::move(proxy));
                    return true;
                }

                AZStd::unique_ptr<ManagedEBusProxy> Release(int64_t token)
                {
                    AZStd::lock_guard<AZStd::mutex> lock(m_mutex);
                    auto it = m_entries.find(token);
                    if (it == m_entries.end()) return nullptr;
                    auto proxy = AZStd::move(it->second);
                    m_entries.erase(it);
                    return proxy;
                }

                void Clear()
                {
                    AZStd::lock_guard<AZStd::mutex> lock(m_mutex);
                    m_entries.clear();
                }

                size_t Size() const
                {
                    AZStd::lock_guard<AZStd::mutex> lock(m_mutex);
                    return m_entries.size();
                }

            private:
                mutable AZStd::mutex m_mutex;
                AZStd::unordered_map<int64_t, AZStd::unique_ptr<ManagedEBusProxy>> m_entries;
            };

            static ManagedHandlerTable s_managedHandlers;

            // The generic hook for every event on a managed handler.
            // userData carries the managed token (encoded as void* via
            // a uintptr_t cast - tokens are int64 so this fits in a
            // pointer on 64-bit platforms; on 32-bit we'd need to box).
            //
            // The hook receives the AZ::BehaviorArguments for the
            // event - we marshal them into a JSON array via the
            // existing BehaviorArgumentToJsonValue helper, then call
            // back into managed via the static method
            //   O3DE.Reflection.EBusHandlerRegistry.DispatchEvent
            // The managed side returns a JSON envelope (an "ok" / "error"
            // / "result" object) which we currently ignore - the
            // existing bus dispatch flow has no slot for handler-
            // returned results since each handler returns independently
            // and the bus aggregates them. Future Phase 18-E3 could
            // wire this up for ResultEBus dispatches.
            void ForwardEventToManaged(
                void* userData,
                const char* eventName,
                int eventIndex,
                AZ::BehaviorArgument* /*result*/,
                int numParameters,
                AZ::BehaviorArgument* parameters)
            {
                AZ_UNUSED(eventIndex);
                ManagedEBusProxy* proxy = static_cast<ManagedEBusProxy*>(userData);
                if (proxy == nullptr || proxy->managedToken == 0) return;
                const int64_t token = proxy->managedToken;

                // Build the args JSON array. Each parameter goes through
                // BehaviorArgumentToJsonValue which handles all the
                // primitive + math marshaling we've already shipped.
                rapidjson::Document argsDoc;
                argsDoc.SetArray();
                for (int i = 0; i < numParameters; ++i)
                {
                    rapidjson::Value v;
                    AZStd::string marshalErr;
                    if (!Marshaling::BehaviorArgumentToJsonValue(
                            parameters[i], v, argsDoc.GetAllocator(), marshalErr))
                    {
                        AZ_Warning("O3DESharp", false,
                            "ForwardEventToManaged('%s'): arg[%d] marshal failed: %s - skipping handler call",
                            eventName, i, marshalErr.c_str());
                        return;
                    }
                    argsDoc.PushBack(v, argsDoc.GetAllocator());
                }

                rapidjson::StringBuffer buffer;
                rapidjson::Writer<rapidjson::StringBuffer> writer(buffer);
                argsDoc.Accept(writer);
                const char* argsJson = buffer.GetString();

                // Resolve EBusHandlerRegistry's Coral::Type once per
                // registration and cache it on the proxy. Previously this
                // called CoralHostManager::GetCoreType (a string-keyed
                // hashmap lookup plus a fresh AZStd::string construction
                // from the "O3DE.Reflection.EBusHandlerRegistry" literal)
                // on EVERY event fire, even though the type can never
                // change once resolved for a given handler registration.
                Coral::Type* registryType = proxy->cachedRegistryType;
                if (registryType == nullptr)
                {
                    ICoralHostManager* hostMgr = CoralHostManagerInterface::Get();
                    if (hostMgr == nullptr)
                    {
                        AZ_Warning("O3DESharp", false,
                            "ForwardEventToManaged('%s'): CoralHostManager unavailable; dropping event",
                            eventName);
                        return;
                    }
                    registryType = hostMgr->GetCoreType("O3DE.Reflection.EBusHandlerRegistry");
                    if (registryType == nullptr)
                    {
                        AZ_Warning("O3DESharp", false,
                            "ForwardEventToManaged('%s'): O3DE.Reflection.EBusHandlerRegistry type not found; dropping event",
                            eventName);
                        return;
                    }
                    proxy->cachedRegistryType = registryType;
                }
                // DispatchEvent(long token, string eventName, string argsJson) -> string?
                // Coral::Type::InvokeStaticMethod marshals string + long
                // arguments via its built-in primitive support.
                Coral::ScopedString eventNameStr = Coral::String::New(eventName);
                Coral::ScopedString argsJsonStr = Coral::String::New(argsJson);
                try
                {
                    registryType->InvokeStaticMethod(
                        "DispatchEvent", token, eventNameStr, argsJsonStr);
                }
                catch (...)
                {
                    AZ_Warning("O3DESharp", false,
                        "ForwardEventToManaged('%s'): exception from managed DispatchEvent; "
                        "event dropped",
                        eventName);
                }
            }

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
            template<typename ContextLabelFn>
            Coral::String CallBehaviorMethodAndMarshalResult(
                AZ::BehaviorMethod* method,
                const ContextLabelFn& contextLabelFn,
                Coral::String argsJson)
            {
                std::string argsJsonStr(argsJson);

                // contextLabelFn is a zero-argument callable returning
                // AZStd::string (the call sites pass a
                // `auto contextLabel = [&]() -> AZStd::string { ... };`
                // lambda). It is only invoked here, inside makeError, so a
                // caller whose behavior method call succeeds never pays the
                // AZStd::string::format cost of building its diagnostic
                // label at all - this is the whole point of this task.
                auto makeError = [&](const char* fmt, ...) -> Coral::String
                {
                    va_list args;
                    va_start(args, fmt);
                    char buf[1024] = {};
                    azvsnprintf(buf, sizeof(buf), fmt, args);
                    va_end(args);
                    const AZStd::string label = contextLabelFn();
                    AZ_Warning("O3DESharp", false, "%s: %s", label.c_str(), buf);
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
            // Deferred: only formatted when an error branch below actually
            // needs it, or once (unavoidably) on the path that proceeds to
            // CallBehaviorMethodAndMarshalResult - but that helper is now a
            // template accepting this lambda directly, so even THAT call
            // never formats the string unless ITS OWN internal error path
            // fires. A fully successful call therefore never pays the
            // AZStd::string::format cost at all.
            auto contextLabel = [&]() -> AZStd::string
            {
                return AZStd::string::format(
                    "InvokeStaticMethod('%s', '%s')", classNameStr.c_str(), methodNameStr.c_str());
            };

            auto* ctx = GetBehaviorContext();
            if (ctx == nullptr)
            {
                AZStd::string msg = AZStd::string::format(
                    "{\"error\":\"%s: no BehaviorContext\"}", contextLabel().c_str());
                return Coral::String::New(msg.c_str());
            }

            auto classIt = ctx->m_classes.find(classNameStr.c_str());
            if (classIt == ctx->m_classes.end())
            {
                AZStd::string msg = AZStd::string::format(
                    "{\"error\":\"%s: class not reflected\"}", contextLabel().c_str());
                return Coral::String::New(msg.c_str());
            }
            AZ::BehaviorClass* cls = classIt->second;

            auto methodIt = cls->m_methods.find(methodNameStr.c_str());
            if (methodIt == cls->m_methods.end())
            {
                AZStd::string msg = AZStd::string::format(
                    "{\"error\":\"%s: method not reflected on class\"}", contextLabel().c_str());
                return Coral::String::New(msg.c_str());
            }
            AZ::BehaviorMethod* method = methodIt->second;
            if (method->IsMember())
            {
                AZStd::string msg = AZStd::string::format(
                    "{\"error\":\"%s: method is an instance member; use InvokeInstanceMethod\"}",
                    contextLabel().c_str());
                return Coral::String::New(msg.c_str());
            }

            return CallBehaviorMethodAndMarshalResult(method, contextLabel, argsJson);
        }

        Coral::String InvokeInstanceMethod(Coral::String className, Coral::String methodName, int64_t instanceHandle, Coral::String argsJson)
        {
            std::string classNameStr(className);
            std::string methodNameStr(methodName);
            // Deferred: only formatted when actually read below (an early
            // validation branch, the class-name-mismatch diagnostic, or
            // makeError's own AZ_Warning) - this function's fully
            // successful path never touches it, so a lazy lambda avoids
            // formatting a label that would otherwise be discarded.
            auto contextLabel = [&]() -> AZStd::string
            {
                return AZStd::string::format(
                    "InvokeInstanceMethod('%s', '%s', handle=%lld)",
                    classNameStr.c_str(), methodNameStr.c_str(),
                    static_cast<long long>(instanceHandle));
            };

            // Resolve the handle. Empty/stale handle -> immediate error
            // with the entry-point label, so the C# call site can see
            // exactly which instance failed.
            InstanceEntry entry;
            if (!s_instanceTable.Lookup(instanceHandle, entry))
            {
                AZStd::string msg = AZStd::string::format(
                    "{\"error\":\"%s: instance handle not found in table (already destroyed or never allocated)\"}",
                    contextLabel().c_str());
                return Coral::String::New(msg.c_str());
            }
            if (entry.behaviorClass == nullptr || entry.address == nullptr)
            {
                AZStd::string msg = AZStd::string::format(
                    "{\"error\":\"%s: instance entry corrupted (class or address null)\"}",
                    contextLabel().c_str());
                return Coral::String::New(msg.c_str());
            }

            // Optional sanity check - if the caller's className doesn't
            // match the class the handle was registered under, we have
            // either a generator bug or a caller bug. Don't fail the
            // dispatch (the handle's recorded class is the source of
            // truth), but log it so the next person debugging knows.
            if (!classNameStr.empty() && strcmp(classNameStr.c_str(), entry.className.c_str()) != 0)
            {
                AZ_Warning("O3DESharp", false,
                    "%s: caller's class name '%s' differs from handle's registered class '%s' "
                    "- proceeding with handle's class",
                    contextLabel().c_str(), classNameStr.c_str(), entry.className.c_str());
            }

            auto methodIt = entry.behaviorClass->m_methods.find(methodNameStr.c_str());
            if (methodIt == entry.behaviorClass->m_methods.end())
            {
                AZStd::string msg = AZStd::string::format(
                    "{\"error\":\"%s: method not reflected on class '%s'\"}",
                    contextLabel().c_str(), entry.className.c_str());
                return Coral::String::New(msg.c_str());
            }
            AZ::BehaviorMethod* method = methodIt->second;
            if (!method->IsMember())
            {
                AZStd::string msg = AZStd::string::format(
                    "{\"error\":\"%s: method is static; use InvokeStaticMethod\"}",
                    contextLabel().c_str());
                return Coral::String::New(msg.c_str());
            }

            // Prepend the `this` BehaviorArgument to the args JSON.
            // BehaviorMethod::Call expects member functions to receive
            // the instance as arg[0]. We synthesize that argument
            // directly (no JSON round-trip) by building a
            // BehaviorArgument pointing at the live address with the
            // class's TypeId; then we let CallBehaviorMethodAndMarshalResult
            // marshal the remaining params from the JSON array.
            //
            // The helper currently doesn't have a "leading argument"
            // hook - we cheat by doing the call inline here. Refactor
            // candidate: thread a `BehaviorArgument* leading` through
            // CallBehaviorMethodAndMarshalResult so EBus addressed
            // dispatch (which also has a leading arg, the bus id) can
            // reuse the same path.
            std::string argsJsonStr(argsJson);

            auto makeError = [&](const char* fmt, ...) -> Coral::String
            {
                va_list vargs;
                va_start(vargs, fmt);
                char buf[1024] = {};
                azvsnprintf(buf, sizeof(buf), fmt, vargs);
                va_end(vargs);
                AZ_Warning("O3DESharp", false, "%s: %s", contextLabel().c_str(), buf);
                AZStd::string m = AZStd::string::format("{\"error\":\"%s\"}", buf);
                return Coral::String::New(m.c_str());
            };

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
            // skipFront=1 because arg 0 is the `this` we're about to
            // synthesize - we don't expect the JSON to provide it.
            if (!Marshaling::MarshalJsonArrayToArguments(
                    argsDoc, *method, dispatchArgs,
                    marshalAlloc, marshalError, /*skipFront=*/1))
            {
                return makeError("arg marshal: %s", marshalError.c_str());
            }

            // Build the leading `this` arg from the registered handle entry.
            AZ::BehaviorArgument thisArg;
            thisArg.m_value = entry.address;
            thisArg.m_typeId = entry.behaviorClass->m_typeId;
            thisArg.m_traits = AZ::BehaviorParameter::TR_POINTER;

            AZStd::vector<AZ::BehaviorArgument> flatArgs;
            flatArgs.reserve(dispatchArgs.size() + 1);
            flatArgs.push_back(thisArg);
            for (auto& a : dispatchArgs)
            {
                flatArgs.push_back(a);
            }

            AZ::BehaviorArgument result;
            const bool hasReturn = method->HasResult();
            if (hasReturn)
            {
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

        Coral::String InvokeGlobalMethod(Coral::String methodName, Coral::String argsJson)
        {
            std::string methodNameStr(methodName);
            auto contextLabel = [&]() -> AZStd::string
            {
                return AZStd::string::format(
                    "InvokeGlobalMethod('%s')", methodNameStr.c_str());
            };

            auto* ctx = GetBehaviorContext();
            if (ctx == nullptr)
            {
                AZStd::string msg = AZStd::string::format(
                    "{\"error\":\"%s: no BehaviorContext\"}", contextLabel().c_str());
                return Coral::String::New(msg.c_str());
            }

            auto methodIt = ctx->m_methods.find(methodNameStr.c_str());
            if (methodIt == ctx->m_methods.end())
            {
                AZStd::string msg = AZStd::string::format(
                    "{\"error\":\"%s: global method not reflected\"}", contextLabel().c_str());
                return Coral::String::New(msg.c_str());
            }
            AZ::BehaviorMethod* method = methodIt->second;
            return CallBehaviorMethodAndMarshalResult(method, contextLabel, argsJson);
        }

        Coral::String GetProperty(Coral::String className, Coral::String propertyName, int64_t instanceHandle)
        {
            std::string classNameStr(className);
            std::string propertyNameStr(propertyName);
            auto contextLabel = [&]() -> AZStd::string
            {
                return AZStd::string::format(
                    "GetProperty('%s.%s', handle=%lld)",
                    classNameStr.c_str(), propertyNameStr.c_str(),
                    static_cast<long long>(instanceHandle));
            };

            InstanceEntry entry;
            if (!s_instanceTable.Lookup(instanceHandle, entry))
            {
                AZStd::string msg = AZStd::string::format(
                    "{\"error\":\"%s: instance handle not found\"}", contextLabel().c_str());
                return Coral::String::New(msg.c_str());
            }
            if (entry.behaviorClass == nullptr || entry.address == nullptr)
            {
                AZStd::string msg = AZStd::string::format(
                    "{\"error\":\"%s: instance entry corrupted\"}", contextLabel().c_str());
                return Coral::String::New(msg.c_str());
            }

            auto propIt = entry.behaviorClass->m_properties.find(propertyNameStr.c_str());
            if (propIt == entry.behaviorClass->m_properties.end())
            {
                AZStd::string msg = AZStd::string::format(
                    "{\"error\":\"%s: property not reflected on class '%s'\"}",
                    contextLabel().c_str(), entry.className.c_str());
                return Coral::String::New(msg.c_str());
            }
            AZ::BehaviorProperty* prop = propIt->second;
            if (prop->m_getter == nullptr)
            {
                AZStd::string msg = AZStd::string::format(
                    "{\"error\":\"%s: property has no getter\"}", contextLabel().c_str());
                return Coral::String::New(msg.c_str());
            }

            // The getter takes `this` as its only argument.
            AZ::BehaviorArgument thisArg;
            thisArg.m_value = entry.address;
            thisArg.m_typeId = entry.behaviorClass->m_typeId;
            thisArg.m_traits = AZ::BehaviorParameter::TR_POINTER;

            AZ::BehaviorArgument result;
            if (const auto* resultParam = prop->m_getter->GetResult())
            {
                result.m_typeId = resultParam->m_typeId;
                result.m_name = resultParam->m_name;
                result.m_traits = resultParam->m_traits;
            }
            const bool ok = prop->m_getter->Call(&thisArg, 1, &result);
            if (!ok)
            {
                AZStd::string msg = AZStd::string::format(
                    "{\"error\":\"%s: getter Call returned false\"}", contextLabel().c_str());
                return Coral::String::New(msg.c_str());
            }
            if (result.m_typeId.IsNull())
            {
                if (const auto* resultParam = prop->m_getter->GetResult())
                {
                    result.m_typeId = resultParam->m_typeId;
                }
            }

            rapidjson::Document resultDoc;
            resultDoc.SetObject();
            rapidjson::Value resultValue;
            AZStd::string resultError;
            if (!Marshaling::BehaviorArgumentToJsonValue(
                    result, resultValue, resultDoc.GetAllocator(), resultError))
            {
                AZStd::string msg = AZStd::string::format(
                    "{\"error\":\"%s: result marshal: %s\"}",
                    contextLabel().c_str(), resultError.c_str());
                return Coral::String::New(msg.c_str());
            }
            resultDoc.AddMember("result", resultValue, resultDoc.GetAllocator());
            rapidjson::StringBuffer buffer;
            rapidjson::Writer<rapidjson::StringBuffer> writer(buffer);
            resultDoc.Accept(writer);
            return Coral::String::New(buffer.GetString());
        }

        bool SetProperty(Coral::String className, Coral::String propertyName, int64_t instanceHandle, Coral::String valueJson)
        {
            std::string classNameStr(className);
            std::string propertyNameStr(propertyName);
            auto contextLabel = [&]() -> AZStd::string
            {
                return AZStd::string::format(
                    "SetProperty('%s.%s', handle=%lld)",
                    classNameStr.c_str(), propertyNameStr.c_str(),
                    static_cast<long long>(instanceHandle));
            };

            InstanceEntry entry;
            if (!s_instanceTable.Lookup(instanceHandle, entry))
            {
                AZ_Warning("O3DESharp", false, "%s: instance handle not found", contextLabel().c_str());
                return false;
            }
            if (entry.behaviorClass == nullptr || entry.address == nullptr)
            {
                AZ_Warning("O3DESharp", false, "%s: instance entry corrupted", contextLabel().c_str());
                return false;
            }
            auto propIt = entry.behaviorClass->m_properties.find(propertyNameStr.c_str());
            if (propIt == entry.behaviorClass->m_properties.end())
            {
                AZ_Warning("O3DESharp", false, "%s: property not reflected", contextLabel().c_str());
                return false;
            }
            AZ::BehaviorProperty* prop = propIt->second;
            if (prop->m_setter == nullptr)
            {
                AZ_Warning("O3DESharp", false, "%s: property has no setter (readonly)", contextLabel().c_str());
                return false;
            }

            // Setter signature is (this, newValue). Build the two-arg
            // call: synthesize `this`, marshal newValue from valueJson.
            AZ::BehaviorArgument thisArg;
            thisArg.m_value = entry.address;
            thisArg.m_typeId = entry.behaviorClass->m_typeId;
            thisArg.m_traits = AZ::BehaviorParameter::TR_POINTER;

            std::string valueJsonStr(valueJson);
            rapidjson::Document valueDoc;
            valueDoc.Parse(valueJsonStr.empty() ? "null" : valueJsonStr.c_str());
            if (valueDoc.HasParseError())
            {
                AZ_Warning("O3DESharp", false, "%s: value JSON parse error at offset %zu",
                    contextLabel().c_str(), valueDoc.GetErrorOffset());
                return false;
            }

            // Setter arg 0 is `this`, arg 1 is the new value's param.
            const AZ::BehaviorParameter* valueParam = prop->m_setter->GetArgument(1);
            if (valueParam == nullptr)
            {
                AZ_Warning("O3DESharp", false, "%s: setter has no value parameter (corrupted reflection)",
                    contextLabel().c_str());
                return false;
            }

            Marshaling::StackAllocator marshalAlloc;
            AZ::BehaviorArgument valueArg;
            AZStd::string marshalError;
            if (!Marshaling::JsonValueToBehaviorParameter(
                    valueDoc, *valueParam, valueArg, marshalAlloc, marshalError))
            {
                AZ_Warning("O3DESharp", false, "%s: value marshal: %s",
                    contextLabel().c_str(), marshalError.c_str());
                return false;
            }

            AZ::BehaviorArgument callArgs[2] = { thisArg, valueArg };
            const bool ok = prop->m_setter->Call(callArgs, 2, nullptr);
            if (!ok)
            {
                AZ_Warning("O3DESharp", false, "%s: setter Call returned false", contextLabel().c_str());
                return false;
            }
            return true;
        }

        Coral::String GetGlobalProperty(Coral::String propertyName)
        {
            std::string propertyNameStr(propertyName);
            auto contextLabel = [&]() -> AZStd::string
            {
                return AZStd::string::format(
                    "GetGlobalProperty('%s')", propertyNameStr.c_str());
            };

            auto* ctx = GetBehaviorContext();
            if (ctx == nullptr)
            {
                AZStd::string msg = AZStd::string::format(
                    "{\"error\":\"%s: no BehaviorContext\"}", contextLabel().c_str());
                return Coral::String::New(msg.c_str());
            }

            auto propIt = ctx->m_properties.find(propertyNameStr.c_str());
            if (propIt == ctx->m_properties.end())
            {
                AZStd::string msg = AZStd::string::format(
                    "{\"error\":\"%s: global property not reflected\"}", contextLabel().c_str());
                return Coral::String::New(msg.c_str());
            }
            AZ::BehaviorProperty* prop = propIt->second;
            if (prop->m_getter == nullptr)
            {
                AZStd::string msg = AZStd::string::format(
                    "{\"error\":\"%s: property has no getter\"}", contextLabel().c_str());
                return Coral::String::New(msg.c_str());
            }
            // No args for a property getter; the empty argsJson string
            // exercises the no-args path inside the helper.
            return CallBehaviorMethodAndMarshalResult(prop->m_getter, contextLabel, Coral::String::New(""));
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
            auto contextLabel = [&]() -> AZStd::string
            {
                return AZStd::string::format("SetGlobalProperty('%s')", propertyNameStr.c_str());
            };
            std::string valueJsonStr(valueJson);
            AZStd::string wrappedJson = AZStd::string::format("[%s]",
                valueJsonStr.empty() ? "null" : valueJsonStr.c_str());
            Coral::String wrapped = Coral::String::New(wrappedJson.c_str());
            Coral::String result = CallBehaviorMethodAndMarshalResult(
                prop->m_setter, contextLabel, wrapped);
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
            AZ_UNUSED(argsJson);  // constructor args - default-construct only for now

            if (!s_dispatcherInstance)
            {
                return 0;
            }

            std::string classNameStr(className);
            AZStd::vector<MarshalledValue> args;

            DispatchResult result = s_dispatcherInstance->CreateInstance(classNameStr.c_str(), args);
            if (!result.success || result.returnValue.type != ReflectedParameter::MarshalType::Object)
            {
                return 0;
            }

            // Look up the BehaviorClass so the handle table can later
            // resolve method/property/destructor calls without round-
            // tripping through the className string each time.
            auto* ctx = GetBehaviorContext();
            if (ctx == nullptr)
            {
                // Leak warning: we constructed an instance but can't
                // register it. The raw pointer is the only reference,
                // and the caller gets handle=0 which the C# side
                // treats as "construction failed", so it won't try
                // to destroy. This is a real leak but only fires
                // during ComponentApplication teardown - and at that
                // point the whole runtime is going down anyway.
                AZ_Warning("O3DESharp", false,
                    "CreateInstance('%s'): no BehaviorContext after construction; leaking instance",
                    classNameStr.c_str());
                return 0;
            }
            auto classIt = ctx->m_classes.find(classNameStr.c_str());
            AZ::BehaviorClass* behaviorClass = (classIt != ctx->m_classes.end()) ? classIt->second : nullptr;

            void* address = result.returnValue.objectHandle;
            const int64_t handle = s_instanceTable.Register(address, behaviorClass, AZStd::string(classNameStr.c_str()));
            return handle;
        }

        void DestroyInstance(Coral::String className, int64_t instanceHandle)
        {
            AZ_UNUSED(className);  // handle-recorded class is the source of truth
            if (!s_dispatcherInstance || instanceHandle == 0)
            {
                return;
            }

            InstanceEntry entry;
            if (!s_instanceTable.Release(instanceHandle, entry))
            {
                AZ_Warning("O3DESharp", false,
                    "DestroyInstance(handle=%lld): not in table (double-destroy or never allocated)",
                    static_cast<long long>(instanceHandle));
                return;
            }

            // Drive the existing dispatcher destructor + deallocator
            // through the registered class so any BehaviorClass-side
                // user-data hooks fire correctly. Outside the table
                // mutex so a re-entrant call from a destructor doesn't
                // self-deadlock.
            s_dispatcherInstance->DestroyInstance(entry.className.c_str(), entry.address);
        }

        // ========================================================
        // Phase 18-E2: Managed EBus handler registration.
        // ========================================================
        // Wired into the C# side as ReflectionInternalCalls.
        //   Reflection_RegisterEBusHandler / Reflection_UnregisterEBusHandler

        int64_t RegisterEBusHandler(Coral::String busName, uint64_t address, int64_t managedToken)
        {
            if (managedToken == 0)
            {
                return 0;
            }
            auto* ctx = GetBehaviorContext();
            if (ctx == nullptr)
            {
                AZ_Warning("O3DESharp", false,
                    "RegisterEBusHandler(token=%lld): no BehaviorContext available",
                    static_cast<long long>(managedToken));
                return 0;
            }

            std::string busNameStr(busName);
            auto busIt = ctx->m_ebuses.find(busNameStr.c_str());
            if (busIt == ctx->m_ebuses.end())
            {
                AZ_Warning("O3DESharp", false,
                    "RegisterEBusHandler(token=%lld): bus '%s' not reflected in BehaviorContext",
                    static_cast<long long>(managedToken), busNameStr.c_str());
                return 0;
            }
            AZ::BehaviorEBus* bus = busIt->second;

            if (bus->m_createHandler == nullptr || bus->m_destroyHandler == nullptr)
            {
                AZ_Warning("O3DESharp", false,
                    "RegisterEBusHandler(token=%lld): bus '%s' has no handler factory "
                    "(not all buses are reflected with handler support; only those that "
                    "explicitly call .Handler<HandlerT>() in their Reflect can be implemented "
                    "in script)",
                    static_cast<long long>(managedToken), busNameStr.c_str());
                return 0;
            }

            // Spawn the BehaviorEBusHandler via the bus's factory.
            // m_createHandler is a BehaviorMethod*; its return type is
            // a BehaviorEBusHandler*. Call with no args.
            AZ::BehaviorArgument handlerResult;
            if (const auto* resultParam = bus->m_createHandler->GetResult())
            {
                handlerResult.m_typeId = resultParam->m_typeId;
                handlerResult.m_traits = resultParam->m_traits;
            }
            if (!bus->m_createHandler->Call(nullptr, 0, &handlerResult))
            {
                AZ_Warning("O3DESharp", false,
                    "RegisterEBusHandler(token=%lld): bus '%s' m_createHandler->Call returned false",
                    static_cast<long long>(managedToken), busNameStr.c_str());
                return 0;
            }
            AZ::BehaviorEBusHandler* handler =
                *reinterpret_cast<AZ::BehaviorEBusHandler**>(handlerResult.m_value);
            if (handler == nullptr)
            {
                AZ_Warning("O3DESharp", false,
                    "RegisterEBusHandler(token=%lld): bus '%s' factory returned null handler",
                    static_cast<long long>(managedToken), busNameStr.c_str());
                return 0;
            }

            // Allocate the proxy up front (before installing hooks) so
            // its stable heap address can be handed to InstallGenericHook
            // as userData. The proxy carries the managed token AND (once
            // populated on first event fire) the cached Coral::Type* for
            // EBusHandlerRegistry, so ForwardEventToManaged never needs
            // to re-resolve the type by name after the first event.
            auto proxy = AZStd::make_unique<ManagedEBusProxy>();
            proxy->bus = bus;
            proxy->handler = handler;
            proxy->managedToken = managedToken;

            // Install our generic hook on every event the handler
            // exposes. userData carries a pointer to the ManagedEBusProxy
            // itself (stable for the proxy's lifetime - see
            // UnregisterEBusHandler, which moves the proxy out of
            // s_managedHandlers into a local via Release() BEFORE calling
            // handler->Disconnect(), so the proxy stays alive for as long
            // as the hook could still fire).
            void* userData = proxy.get();
            for (const auto& evt : handler->GetEvents())
            {
                if (!handler->InstallGenericHook(
                        evt.m_name, &ForwardEventToManaged, userData))
                {
                    AZ_Warning("O3DESharp", false,
                        "RegisterEBusHandler(token=%lld): InstallGenericHook failed for event '%s' on bus '%s'",
                        static_cast<long long>(managedToken), evt.m_name, busNameStr.c_str());
                    // Continue with the other events - partial hook
                    // installation is still useful.
                }
            }

            // Connect. For addressed buses we synthesize a
            // BehaviorArgument carrying the address; for broadcast-
            // only buses we pass nullptr.
            //
            // The static_cast disambiguates between
            //   virtual bool Connect(BehaviorArgument* id = nullptr)
            // and the sibling
            //   template<typename BusId> bool Connect(BusId id)
            // which otherwise wins overload resolution on a bare
            // `nullptr` (exact-match deduction beats the pointer
            // conversion) and tries to instantiate AzTypeInfo on
            // nullptr_t.
            bool connected = false;
            if (address != 0)
            {
                // Synthesize the address argument. We currently support
                // EntityId-shaped addresses (the most common case) and
                // u64 raw handles. Other address types (Crc32, strings)
                // need their own marshaling path - tracked as Phase
                // 18-E3 follow-up.
                AZ::BehaviorArgument addressArg;
                AZ::EntityId entityId(address);
                addressArg.Set<AZ::EntityId>(&entityId);
                connected = handler->Connect(&addressArg);
            }
            else
            {
                connected = handler->Connect(static_cast<AZ::BehaviorArgument*>(nullptr));
            }
            if (!connected)
            {
                AZ_Warning("O3DESharp", false,
                    "RegisterEBusHandler(token=%lld): handler->Connect returned false for bus '%s'",
                    static_cast<long long>(managedToken), busNameStr.c_str());
                bus->m_destroyHandler->Call(&handlerResult, 1, nullptr);
                return 0;
            }

            // Register in the managed-handler table so Unregister can
            // find it and disconnect cleanly.
            if (!s_managedHandlers.Register(managedToken, AZStd::move(proxy)))
            {
                AZ_Warning("O3DESharp", false,
                    "RegisterEBusHandler(token=%lld): duplicate token; rolling back",
                    static_cast<long long>(managedToken));
                handler->Disconnect();
                bus->m_destroyHandler->Call(&handlerResult, 1, nullptr);
                return 0;
            }

            // Return a non-zero confirmation handle. We return the
            // token itself; the C# side just checks != 0.
            return managedToken;
        }

        void UnregisterEBusHandler(int64_t managedToken)
        {
            if (managedToken == 0) return;
            auto proxy = s_managedHandlers.Release(managedToken);
            if (proxy == nullptr) return;

            if (proxy->handler != nullptr)
            {
                proxy->handler->Disconnect();
                if (proxy->bus != nullptr && proxy->bus->m_destroyHandler != nullptr)
                {
                    AZ::BehaviorArgument handlerArg;
                    handlerArg.Set<AZ::BehaviorEBusHandler*>(&proxy->handler);
                    proxy->bus->m_destroyHandler->Call(&handlerArg, 1, nullptr);
                }
            }
        }
    }

} // namespace O3DESharp
