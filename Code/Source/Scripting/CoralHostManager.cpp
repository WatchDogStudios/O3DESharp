/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

#include "CoralHostManager.h"

#include <AzCore/Console/ILogger.h>
#include <AzCore/IO/FileIO.h>
#include <AzCore/IO/Path/Path.h>
#include <AzCore/Utils/Utils.h>
#include <AzCore/StringFunc/StringFunc.h>

namespace O3DESharp
{
    // Static callback functions for Coral
    void CoralHostManager::CoralMessageCallback(std::string_view message, Coral::MessageLevel level)
    {
        switch (level)
        {
        case Coral::MessageLevel::Info:
            AZLOG_INFO("[Coral] %.*s", static_cast<int>(message.size()), message.data());
            break;
        case Coral::MessageLevel::Warning:
            AZLOG_WARN("[Coral] %.*s", static_cast<int>(message.size()), message.data());
            break;
        case Coral::MessageLevel::Error:
            AZLOG_ERROR("[Coral] %.*s", static_cast<int>(message.size()), message.data());
            break;
        default:
            AZLOG_INFO("[Coral] %.*s", static_cast<int>(message.size()), message.data());
            break;
        }
    }

    void CoralHostManager::CoralExceptionCallback(std::string_view message)
    {
        AZLOG_ERROR("[Coral Exception] %.*s", static_cast<int>(message.size()), message.data());
    }

    CoralHostManager::CoralHostManager()
        : m_hostInstance(AZStd::make_unique<Coral::HostInstance>())
    {
    }

    CoralHostManager::~CoralHostManager()
    {
        if (m_initialized)
        {
            Shutdown();
        }
    }

    CoralHostStatus CoralHostManager::Initialize(const CoralHostConfig& config)
    {
        if (m_initialized)
        {
            AZLOG_WARN("CoralHostManager::Initialize - Already initialized");
            return CoralHostStatus::AlreadyInitialized;
        }

        m_config = config;

        // Setup Coral host settings
        Coral::HostSettings settings;
        settings.CoralDirectory = std::string(m_config.coralDirectory.c_str());
        settings.MessageCallback = &CoralHostManager::CoralMessageCallback;
        settings.MessageFilter = Coral::MessageLevel::All;
        settings.ExceptionCallback = &CoralHostManager::CoralExceptionCallback;

        AZLOG_INFO("CoralHostManager: Initializing .NET runtime...");
        AZLOG_INFO("CoralHostManager: Coral directory: %s", m_config.coralDirectory.c_str());

        // Initialize the Coral host (starts .NET runtime)
        Coral::CoralInitStatus initStatus = m_hostInstance->Initialize(settings);

        switch (initStatus)
        {
        case Coral::CoralInitStatus::Success:
            AZLOG_INFO("CoralHostManager: .NET runtime initialized successfully");
            break;
        case Coral::CoralInitStatus::CoralManagedNotFound:
            AZLOG_ERROR("CoralHostManager: Coral.Managed.dll not found at: %s", m_config.coralDirectory.c_str());
            return CoralHostStatus::CoralManagedNotFound;
        case Coral::CoralInitStatus::CoralManagedInitError:
            AZLOG_ERROR("CoralHostManager: Failed to initialize Coral.Managed");
            return CoralHostStatus::CoralInitError;
        case Coral::CoralInitStatus::DotNetNotFound:
            AZLOG_ERROR("CoralHostManager: .NET runtime not found. Please install the .NET SDK.");
            return CoralHostStatus::DotNetNotFound;
        default:
            AZLOG_ERROR("CoralHostManager: Unknown initialization error");
            return CoralHostStatus::CoralInitError;
        }

        // Create assembly load contexts
        // Core context: holds O3DE.Core.dll - never unloaded during runtime
        m_coreContext = m_hostInstance->CreateAssemblyLoadContext("O3DECoreContext");

        // User context: holds user game assemblies - can be unloaded for hot-reload
        m_userContext = m_hostInstance->CreateAssemblyLoadContext("O3DEUserContext");

        // Load the core API assembly
        if (!LoadCoreAssembly())
        {
            AZLOG_ERROR("CoralHostManager: Failed to load core API assembly");
            m_hostInstance->Shutdown();
            return CoralHostStatus::AssemblyLoadFailed;
        }

        // Register internal calls (C++ functions exposed to C#)
        RegisterInternalCalls();

        // Load user assembly if specified
        if (!m_config.userAssemblyPath.empty())
        {
            if (!LoadUserAssembly())
            {
                AZLOG_WARN("CoralHostManager: Failed to load user assembly: %s", m_config.userAssemblyPath.c_str());
                // Not a fatal error - user can load it later
            }
        }

        m_initialized = true;
        AZLOG_INFO("CoralHostManager: Initialization complete");

        return CoralHostStatus::Success;
    }

    void CoralHostManager::Shutdown()
    {
        if (!m_initialized)
        {
            return;
        }

        AZLOG_INFO("CoralHostManager: Shutting down...");

        // Clear type caches
        m_coreTypeCache.clear();
        m_userTypeCache.clear();

        // Unload user context first (allows hot-reload cleanup)
        if (m_userAssembly != nullptr)
        {
            m_hostInstance->UnloadAssemblyLoadContext(m_userContext);
            m_userAssembly = nullptr;
        }

        // Unload core context
        if (m_coreAssembly != nullptr)
        {
            m_hostInstance->UnloadAssemblyLoadContext(m_coreContext);
            m_coreAssembly = nullptr;
        }

        // Shutdown the .NET runtime
        m_hostInstance->Shutdown();

        m_initialized = false;
        AZLOG_INFO("CoralHostManager: Shutdown complete");
    }

    bool CoralHostManager::IsInitialized() const
    {
        return m_initialized;
    }

    Coral::ManagedAssembly* CoralHostManager::LoadAssembly(const AZStd::string& assemblyPath)
    {
        if (!m_initialized)
        {
            AZLOG_ERROR("CoralHostManager::LoadAssembly - Host not initialized");
            return nullptr;
        }

        AZLOG_INFO("CoralHostManager: Loading assembly: %s", assemblyPath.c_str());

        Coral::ManagedAssembly& assembly = m_userContext.LoadAssembly(std::string(assemblyPath.c_str()));

        if (assembly.GetLoadStatus() != Coral::AssemblyLoadStatus::Success)
        {
            AZLOG_ERROR("CoralHostManager: Failed to load assembly: %s", assemblyPath.c_str());
            return nullptr;
        }

        AZLOG_INFO("CoralHostManager: Successfully loaded assembly: %s", assembly.GetName().data());
        return &assembly;
    }

    bool CoralHostManager::ReloadUserAssemblies()
    {
        if (!m_initialized)
        {
            AZLOG_ERROR("CoralHostManager::ReloadUserAssemblies - Host not initialized");
            return false;
        }

        if (!m_config.enableHotReload)
        {
            AZLOG_WARN("CoralHostManager::ReloadUserAssemblies - Hot-reload is disabled");
            return false;
        }

        AZLOG_INFO("CoralHostManager: Reloading user assemblies...");

        // Clear user type cache
        m_userTypeCache.clear();

        // Unload the current user context
        m_hostInstance->UnloadAssemblyLoadContext(m_userContext);
        m_userAssembly = nullptr;

        // Create a new user context
        m_userContext = m_hostInstance->CreateAssemblyLoadContext("O3DEUserContext");

        // Reload the user assembly
        if (!m_config.userAssemblyPath.empty())
        {
            if (!LoadUserAssembly())
            {
                AZLOG_ERROR("CoralHostManager: Failed to reload user assembly");
                return false;
            }
        }

        AZLOG_INFO("CoralHostManager: User assemblies reloaded successfully");
        return true;
    }

    Coral::Type* CoralHostManager::GetCoreType(const AZStd::string& fullTypeName)
    {
        if (!m_initialized || m_coreAssembly == nullptr)
        {
            return nullptr;
        }

        // Check cache first
        auto it = m_coreTypeCache.find(fullTypeName);
        if (it != m_coreTypeCache.end())
        {
            return it->second;
        }

        // Look up the type
        Coral::Type& type = m_coreAssembly->GetLocalType(std::string_view(fullTypeName.c_str(), fullTypeName.size()));
        if (!type)
        {
            AZLOG_WARN("CoralHostManager: Core type not found: %s", fullTypeName.c_str());
            return nullptr;
        }

        // Cache and return
        m_coreTypeCache[fullTypeName] = &type;
        return &type;
    }

    Coral::Type* CoralHostManager::GetUserType(const AZStd::string& fullTypeName)
    {
        if (!m_initialized || m_userAssembly == nullptr)
        {
            return nullptr;
        }

        // Check cache first
        auto it = m_userTypeCache.find(fullTypeName);
        if (it != m_userTypeCache.end())
        {
            return it->second;
        }

        // Look up the type
        Coral::Type& type = m_userAssembly->GetLocalType(std::string_view(fullTypeName.c_str(), fullTypeName.size()));
        if (!type)
        {
            AZLOG_WARN("CoralHostManager: User type not found: %s", fullTypeName.c_str());
            return nullptr;
        }

        // Cache and return
        m_userTypeCache[fullTypeName] = &type;
        return &type;
    }

    Coral::ManagedObject CoralHostManager::CreateInstance(Coral::Type& type)
    {
        if (!m_initialized)
        {
            AZLOG_ERROR("CoralHostManager::CreateInstance - Host not initialized");
            return Coral::ManagedObject();
        }

        return type.CreateInstance();
    }

    Coral::ManagedAssembly* CoralHostManager::GetCoreAssembly()
    {
        return m_coreAssembly;
    }

    Coral::ManagedAssembly* CoralHostManager::GetUserAssembly()
    {
        return m_userAssembly;
    }

    bool CoralHostManager::LoadCoreAssembly()
    {
        if (m_config.coreApiAssemblyPath.empty())
        {
            // Try to find O3DE.Core.dll in the default location
            AZ::IO::FixedMaxPath projectPath = AZ::Utils::GetProjectPath();
            AZ::IO::FixedMaxPath coreDllPath = projectPath / "Bin" / "Scripts" / "O3DE.Core.dll";
            m_config.coreApiAssemblyPath = coreDllPath.c_str();
        }

        AZLOG_INFO("CoralHostManager: Loading core API assembly: %s", m_config.coreApiAssemblyPath.c_str());

        // Check if file exists
        if (!AZ::IO::FileIOBase::GetInstance()->Exists(m_config.coreApiAssemblyPath.c_str()))
        {
            AZLOG_ERROR("CoralHostManager: Core API assembly not found: %s", m_config.coreApiAssemblyPath.c_str());
            return false;
        }

        Coral::ManagedAssembly& assembly = m_coreContext.LoadAssembly(std::string(m_config.coreApiAssemblyPath.c_str()));

        if (assembly.GetLoadStatus() != Coral::AssemblyLoadStatus::Success)
        {
            AZLOG_ERROR("CoralHostManager: Failed to load core API assembly. Status: %d", static_cast<int>(assembly.GetLoadStatus()));
            return false;
        }

        m_coreAssembly = &assembly;
        
        // Debug: Log detailed assembly info
        AZLOG_INFO("CoralHostManager: Core API assembly loaded:");
        AZLOG_INFO("  - Assembly Name: '%s'", assembly.GetName().data());
        AZLOG_INFO("  - Assembly ID: %d", assembly.GetAssemblyID());
        AZLOG_INFO("  - Assembly Path: %s", m_config.coreApiAssemblyPath.c_str());
        
        // Verify the assembly name is correct
        if (assembly.GetName() != "O3DE.Core")
        {
            AZLOG_ERROR("CoralHostManager: CRITICAL - Assembly name mismatch! Expected 'O3DE.Core', got '%s'", assembly.GetName().data());
            AZLOG_ERROR("CoralHostManager: This will cause internal call registration to fail!");
        }

        return true;
    }

    bool CoralHostManager::LoadUserAssembly()
    {
        if (m_config.userAssemblyPath.empty())
        {
            AZLOG_INFO("CoralHostManager: No user assembly path specified");
            return false;
        }

        AZLOG_INFO("CoralHostManager: Loading user assembly: %s", m_config.userAssemblyPath.c_str());

        // Check if file exists
        if (!AZ::IO::FileIOBase::GetInstance()->Exists(m_config.userAssemblyPath.c_str()))
        {
            AZLOG_ERROR("CoralHostManager: User assembly not found: %s", m_config.userAssemblyPath.c_str());
            return false;
        }

        Coral::ManagedAssembly& assembly = m_userContext.LoadAssembly(std::string(m_config.userAssemblyPath.c_str()));

        if (assembly.GetLoadStatus() != Coral::AssemblyLoadStatus::Success)
        {
            AZLOG_ERROR("CoralHostManager: Failed to load user assembly");
            return false;
        }

        m_userAssembly = &assembly;
        AZLOG_INFO("CoralHostManager: User assembly loaded: %s", assembly.GetName().data());

        return true;
    }

    void CoralHostManager::RegisterInternalCalls()
    {
        if (m_coreAssembly == nullptr)
        {
            AZLOG_ERROR("CoralHostManager::RegisterInternalCalls - Core assembly not loaded");
            return;
        }

        AZLOG_INFO("CoralHostManager: Registering internal calls...");

        // Internal calls are registered in ScriptBindings.cpp
        // This method is called after the core assembly is loaded to allow
        // ScriptBindings to register all the C++ functions exposed to C#

        // The actual registration happens via:
        // m_coreAssembly->AddInternalCall("O3DE.InternalCalls", "FunctionName", &FunctionPtr);
        // m_coreAssembly->UploadInternalCalls();

        // For now, we defer to ScriptBindings::RegisterAll() which should be called
        // after this manager is fully initialized

        AZLOG_INFO("CoralHostManager: Internal calls will be registered by ScriptBindings");
    }

} // namespace O3DESharp