/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

#pragma once

#include <AzCore/base.h>
#include <AzCore/Memory/SystemAllocator.h>
#include <AzCore/RTTI/RTTI.h>
#include <AzCore/RTTI/BehaviorContext.h>
#include <AzCore/std/string/string.h>
#include <AzCore/std/containers/vector.h>
#include <AzCore/std/any.h>

#include <Coral/String.hpp>
#include <Coral/Assembly.hpp>

#include "BehaviorContextReflector.h"

namespace O3DESharp
{
    /**
     * Represents a value that can be passed between C++ and C#
     * This is a variant type that can hold any marshallable value
     */
    struct MarshalledValue
    {
        ReflectedParameter::MarshalType type = ReflectedParameter::MarshalType::Void;
        
        union
        {
            bool boolValue;
            int8_t int8Value;
            int16_t int16Value;
            int32_t int32Value;
            int64_t int64Value;
            uint8_t uint8Value;
            uint16_t uint16Value;
            uint32_t uint32Value;
            uint64_t uint64Value;
            float floatValue;
            double doubleValue;
            
            // For complex types stored as raw bytes
            struct
            {
                float x, y, z;
            } vector3Value;
            
            struct
            {
                float x, y, z, w;
            } quaternionValue;
        };
        
        // String values are stored separately due to allocation
        AZStd::string stringValue;
        
        // For object types, we store a pointer to the managed handle
        void* objectHandle = nullptr;
        AZStd::string objectTypeName;

        MarshalledValue() : type(ReflectedParameter::MarshalType::Void), int64Value(0) {}
        
        // Convenience constructors
        static MarshalledValue FromBool(bool value);
        static MarshalledValue FromInt32(int32_t value);
        static MarshalledValue FromInt64(int64_t value);
        static MarshalledValue FromUInt64(uint64_t value);
        static MarshalledValue FromFloat(float value);
        static MarshalledValue FromDouble(double value);
        static MarshalledValue FromString(const AZStd::string& value);
        static MarshalledValue FromVector3(float x, float y, float z);
        static MarshalledValue FromQuaternion(float x, float y, float z, float w);
        static MarshalledValue FromEntityId(AZ::u64 entityId);
        static MarshalledValue FromObject(void* handle, const AZStd::string& typeName);
    };

    /**
     * Result of a dispatched method call
     */
    struct DispatchResult
    {
        bool success = false;
        AZStd::string errorMessage;
        MarshalledValue returnValue;
        
        static DispatchResult Success(const MarshalledValue& value = MarshalledValue());
        static DispatchResult Error(const AZStd::string& message);
    };

    /**
     * GenericDispatcher - Enables C# to invoke any reflected BehaviorContext method
     *
     * This class provides a generic entry point for managed code to call native O3DE
     * functions that have been reflected to the BehaviorContext. Instead of creating
     * individual bindings for every method, we use a single dispatcher that:
     *
     * 1. Looks up the method by class name and method name
     * 2. Marshals arguments from C# representations to C++ types
     * 3. Invokes the method via BehaviorMethod
     * 4. Marshals the return value back to C#
     *
     * This approach allows automatic support for any BehaviorContext-reflected API
     * without requiring manual binding code.
     */
    class GenericDispatcher
    {
    public:
        AZ_RTTI(GenericDispatcher, "{D2E3F4A5-B6C7-8901-CDEF-234567890ABC}");
        AZ_CLASS_ALLOCATOR(GenericDispatcher, AZ::SystemAllocator);

        GenericDispatcher() = default;
        ~GenericDispatcher() = default;

        /**
         * Initialize the dispatcher with the reflector
         * @param reflector The BehaviorContextReflector to use for lookups
         */
        void Initialize(BehaviorContextReflector* reflector);

        /**
         * Shutdown and release resources
         */
        void Shutdown();

        // ============================================================
        // Method Invocation
        // ============================================================

        /**
         * Invoke a static method on a class
         * @param className The name of the class
         * @param methodName The name of the method
         * @param arguments The marshalled arguments
         * @return The dispatch result with return value or error
         */
        DispatchResult InvokeStaticMethod(
            const AZStd::string& className,
            const AZStd::string& methodName,
            const AZStd::vector<MarshalledValue>& arguments);

        /**
         * Invoke an instance method on an object
         * @param className The name of the class
         * @param methodName The name of the method
         * @param instanceHandle Handle to the object instance (from C#)
         * @param arguments The marshalled arguments
         * @return The dispatch result with return value or error
         */
        DispatchResult InvokeInstanceMethod(
            const AZStd::string& className,
            const AZStd::string& methodName,
            void* instanceHandle,
            const AZStd::vector<MarshalledValue>& arguments);

        /**
         * Invoke a global method (not part of any class)
         * @param methodName The name of the method
         * @param arguments The marshalled arguments
         * @return The dispatch result with return value or error
         */
        DispatchResult InvokeGlobalMethod(
            const AZStd::string& methodName,
            const AZStd::vector<MarshalledValue>& arguments);

        // ============================================================
        // Property Access
        // ============================================================

        /**
         * Get a property value from an object
         * @param className The name of the class
         * @param propertyName The name of the property
         * @param instanceHandle Handle to the object instance (nullptr for static)
         * @return The dispatch result with the property value or error
         */
        DispatchResult GetProperty(
            const AZStd::string& className,
            const AZStd::string& propertyName,
            void* instanceHandle);

        /**
         * Set a property value on an object
         * @param className The name of the class
         * @param propertyName The name of the property
         * @param instanceHandle Handle to the object instance (nullptr for static)
         * @param value The value to set
         * @return The dispatch result indicating success or error
         */
        DispatchResult SetProperty(
            const AZStd::string& className,
            const AZStd::string& propertyName,
            void* instanceHandle,
            const MarshalledValue& value);

        /**
         * Get a global property value
         * @param propertyName The name of the property
         * @return The dispatch result with the property value or error
         */
        DispatchResult GetGlobalProperty(const AZStd::string& propertyName);

        /**
         * Set a global property value
         * @param propertyName The name of the property
         * @param value The value to set
         * @return The dispatch result indicating success or error
         */
        DispatchResult SetGlobalProperty(
            const AZStd::string& propertyName,
            const MarshalledValue& value);

        // ============================================================
        // EBus Operations
        // ============================================================

        /**
         * Broadcast an event on an EBus
         * @param busName The name of the EBus
         * @param eventName The name of the event
         * @param arguments The marshalled arguments
         * @return The dispatch result with return value or error
         */
        DispatchResult BroadcastEBusEvent(
            const AZStd::string& busName,
            const AZStd::string& eventName,
            const AZStd::vector<MarshalledValue>& arguments);

        /**
         * Send an event to a specific address on an EBus
         * @param busName The name of the EBus
         * @param eventName The name of the event
         * @param address The address (typically EntityId)
         * @param arguments The marshalled arguments
         * @return The dispatch result with return value or error
         */
        DispatchResult SendEBusEvent(
            const AZStd::string& busName,
            const AZStd::string& eventName,
            const MarshalledValue& address,
            const AZStd::vector<MarshalledValue>& arguments);

        // ============================================================
        // Object Construction
        // ============================================================

        /**
         * Create an instance of a reflected class
         * @param className The name of the class to instantiate
         * @param constructorArgs Arguments for the constructor
         * @return The dispatch result with the new object handle or error
         */
        DispatchResult CreateInstance(
            const AZStd::string& className,
            const AZStd::vector<MarshalledValue>& constructorArgs);

        /**
         * Destroy an instance of a reflected class
         * @param className The name of the class
         * @param instanceHandle Handle to the object to destroy
         * @return The dispatch result indicating success or error
         */
        DispatchResult DestroyInstance(
            const AZStd::string& className,
            void* instanceHandle);

        // ============================================================
        // Internal Calls Registration
        // ============================================================

        /**
         * Register all generic dispatcher internal calls with a Coral assembly
         * These are the C# -> C++ entry points
         * @param assembly The assembly to register with
         */
        static void RegisterInternalCalls(Coral::ManagedAssembly* assembly);

    private:
        /**
         * Convert MarshalledValue to AZ::BehaviorArgument for method invocation
         */
        bool MarshalToBehaviorArgument(
            const MarshalledValue& value,
            const ReflectedParameter& expectedType,
            AZ::BehaviorArgument& outArg,
            AZStd::vector<AZStd::any>& storageBuffer);

        /**
         * Convert AZ::BehaviorArgument result to MarshalledValue
         */
        MarshalledValue MarshalFromBehaviorResult(
            const AZ::BehaviorArgument& result,
            const ReflectedParameter& resultType);

        /**
         * Find a method that matches the given arguments
         */
        const ReflectedMethod* FindMatchingMethod(
            const ReflectedClass* cls,
            const AZStd::string& methodName,
            const AZStd::vector<MarshalledValue>& arguments,
            bool isStatic);

        /**
         * Find a matching constructor
         */
        const ReflectedMethod* FindMatchingConstructor(
            const ReflectedClass* cls,
            const AZStd::vector<MarshalledValue>& arguments);

    public:
        /**
         * Get the reflector (for internal calls access)
         */
        BehaviorContextReflector* GetReflector() const { return m_reflector; }

    private:
        BehaviorContextReflector* m_reflector = nullptr;
        bool m_initialized = false;

        // Storage for temporary values during marshalling
        // Using thread_local for thread safety
        static thread_local AZStd::vector<AZStd::any> s_marshalStorage;
    };

    // ============================================================
    // Static Internal Call Functions
    // These are the actual C functions that C# calls via InternalCall
    // ============================================================

    namespace GenericDispatcherInternalCalls
    {
        // Reflection queries
        Coral::String Reflection_GetClassNames();
        Coral::String Reflection_GetMethodNames(Coral::String className);
        Coral::String Reflection_GetPropertyNames(Coral::String className);
        Coral::String Reflection_GetEBusNames();
        Coral::String Reflection_GetEBusEventNames(Coral::String busName);
        bool Reflection_ClassExists(Coral::String className);
        bool Reflection_MethodExists(Coral::String className, Coral::String methodName);
        
        // Method invocation (using JSON-encoded arguments for simplicity)
        Coral::String InvokeStaticMethod(Coral::String className, Coral::String methodName, Coral::String argsJson);
        Coral::String InvokeInstanceMethod(Coral::String className, Coral::String methodName, int64_t instanceHandle, Coral::String argsJson);
        Coral::String InvokeGlobalMethod(Coral::String methodName, Coral::String argsJson);
        
        // Property access
        Coral::String GetProperty(Coral::String className, Coral::String propertyName, int64_t instanceHandle);
        bool SetProperty(Coral::String className, Coral::String propertyName, int64_t instanceHandle, Coral::String valueJson);
        Coral::String GetGlobalProperty(Coral::String propertyName);
        bool SetGlobalProperty(Coral::String propertyName, Coral::String valueJson);
        
        // EBus
        Coral::String BroadcastEBusEvent(Coral::String busName, Coral::String eventName, Coral::String argsJson);
        Coral::String SendEBusEvent(Coral::String busName, Coral::String eventName, int64_t address, Coral::String argsJson);
        
        // Object lifecycle
        int64_t CreateInstance(Coral::String className, Coral::String argsJson);
        void DestroyInstance(Coral::String className, int64_t instanceHandle);
    }

} // namespace O3DESharp