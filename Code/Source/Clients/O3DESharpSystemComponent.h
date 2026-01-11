/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

#pragma once

#include <AzCore/Component/Component.h>
#include <AzCore/Component/TickBus.h>
#include <AzCore/std/smart_ptr/unique_ptr.h>
#include <O3DESharp/O3DESharpBus.h>

namespace O3DESharp
{
    class CoralHostManager;
    class BehaviorContextReflector;
    class GenericDispatcher;

    /**
     * O3DESharpSystemComponent - Core system component for C# scripting support
     * 
     * This component is responsible for:
     * - Initializing and shutting down the Coral .NET host
     * - Managing the lifecycle of managed assemblies
     * - Registering C++ functions as internal calls for C#
     * - Reflecting the BehaviorContext to enable automatic C# bindings
     * - Providing the bridge between O3DE and the .NET runtime
     * 
     * Configuration can be provided via the Settings Registry:
     * - /O3DE/O3DESharp/CoralDirectory: Path to Coral.Managed.dll
     * - /O3DE/O3DESharp/CoreApiAssemblyPath: Path to O3DE.Core.dll
     * - /O3DE/O3DESharp/UserAssemblyPath: Path to the user's game scripts DLL
     */
    class O3DESharpSystemComponent
        : public AZ::Component
        , protected O3DESharpRequestBus::Handler
    {
    public:
        AZ_COMPONENT_DECL(O3DESharpSystemComponent);

        static void Reflect(AZ::ReflectContext* context);

        static void GetProvidedServices(AZ::ComponentDescriptor::DependencyArrayType& provided);
        static void GetIncompatibleServices(AZ::ComponentDescriptor::DependencyArrayType& incompatible);
        static void GetRequiredServices(AZ::ComponentDescriptor::DependencyArrayType& required);
        static void GetDependentServices(AZ::ComponentDescriptor::DependencyArrayType& dependent);

        O3DESharpSystemComponent();
        ~O3DESharpSystemComponent();

    protected:
        ////////////////////////////////////////////////////////////////////////
        // O3DESharpRequestBus interface implementation
        bool IsCoralHostInitialized() const override;
        AZStd::string GetCoralHostStatus() const override;
        bool LoadAssembly(const AZStd::string& assemblyPath) override;
        bool ReloadUserAssemblies() override;
        bool IsHotReloadEnabled() const override;
        bool TypeExists(const AZStd::string& fullTypeName) const override;
        AZStd::vector<AZStd::string> GetAvailableScriptTypes() const override;
        AZStd::string GetCoralDirectory() const override;
        AZStd::string GetCoreAssemblyPath() const override;
        AZStd::string GetUserAssemblyPath() const override;
        ////////////////////////////////////////////////////////////////////////

        ////////////////////////////////////////////////////////////////////////
        // AZ::Component interface implementation
        void Init() override;
        void Activate() override;
        void Deactivate() override;
        ////////////////////////////////////////////////////////////////////////

    private:
        /**
         * Initialize the Coral .NET host
         * Called during component activation
         */
        void InitializeCoralHost();

        /**
         * Shutdown the Coral .NET host
         * Called during component deactivation
         */
        void ShutdownCoralHost();

        /**
         * Register all C++ internal calls with the loaded core assembly
         * These are the functions that C# code can call into native code
         */
        void RegisterScriptBindings();

        /**
         * Initialize the BehaviorContext reflection system
         * This extracts metadata from O3DE's BehaviorContext for automatic C# binding
         */
        void InitializeReflectionSystem();

        /**
         * Shutdown the reflection system
         */
        void ShutdownReflectionSystem();

        /**
         * Called after BehaviorContext has been populated to reflect all types
         */
        void ReflectBehaviorContext();

    private:
        // The Coral host manager instance - manages .NET runtime lifecycle
        AZStd::unique_ptr<CoralHostManager> m_coralHostManager;

        // The BehaviorContext reflector - extracts type information from O3DE
        AZStd::unique_ptr<BehaviorContextReflector> m_reflector;

        // The generic dispatcher - enables dynamic method invocation from C#
        AZStd::unique_ptr<GenericDispatcher> m_dispatcher;

        // Cached configuration values
        AZStd::string m_coralDirectory;
        AZStd::string m_coreAssemblyPath;
        AZStd::string m_userAssemblyPath;
        bool m_hotReloadEnabled = false;
    };

} // namespace O3DESharp