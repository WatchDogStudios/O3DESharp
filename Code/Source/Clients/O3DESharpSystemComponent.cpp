/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

#include "O3DESharpSystemComponent.h"

#include <O3DESharp/O3DESharpTypeIds.h>

#include <AzCore/Console/ILogger.h>
#include <AzCore/IO/FileIO.h>
#include <AzCore/IO/Path/Path.h>
#include <AzCore/Serialization/SerializeContext.h>
#include <AzCore/Settings/SettingsRegistry.h>
#include <AzCore/Utils/Utils.h>
#include <AzCore/RTTI/BehaviorContext.h>
#include <AzCore/Component/ComponentApplicationBus.h>

#include <Atom/RPI.Public/FeatureProcessorFactory.h>

#include <Render/O3DESharpFeatureProcessor.h>
#include <Scripting/CoralHostManager.h>
#include <Scripting/ScriptBindings.h>
#include <Scripting/CSharpScriptComponent.h>
#include <Scripting/Reflection/BehaviorContextReflector.h>
#include <Scripting/Reflection/GenericDispatcher.h>

namespace O3DESharp
{
    AZ_COMPONENT_IMPL(O3DESharpSystemComponent, "O3DESharpSystemComponent",
        O3DESharpSystemComponentTypeId);

    void O3DESharpSystemComponent::Reflect(AZ::ReflectContext* context)
    {
        if (auto serializeContext = azrtti_cast<AZ::SerializeContext*>(context))
        {
            serializeContext->Class<O3DESharpSystemComponent, AZ::Component>()
                ->Version(2)
                ;
        }

        O3DESharpFeatureProcessor::Reflect(context);
        
        // Reflect scripting components
        CSharpScriptComponent::Reflect(context);
    }

    void O3DESharpSystemComponent::GetProvidedServices(AZ::ComponentDescriptor::DependencyArrayType& provided)
    {
        provided.push_back(AZ_CRC_CE("O3DESharpSystemService"));
    }

    void O3DESharpSystemComponent::GetIncompatibleServices(AZ::ComponentDescriptor::DependencyArrayType& incompatible)
    {
        incompatible.push_back(AZ_CRC_CE("O3DESharpSystemService"));
    }

    void O3DESharpSystemComponent::GetRequiredServices([[maybe_unused]] AZ::ComponentDescriptor::DependencyArrayType& required)
    {
        required.push_back(AZ_CRC_CE("RPISystem"));
    }

    void O3DESharpSystemComponent::GetDependentServices([[maybe_unused]] AZ::ComponentDescriptor::DependencyArrayType& dependent)
    {
    }

    O3DESharpSystemComponent::O3DESharpSystemComponent()
    {
        if (O3DESharpInterface::Get() == nullptr)
        {
            O3DESharpInterface::Register(this);
        }
    }

    O3DESharpSystemComponent::~O3DESharpSystemComponent()
    {
        if (O3DESharpInterface::Get() == this)
        {
            O3DESharpInterface::Unregister(this);
        }
    }

    void O3DESharpSystemComponent::Init()
    {
        // Create the Coral host manager
        m_coralHostManager = AZStd::make_unique<CoralHostManager>();
        
        // Create the reflection system components
        m_reflector = AZStd::make_unique<BehaviorContextReflector>();
        m_dispatcher = AZStd::make_unique<GenericDispatcher>();
    }

    void O3DESharpSystemComponent::Activate()
    {
        O3DESharpRequestBus::Handler::BusConnect();

        // Register the feature processor for rendering support
        AZ::RPI::FeatureProcessorFactory::Get()->RegisterFeatureProcessor<O3DESharpFeatureProcessor>();

        // Initialize the Coral .NET host
        InitializeCoralHost();

        // Initialize the BehaviorContext reflection system
        InitializeReflectionSystem();

        AZLOG_INFO("O3DESharpSystemComponent: Activated - C# scripting is ready");
    }

    void O3DESharpSystemComponent::Deactivate()
    {
        // Shutdown reflection system
        ShutdownReflectionSystem();

        // Shutdown Coral host
        ShutdownCoralHost();

        AZ::RPI::FeatureProcessorFactory::Get()->UnregisterFeatureProcessor<O3DESharpFeatureProcessor>();

        O3DESharpRequestBus::Handler::BusDisconnect();

        AZLOG_INFO("O3DESharpSystemComponent: Deactivated");
    }

    // ============================================================
    // O3DESharpRequestBus Implementation
    // ============================================================

    bool O3DESharpSystemComponent::IsCoralHostInitialized() const
    {
        return m_coralHostManager && m_coralHostManager->IsInitialized();
    }

    AZStd::string O3DESharpSystemComponent::GetCoralHostStatus() const
    {
        if (!m_coralHostManager)
        {
            return "Host manager not created";
        }
        if (!m_coralHostManager->IsInitialized())
        {
            return "Not initialized";
        }
        return "Initialized and running";
    }

    bool O3DESharpSystemComponent::LoadAssembly(const AZStd::string& assemblyPath)
    {
        if (!m_coralHostManager || !m_coralHostManager->IsInitialized())
        {
            AZLOG_ERROR("O3DESharpSystemComponent: Cannot load assembly - host not initialized");
            return false;
        }

        Coral::ManagedAssembly* assembly = m_coralHostManager->LoadAssembly(assemblyPath);
        return assembly != nullptr;
    }

    bool O3DESharpSystemComponent::ReloadUserAssemblies()
    {
        if (!m_coralHostManager || !m_coralHostManager->IsInitialized())
        {
            AZLOG_ERROR("O3DESharpSystemComponent: Cannot reload - host not initialized");
            return false;
        }

        bool success = m_coralHostManager->ReloadUserAssemblies();
        
        if (success)
        {
            // Re-register script bindings after reload
            RegisterScriptBindings();
        }

        return success;
    }

    bool O3DESharpSystemComponent::IsHotReloadEnabled() const
    {
        return m_hotReloadEnabled;
    }

    bool O3DESharpSystemComponent::TypeExists(const AZStd::string& fullTypeName) const
    {
        if (!m_coralHostManager || !m_coralHostManager->IsInitialized())
        {
            return false;
        }

        // Check in user assembly first, then core
        Coral::Type* type = m_coralHostManager->GetUserType(fullTypeName);
        if (!type)
        {
            type = m_coralHostManager->GetCoreType(fullTypeName);
        }
        return type != nullptr;
    }

    AZStd::vector<AZStd::string> O3DESharpSystemComponent::GetAvailableScriptTypes() const
    {
        AZStd::vector<AZStd::string> types;

        // This would require iterating the user assembly to find all types
        // that inherit from ScriptComponent. For now, return empty.
        // TODO: Implement type enumeration

        return types;
    }

    AZStd::string O3DESharpSystemComponent::GetCoralDirectory() const
    {
        return m_coralDirectory;
    }

    AZStd::string O3DESharpSystemComponent::GetCoreAssemblyPath() const
    {
        return m_coreAssemblyPath;
    }

    AZStd::string O3DESharpSystemComponent::GetUserAssemblyPath() const
    {
        return m_userAssemblyPath;
    }

    // ============================================================
    // Coral Host Management
    // ============================================================

    void O3DESharpSystemComponent::InitializeCoralHost()
    {
        if (!m_coralHostManager)
        {
            AZLOG_ERROR("O3DESharpSystemComponent: Coral host manager not created");
            return;
        }

        // Build the configuration for Coral
        CoralHostConfig config;

        // Get the project path to locate assemblies
        AZ::IO::FixedMaxPath projectPath = AZ::Utils::GetProjectPath();
        AZ::IO::FixedMaxPath engineRoot = AZ::Utils::GetEnginePath();
        
        // Coral directory - where Coral.Managed.dll and runtimeconfig are located
        AZ::IO::FixedMaxPath coralDir = projectPath / "Bin" / "Scripts" / "Coral";
        
        // Try to get Coral directory from settings registry
        if (auto settingsRegistry = AZ::SettingsRegistry::Get())
        {
            AZStd::string coralDirSetting;
            if (settingsRegistry->Get(coralDirSetting, "/O3DE/O3DESharp/CoralDirectory"))
            {
                coralDir = AZ::IO::FixedMaxPath(coralDirSetting.c_str());
            }
        }
        
        config.coralDirectory = coralDir.c_str();
        m_coralDirectory = config.coralDirectory;

        // Core API assembly path - O3DE.Core.dll
        AZ::IO::FixedMaxPath coreApiPath = projectPath / "Bin" / "Scripts" / "O3DE.Core.dll";
        
        if (auto settingsRegistry = AZ::SettingsRegistry::Get())
        {
            AZStd::string coreApiSetting;
            if (settingsRegistry->Get(coreApiSetting, "/O3DE/O3DESharp/CoreApiAssemblyPath"))
            {
                coreApiPath = AZ::IO::FixedMaxPath(coreApiSetting.c_str());
            }
        }
        
        config.coreApiAssemblyPath = coreApiPath.c_str();
        m_coreAssemblyPath = config.coreApiAssemblyPath;

        // User assembly path - the game's C# scripts compiled DLL
        AZ::IO::FixedMaxPath userAssemblyPath = projectPath / "Bin" / "Scripts" / "GameScripts.dll";
        
        if (auto settingsRegistry = AZ::SettingsRegistry::Get())
        {
            AZStd::string userAssemblySetting;
            if (settingsRegistry->Get(userAssemblySetting, "/O3DE/O3DESharp/UserAssemblyPath"))
            {
                userAssemblyPath = AZ::IO::FixedMaxPath(userAssemblySetting.c_str());
            }
        }
        
        config.userAssemblyPath = userAssemblyPath.c_str();
        m_userAssemblyPath = config.userAssemblyPath;

        // Enable hot reload in development builds
#if defined(AZ_DEBUG_BUILD) || defined(AZ_PROFILE_BUILD)
        config.enableHotReload = true;
#else
        config.enableHotReload = false;
#endif
        m_hotReloadEnabled = config.enableHotReload;

        AZLOG_INFO("O3DESharpSystemComponent: Initializing Coral .NET Host");
        AZLOG_INFO("  Coral Directory: %s", config.coralDirectory.c_str());
        AZLOG_INFO("  Core API Assembly: %s", config.coreApiAssemblyPath.c_str());
        AZLOG_INFO("  User Assembly: %s", config.userAssemblyPath.c_str());
        AZLOG_INFO("  Hot Reload: %s", config.enableHotReload ? "Enabled" : "Disabled");

        // Initialize the Coral host
        CoralHostStatus status = m_coralHostManager->Initialize(config);

        switch (status)
        {
        case CoralHostStatus::Success:
            AZLOG_INFO("O3DESharpSystemComponent: Coral host initialized successfully");
            
            // Register the interface so other systems can access the host
            CoralHostManagerInterface::Register(m_coralHostManager.get());
            
            // Register internal calls (C++ functions exposed to C#)
            RegisterScriptBindings();
            break;

        case CoralHostStatus::CoralManagedNotFound:
            AZLOG_ERROR("O3DESharpSystemComponent: Coral.Managed.dll not found. "
                "Please ensure the .NET runtime is installed and Coral is properly configured.");
            break;

        case CoralHostStatus::DotNetNotFound:
            AZLOG_ERROR("O3DESharpSystemComponent: .NET runtime not found. "
                "Please install the .NET SDK from https://dotnet.microsoft.com/download");
            break;

        case CoralHostStatus::CoralInitError:
            AZLOG_ERROR("O3DESharpSystemComponent: Failed to initialize Coral. "
                "Check the log for details.");
            break;

        case CoralHostStatus::AssemblyLoadFailed:
            AZLOG_ERROR("O3DESharpSystemComponent: Failed to load required assemblies. "
                "Ensure O3DE.Core.dll exists at the configured path.");
            break;

        case CoralHostStatus::AlreadyInitialized:
            AZLOG_WARN("O3DESharpSystemComponent: Coral host already initialized");
            break;

        default:
            AZLOG_ERROR("O3DESharpSystemComponent: Unknown error initializing Coral host");
            break;
        }
    }

    void O3DESharpSystemComponent::ShutdownCoralHost()
    {
        if (m_coralHostManager)
        {
            // Unregister the interface first
            if (CoralHostManagerInterface::Get() == m_coralHostManager.get())
            {
                CoralHostManagerInterface::Unregister(m_coralHostManager.get());
            }

            // Shutdown the Coral host
            m_coralHostManager->Shutdown();
            
            AZLOG_INFO("O3DESharpSystemComponent: Coral host shutdown complete");
        }
    }

    void O3DESharpSystemComponent::RegisterScriptBindings()
    {
        if (!m_coralHostManager || !m_coralHostManager->IsInitialized())
        {
            AZLOG_ERROR("O3DESharpSystemComponent: Cannot register bindings - host not initialized");
            return;
        }

        Coral::ManagedAssembly* coreAssembly = m_coralHostManager->GetCoreAssembly();
        if (!coreAssembly)
        {
            AZLOG_ERROR("O3DESharpSystemComponent: Cannot register bindings - core assembly not loaded");
            return;
        }

        // Register all manual internal calls (C++ functions callable from C#)
        ScriptBindings::RegisterAll(coreAssembly);

        // Register the generic dispatcher internal calls for reflection-based invocation
        GenericDispatcher::RegisterInternalCalls(coreAssembly);

        AZLOG_INFO("O3DESharpSystemComponent: Script bindings registered");
    }

    // ============================================================
    // Reflection System
    // ============================================================

    void O3DESharpSystemComponent::InitializeReflectionSystem()
    {
        if (!m_reflector || !m_dispatcher)
        {
            AZLOG_ERROR("O3DESharpSystemComponent: Reflection system components not created");
            return;
        }

        AZLOG_INFO("O3DESharpSystemComponent: Initializing BehaviorContext reflection system...");

        // Get the BehaviorContext
        AZ::BehaviorContext* behaviorContext = nullptr;
        AZ::ComponentApplicationBus::BroadcastResult(
            behaviorContext, 
            &AZ::ComponentApplicationRequests::GetBehaviorContext);

        if (!behaviorContext)
        {
            AZLOG_ERROR("O3DESharpSystemComponent: Failed to get BehaviorContext");
            return;
        }

        // Reflect all types from the BehaviorContext
        m_reflector->ReflectFromContext(behaviorContext);

        // Initialize the generic dispatcher with the reflector
        m_dispatcher->Initialize(m_reflector.get());

        AZLOG_INFO("O3DESharpSystemComponent: Reflection system initialized");
        AZLOG_INFO("  Reflected %zu classes", m_reflector->GetClassCount());
        AZLOG_INFO("  Reflected %zu EBuses", m_reflector->GetEBusCount());
        AZLOG_INFO("  Reflected %zu global methods", m_reflector->GetGlobalMethodCount());
        AZLOG_INFO("  Reflected %zu global properties", m_reflector->GetGlobalPropertyCount());
    }

    void O3DESharpSystemComponent::ShutdownReflectionSystem()
    {
        if (m_dispatcher)
        {
            m_dispatcher->Shutdown();
        }

        if (m_reflector)
        {
            m_reflector->Clear();
        }

        AZLOG_INFO("O3DESharpSystemComponent: Reflection system shutdown complete");
    }

    void O3DESharpSystemComponent::ReflectBehaviorContext()
    {
        // This can be called to re-reflect the BehaviorContext
        // Useful after dynamic registration of new types
        if (m_reflector)
        {
            AZ::BehaviorContext* behaviorContext = nullptr;
            AZ::ComponentApplicationBus::BroadcastResult(
                behaviorContext, 
                &AZ::ComponentApplicationRequests::GetBehaviorContext);

            if (behaviorContext)
            {
                m_reflector->Clear();
                m_reflector->ReflectFromContext(behaviorContext);
                
                AZLOG_INFO("O3DESharpSystemComponent: BehaviorContext re-reflected");
            }
        }
    }

} // namespace O3DESharp