/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

#pragma once

#include <AzCore/base.h>
#include <AzCore/std/string/string.h>
#include <AzCore/std/containers/unordered_map.h>
#include <AzCore/std/smart_ptr/unique_ptr.h>
#include <AzCore/Memory/SystemAllocator.h>
#include <AzCore/RTTI/RTTI.h>
#include <AzCore/Interface/Interface.h>

#include <Coral/HostInstance.hpp>
#include <Coral/Assembly.hpp>
#include <Coral/Type.hpp>
#include <Coral/ManagedObject.hpp>

namespace O3DESharp
{
    /**
     * Configuration for initializing the Coral host
     */
    struct CoralHostConfig
    {
        AZStd::string coralDirectory;           // Path to Coral.Managed.dll and runtimeconfig
        AZStd::string userAssemblyPath;         // Path to user's game assembly
        AZStd::string coreApiAssemblyPath;      // Path to O3DE.Core.dll (our API)
        bool enableHotReload = true;            // Enable assembly hot-reloading
    };

    /**
     * Result of host initialization
     */
    enum class CoralHostStatus
    {
        Success,
        NotInitialized,
        CoralManagedNotFound,
        CoralInitError,
        DotNetNotFound,
        AssemblyLoadFailed,
        AlreadyInitialized
    };

    /**
     * Interface for the Coral Host Manager - allows other systems to interact with C# scripting
     */
    class ICoralHostManager
    {
    public:
        AZ_RTTI(ICoralHostManager, "{A3B7C8D9-1234-5678-9ABC-DEF012345678}");

        virtual ~ICoralHostManager() = default;

        /**
         * Initialize the .NET runtime and Coral host
         * @param config Configuration for the host
         * @return Status of the initialization
         */
        virtual CoralHostStatus Initialize(const CoralHostConfig& config) = 0;

        /**
         * Shutdown the .NET runtime and release all resources
         */
        virtual void Shutdown() = 0;

        /**
         * Check if the host is initialized and ready
         */
        virtual bool IsInitialized() const = 0;

        /**
         * Load a managed assembly from disk
         * @param assemblyPath Full path to the .dll file
         * @return Pointer to the loaded assembly, or nullptr on failure
         */
        virtual Coral::ManagedAssembly* LoadAssembly(const AZStd::string& assemblyPath) = 0;

        /**
         * Reload user assemblies (for hot-reload support)
         * @return true if reload was successful
         */
        virtual bool ReloadUserAssemblies() = 0;

        /**
         * Get a type from the core API assembly
         * @param fullTypeName Fully qualified type name (e.g., "O3DE.Entity")
         * @return Pointer to the type, or nullptr if not found
         */
        virtual Coral::Type* GetCoreType(const AZStd::string& fullTypeName) = 0;

        /**
         * Get a type from the user assembly
         * @param fullTypeName Fully qualified type name (e.g., "MyGame.PlayerController")
         * @return Pointer to the type, or nullptr if not found
         */
        virtual Coral::Type* GetUserType(const AZStd::string& fullTypeName) = 0;

        /**
         * Create an instance of a managed type
         * @param type The type to instantiate
         * @return The managed object instance
         */
        virtual Coral::ManagedObject CreateInstance(Coral::Type& type) = 0;

        /**
         * Get the core API assembly (O3DE.Core.dll)
         */
        virtual Coral::ManagedAssembly* GetCoreAssembly() = 0;

        /**
         * Get the user game assembly
         */
        virtual Coral::ManagedAssembly* GetUserAssembly() = 0;
    };

    using CoralHostManagerInterface = AZ::Interface<ICoralHostManager>;

    /**
     * CoralHostManager - Manages the Coral .NET Host lifecycle
     * 
     * This is the central point for all C# scripting functionality in O3DE.
     * It handles:
     * - Initializing the .NET runtime via Coral
     * - Loading and unloading managed assemblies
     * - Providing access to types and instances
     * - Hot-reloading of user scripts
     */
    class CoralHostManager final
        : public ICoralHostManager
    {
    public:
        AZ_RTTI(CoralHostManager, "{B4C8D9E0-2345-6789-ABCD-EF0123456789}", ICoralHostManager);
        AZ_CLASS_ALLOCATOR(CoralHostManager, AZ::SystemAllocator);

        CoralHostManager();
        ~CoralHostManager() override;

        // ICoralHostManager interface
        CoralHostStatus Initialize(const CoralHostConfig& config) override;
        void Shutdown() override;
        bool IsInitialized() const override;
        Coral::ManagedAssembly* LoadAssembly(const AZStd::string& assemblyPath) override;
        bool ReloadUserAssemblies() override;
        Coral::Type* GetCoreType(const AZStd::string& fullTypeName) override;
        Coral::Type* GetUserType(const AZStd::string& fullTypeName) override;
        Coral::ManagedObject CreateInstance(Coral::Type& type) override;
        Coral::ManagedAssembly* GetCoreAssembly() override;
        Coral::ManagedAssembly* GetUserAssembly() override;

    private:
        // Coral message callback for logging
        static void CoralMessageCallback(std::string_view message, Coral::MessageLevel level);
        
        // Coral exception callback
        static void CoralExceptionCallback(std::string_view message);

        // Load the core O3DE API assembly
        bool LoadCoreAssembly();

        // Load the user's game assembly
        bool LoadUserAssembly();

        // Register all internal calls (C++ functions callable from C#)
        void RegisterInternalCalls();

    private:
        bool m_initialized = false;
        CoralHostConfig m_config;

        // The Coral host instance - manages the .NET runtime
        AZStd::unique_ptr<Coral::HostInstance> m_hostInstance;

        // Assembly load context for core assemblies (O3DE.Core.dll)
        Coral::AssemblyLoadContext m_coreContext;

        // Assembly load context for user assemblies (for hot-reload, we can unload this)
        Coral::AssemblyLoadContext m_userContext;

        // Cached pointers to our assemblies
        Coral::ManagedAssembly* m_coreAssembly = nullptr;
        Coral::ManagedAssembly* m_userAssembly = nullptr;

        // Type cache for faster lookups
        AZStd::unordered_map<AZStd::string, Coral::Type*> m_coreTypeCache;
        AZStd::unordered_map<AZStd::string, Coral::Type*> m_userTypeCache;
    };

} // namespace O3DESharp