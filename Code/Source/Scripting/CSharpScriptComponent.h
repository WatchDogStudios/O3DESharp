/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

#pragma once

#include <AzCore/base.h>
#include <AzCore/Asset/AssetCommon.h>
#include <AzCore/Component/Component.h>
#include <AzCore/Component/TickBus.h>
#include <AzCore/Component/TransformBus.h>
#include <AzCore/std/string/string.h>
#include <AzCore/RTTI/RTTI.h>
#include <AzCore/Memory/SystemAllocator.h>
#include <AzCore/Serialization/SerializeContext.h>
#include <AzCore/Serialization/EditContext.h>

#include <Coral/ManagedObject.hpp>

namespace O3DESharp
{
    /**
     * Configuration for a C# script component
     */
    class CSharpScriptComponentConfig final
        : public AZ::ComponentConfig
    {
    public:
        AZ_RTTI(CSharpScriptComponentConfig, "{8B0E4206-1620-41BC-BDB6-3568A5E57BBC}", AZ::ComponentConfig);
        AZ_CLASS_ALLOCATOR(CSharpScriptComponentConfig, AZ::SystemAllocator);

        static void Reflect(AZ::ReflectContext* context);

        CSharpScriptComponentConfig() = default;

        /**
         * The fully qualified name of the C# class to instantiate
         * Example: "MyGame.PlayerController" or "MyNamespace.MyClass"
         */
        AZStd::string m_scriptClassName;

        /**
         * Optional: Path to the assembly containing the script class
         * If empty, uses the default user assembly
         */
        AZStd::string m_assemblyPath;
    };

    /**
     * CSharpScriptComponent - Allows attaching C# scripts to O3DE entities
     * 
     * This component bridges the O3DE entity system with C# scripting via Coral.
     * It creates an instance of a managed class and calls lifecycle methods:
     * 
     * - OnCreate(): Called when the component is activated
     * - OnUpdate(float deltaTime): Called every tick
     * - OnDestroy(): Called when the component is deactivated
     * 
     * The C# class should inherit from O3DE.ScriptComponent base class.
     * 
     * Example C# script:
     * ```csharp
     * namespace MyGame
     * {
     *     public class PlayerController : O3DE.ScriptComponent
     *     {
     *         public override void OnCreate()
     *         {
     *             Debug.Log("PlayerController created!");
     *         }
     *         
     *         public override void OnUpdate(float deltaTime)
     *         {
     *             // Update logic here
     *         }
     *         
     *         public override void OnDestroy()
     *         {
     *             Debug.Log("PlayerController destroyed!");
     *         }
     *     }
     * }
     * ```
     */
    class CSharpScriptComponent
        : public AZ::Component
        , public AZ::TickBus::Handler
        , public AZ::TransformNotificationBus::Handler
    {
    public:
        AZ_COMPONENT(CSharpScriptComponent, "{05918223-7DEF-48F6-8963-53BA48371E1D}");

        static void Reflect(AZ::ReflectContext* context);

        static void GetProvidedServices(AZ::ComponentDescriptor::DependencyArrayType& provided);
        static void GetIncompatibleServices(AZ::ComponentDescriptor::DependencyArrayType& incompatible);
        static void GetRequiredServices(AZ::ComponentDescriptor::DependencyArrayType& required);
        static void GetDependentServices(AZ::ComponentDescriptor::DependencyArrayType& dependent);

        CSharpScriptComponent() = default;
        explicit CSharpScriptComponent(const CSharpScriptComponentConfig& config);
        ~CSharpScriptComponent() override;

        // Configuration access
        void SetConfiguration(const CSharpScriptComponentConfig& config);
        const CSharpScriptComponentConfig& GetConfiguration() const;

        /**
         * Check if the managed script instance is valid and ready
         */
        bool IsScriptValid() const;

        /**
         * Reload the script (creates a new instance)
         * Useful for hot-reload scenarios
         */
        void ReloadScript();

        /**
         * Invoke a method on the managed script instance
         * @param methodName Name of the method to invoke
         */
        template<typename... TArgs>
        void InvokeMethod(const char* methodName, TArgs&&... args);

        /**
         * Invoke a method with a return value
         */
        template<typename TReturn, typename... TArgs>
        TReturn InvokeMethodRet(const char* methodName, TArgs&&... args);

        /**
         * Set a field value on the managed script instance
         */
        template<typename TValue>
        void SetFieldValue(const char* fieldName, TValue value);

        /**
         * Get a field value from the managed script instance
         */
        template<typename TReturn>
        TReturn GetFieldValue(const char* fieldName);

    protected:
        // AZ::Component interface
        void Init() override;
        void Activate() override;
        void Deactivate() override;

        // AZ::TickBus::Handler
        void OnTick(float deltaTime, AZ::ScriptTimePoint time) override;

        // AZ::TransformNotificationBus::Handler
        void OnTransformChanged(const AZ::Transform& local, const AZ::Transform& world) override;

    private:
        /**
         * Create the managed script instance from the configured class name
         */
        bool CreateScriptInstance();

        /**
         * Destroy the managed script instance
         */
        void DestroyScriptInstance();

        /**
         * Pass the entity ID to the managed instance so it knows which entity it belongs to
         */
        void SetEntityIdOnScript();

    private:
        CSharpScriptComponentConfig m_config;

        // The managed C# object instance
        Coral::ManagedObject m_scriptInstance;

        // Cached type pointer for the script class
        Coral::Type* m_scriptType = nullptr;

        // Flag to track if the script has been initialized
        bool m_scriptInitialized = false;

        // Flag to prevent re-entrant activation
        bool m_isActivating = false;
    };

    // Template implementations
    template<typename... TArgs>
    void CSharpScriptComponent::InvokeMethod(const char* methodName, TArgs&&... args)
    {
        if (m_scriptInstance.IsValid())
        {
            m_scriptInstance.InvokeMethod(methodName, std::forward<TArgs>(args)...);
        }
    }

    template<typename TReturn, typename... TArgs>
    TReturn CSharpScriptComponent::InvokeMethodRet(const char* methodName, TArgs&&... args)
    {
        if (m_scriptInstance.IsValid())
        {
            return m_scriptInstance.InvokeMethod<TReturn>(methodName, std::forward<TArgs>(args)...);
        }
        return TReturn{};
    }

    template<typename TValue>
    void CSharpScriptComponent::SetFieldValue(const char* fieldName, TValue value)
    {
        if (m_scriptInstance.IsValid())
        {
            m_scriptInstance.SetFieldValue(fieldName, value);
        }
    }

    template<typename TReturn>
    TReturn CSharpScriptComponent::GetFieldValue(const char* fieldName)
    {
        if (m_scriptInstance.IsValid())
        {
            return m_scriptInstance.GetFieldValue<TReturn>(fieldName);
        }
        return TReturn{};
    }

} // namespace O3DESharp
