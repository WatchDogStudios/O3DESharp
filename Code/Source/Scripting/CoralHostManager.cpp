/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

#include "CoralHostManager.h"
#include "ScriptBindings.h"

#include <O3DESharp/O3DESharpHotReloadBus.h>

namespace Coral
{
    class ManagedAssembly;
}

namespace O3DESharp::Generated
{
    // Forward declarations of the entry points emitted into
    // Code/Source/Scripting/Generated/BindingRegistration.g.cpp by the
    // binding generator. The placeholder version of the .g.cpp is a no-op;
    // once the generator runs against the gem headers it overwrites the
    // bodies with real AddInternalCall registrations + stub function
    // implementations.
    void RegisterBindings(Coral::ManagedAssembly* assembly);
    void UnregisterBindings(Coral::ManagedAssembly* assembly);
    bool HotReload(Coral::ManagedAssembly* oldAssembly, Coral::ManagedAssembly* newAssembly);
}

#include <AzCore/Console/ILogger.h>
#include <AzCore/IO/FileIO.h>
#include <AzCore/IO/Path/Path.h>
#include <AzCore/StringFunc/StringFunc.h>
#include <AzCore/Utils/Utils.h>

#include <filesystem>

namespace O3DESharp
{
    // Static callback functions for Coral
    void CoralHostManager::CoralMessageCallback(std::string_view message, Coral::MessageLevel level)
    {
        // Truncate long messages to avoid buffer overflow in AZLOG
        // The fixed_string buffer is 1000 bytes, so we limit the message to ~900 to leave room for prefix
        constexpr size_t maxMessageLength = 900;
        const int printLength = static_cast<int>(message.size() > maxMessageLength ? maxMessageLength : message.size());
        const bool truncated = message.size() > maxMessageLength;

        switch (level)
        {
        case Coral::MessageLevel::Info:
            if (truncated)
            {
                AZLOG_INFO("[Coral] %.*s... (truncated)", printLength, message.data());
            }
            else
                AZLOG_INFO("[Coral] %.*s", printLength, message.data());
            break;
        case Coral::MessageLevel::Warning:
            if (truncated)
            {
                AZLOG_WARN("[Coral] %.*s... (truncated)", printLength, message.data());
            }
            else
                AZLOG_WARN("[Coral] %.*s", printLength, message.data());
            break;
        case Coral::MessageLevel::Error:
            if (truncated)
            {
                AZLOG_ERROR("[Coral] %.*s... (truncated)", printLength, message.data());
            }
            else
                AZLOG_ERROR("[Coral] %.*s", printLength, message.data());
            break;
        default:
            if (truncated)
            {
                AZLOG_INFO("[Coral] %.*s... (truncated)", printLength, message.data());
            }
            else
                AZLOG_INFO("[Coral] %.*s", printLength, message.data());
            break;
        }
    }

    void CoralHostManager::CoralExceptionCallback(std::string_view message)
    {
        // Truncate long exception messages to avoid buffer overflow in AZLOG
        // The fixed_string buffer is 1000 bytes, so we limit the message to ~900 to leave room for prefix
        constexpr size_t maxMessageLength = 900;
        if (message.size() > maxMessageLength)
        {
            AZLOG_ERROR(
                "[Coral Exception] %.*s... (truncated, full length: %zu)",
                static_cast<int>(maxMessageLength),
                message.data(),
                message.size());
        }
        else
        {
            AZLOG_ERROR("[Coral Exception] %.*s", static_cast<int>(message.size()), message.data());
        }
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

        // Phase 18-debug: force-enable .NET diagnostics IPC channels before
        // the runtime spins up. The CLR reads these env vars during
        // Debugger::Startup, which fires inside hostfxr_initialize_for_runtime_config -
        // setting them after Coral's HostInstance::Initialize is too late.
        //
        // Why we set them at all when the runtime defaults are already "1":
        // some hosting contexts (corporate-policy GPOs, IIS-style hosts,
        // certain CI runners that proxy through a launcher) inherit
        // DOTNET_EnableDiagnostics=0 from their parent process, which
        // silently disables managed-debugger attach. Forcing them on here
        // makes the editor's debuggability invariant under the host's
        // environment - external attach from vsdbg, JetBrains, or Visual
        // Studio always works the same way regardless of who launched us.
        //
        // overwrite=false so a developer who explicitly disabled
        // diagnostics for a perf-test run (DOTNET_EnableDiagnostics=0 in
        // their shell) still wins; we only flip the unset case.
        AZ::Utils::SetEnv("DOTNET_EnableDiagnostics",          "1", /*overwrite=*/false);
        AZ::Utils::SetEnv("DOTNET_EnableDiagnostics_IPC",      "1", /*overwrite=*/false);
        AZ::Utils::SetEnv("DOTNET_EnableDiagnostics_Debugger", "1", /*overwrite=*/false);
        AZ::Utils::SetEnv("DOTNET_EnableDiagnostics_Profiler", "1", /*overwrite=*/false);

        // Positive confirmation in the editor log that this code path
        // actually executed. Useful when triaging future attach failures:
        // if this line is MISSING from the log, the env-var setup didn't
        // run (something earlier crashed or the binary is stale); if it's
        // present, the runtime came up with diagnostics enabled and
        // attach failures are happening for OTHER reasons (PIX hooks,
        // anti-cheat drivers, kernel debug-port collisions, etc).
        AZLOG_INFO("CoralHostManager: Forced DOTNET_EnableDiagnostics{,_IPC,_Debugger,_Profiler}=1 "
                   "to ensure external managed-debugger attach works regardless of parent env.");

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

        // Create a single unified assembly load context
        // NOTE: We use a single context for both O3DE.Core and user assemblies because:
        // 1. Coral uses MemoryMappedFile which locks DLL files, preventing loading the same file twice
        // 2. Assemblies in the same context can reference each other directly
        // For hot-reload, we'll unload and recreate the entire context
        //
        // KNOWN LIMITATION (investigated for 1.2.0, deliberately deferred - not a
        // TODO to "just do", see below for why): because m_userContext and
        // m_coreContext are the same AssemblyLoadContext, every ReloadUserAssemblies()
        // call (see below) unloads and rebuilds BOTH user and core assemblies, even
        // though only the user assembly actually changed. .NET's
        // AssemblyLoadContext.Unload() is all-or-nothing per context - there is no
        // way to unload just the user assembly from a context that also holds
        // O3DE.Core, so this can't be fixed by restructuring the reload code alone;
        // it requires the contexts to actually be separate objects.
        //
        // Why they aren't already separate: doing so safely requires solving a
        // problem this investigation could not resolve without Coral's own source
        // (not vendored in this repo) and, likely, changes to Coral.Managed (the C#
        // bootstrapper - a *separate* forked repo, not part of this codebase at
        // all):
        //   - Two independently-created custom AssemblyLoadContexts do NOT
        //     automatically see each other's loaded assemblies in .NET (only the
        //     Default ALC has that implicit visibility from other contexts). If
        //     O3DE.Core lived only in m_coreContext, a genuinely separate
        //     m_userContext would fail to resolve user scripts' references to it
        //     (Vector3, Entity, ScriptComponent, ...) unless something explicitly
        //     redirects that resolution - the standard .NET pattern for this is a
        //     custom AssemblyLoadContext subclass overriding Load() to fall back to
        //     a shared context, which is a Coral.Managed-side (C#) change, not
        //     something fixable from this file alone.
        //   - The tempting workaround - just load a second copy of O3DE.Core.dll
        //     into the user context too - does NOT work: it would sidestep the
        //     MemoryMappedFile lock issue but silently break type identity. A class
        //     loaded independently into two different ALCs is NOT the same type to
        //     the CLR even though it's "the same" DLL on disk, so any user script
        //     class deriving from O3DE.Core's ScriptComponent (loaded via the user
        //     context's private copy) would NOT be recognized as a ScriptComponent
        //     by native code that resolved ScriptComponent via the core context's
        //     copy. This would break the entire inheritance-based dispatch model
        //     silently, which is worse than the current (correct, if suboptimal)
        //     unified-context behavior.
        //
        // A real fix needs: (1) confirming what cross-context resolution hooks
        // Coral's C++ AssemblyLoadContext actually exposes, if any, and (2) if none
        // exist, adding a custom resolving AssemblyLoadContext in Coral.Managed that
        // redirects O3DE.Core (and only O3DE.Core) references from the user context
        // back to the single instance already loaded in the core context. Tracked
        // as a follow-up requiring engine/Coral-source access this environment
        // didn't have; see the 1.2.0 audit plan (docs/superpowers/plans/) for the
        // full investigation.
        m_coreContext = m_hostInstance->CreateAssemblyLoadContext("O3DEContext");

        // User context is the same as core context - single unified context
        // This pointer alias simplifies code that expects separate contexts
        m_userContext = m_coreContext;

        // Warn if there's a stale O3DE.Core.dll in the Coral directory
        // This can cause assembly resolution issues since Coral looks there first
#if !defined(AZ_RELEASE_BUILD)
        AZ::IO::FixedMaxPath staleCorePath = AZ::IO::FixedMaxPath(m_config.coralDirectory.c_str()) / "O3DE.Core.dll";
        if (AZ::IO::FileIOBase::GetInstance()->Exists(staleCorePath.c_str()))
        {
            AZ::IO::FixedMaxPath projectPath = AZ::Utils::GetProjectPath();
            AZ::IO::FixedMaxPath expectedPath = projectPath / "Bin" / "Scripts";
            AZLOG_WARN("CoralHostManager: Found O3DE.Core.dll in Coral directory: %s", staleCorePath.c_str());
            AZLOG_WARN(
                "CoralHostManager: This file should be deleted. O3DE.Core.dll should only exist at: %s/O3DE.Core.dll",
                expectedPath.c_str());
        }
#endif

        // Load the core API assembly
        if (!LoadCoreAssembly())
        {
            AZLOG_ERROR("CoralHostManager: Failed to load core API assembly");
            m_hostInstance->Shutdown();
            return CoralHostStatus::AssemblyLoadFailed;
        }

        // Register internal calls (C++ functions exposed to C#)
        RegisterInternalCalls();

        // Load every configured user assembly (multi-assembly), or the legacy single one.
        // Not loading any user assembly is OK - the user can call LoadAssembly later.
        if (!m_config.userAssemblyPaths.empty() || !m_config.userAssemblyPath.empty())
        {
            if (!LoadUserAssemblies())
            {
                AZLOG_WARN("CoralHostManager: One or more user assemblies failed to load");
                // Non-fatal.
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

        // Unload the unified context (contains both O3DE.Core and user assemblies)
        if (m_coreAssembly != nullptr || !m_userAssemblies.empty())
        {
            m_hostInstance->UnloadAssemblyLoadContext(m_coreContext);
            m_coreAssembly = nullptr;
            m_userAssembly = nullptr;
            m_userAssemblies.clear();
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

        // Broadcast OnBeforeUserAssemblyReload so every CSharpScriptComponent
        // (and anything else that caches Coral handles) can release its
        // managed state BEFORE the context unload below. Without this, every
        // cached Coral::Type* / Coral::ManagedObject becomes a dangling
        // pointer the instant UnloadAssemblyLoadContext runs and the next
        // dispatch through them crashes.
        O3DESharpHotReloadNotificationBus::Broadcast(
            &O3DESharpHotReloadNotifications::OnBeforeUserAssemblyReload);

        // Clear type caches - both need to be cleared since we're reloading everything
        m_userTypeCache.clear();
        m_coreTypeCache.clear();

        // Unload the unified context (which contains both O3DE.Core and user assemblies).
        // This is why every hot-reload also reloads O3DE.Core, not just the user
        // assembly that actually changed - see the detailed KNOWN LIMITATION note in
        // Initialize() above (where m_coreContext/m_userContext are created) for why
        // this can't be fixed without genuinely separate AssemblyLoadContexts, and
        // why that separation is deferred rather than attempted blind.
        m_hostInstance->UnloadAssemblyLoadContext(m_coreContext);
        m_coreAssembly = nullptr;
        m_userAssembly = nullptr;
        m_userAssemblies.clear();

        // Create a new unified context
        m_coreContext = m_hostInstance->CreateAssemblyLoadContext("O3DEContext");
        m_userContext = m_coreContext;  // Same context

        // Reload O3DE.Core first
        if (!LoadCoreAssembly())
        {
            AZLOG_ERROR("CoralHostManager: Failed to reload O3DE.Core assembly");
            return false;
        }

        // Re-register internal calls for the new context
        RegisterInternalCalls();

        // Reload all user assemblies
        if (!m_config.userAssemblyPaths.empty() || !m_config.userAssemblyPath.empty())
        {
            if (!LoadUserAssemblies())
            {
                AZLOG_ERROR("CoralHostManager: Failed to reload user assemblies");
                return false;
            }
        }

        // Broadcast OnAfterUserAssemblyReload so every script component
        // re-resolves its Coral::Type and reconstructs its managed instance
        // against the freshly-loaded user assemblies.
        O3DESharpHotReloadNotificationBus::Broadcast(
            &O3DESharpHotReloadNotifications::OnAfterUserAssemblyReload);

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
        if (!m_initialized || m_userAssemblies.empty())
        {
            return nullptr;
        }

        // Check cache first
        auto it = m_userTypeCache.find(fullTypeName);
        if (it != m_userTypeCache.end())
        {
            return it->second;
        }

        // Search every loaded user assembly. First match wins.
        const std::string_view typeNameView(fullTypeName.c_str(), fullTypeName.size());
        for (Coral::ManagedAssembly* assembly : m_userAssemblies)
        {
            if (assembly == nullptr)
            {
                continue;
            }

            Coral::Type& type = assembly->GetLocalType(typeNameView);
            if (type)
            {
                m_userTypeCache[fullTypeName] = &type;
                return &type;
            }
        }

        AZLOG_WARN("CoralHostManager: User type not found in any loaded assembly: %s", fullTypeName.c_str());
        return nullptr;
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

    Coral::HostInstance* CoralHostManager::GetHostInstance()
    {
        return m_hostInstance.get();
    }

    AZ::IO::Path CoralHostManager::GetScriptsDirectory() const
    {
        // m_config.coreApiAssemblyPath is populated by LoadCoreAssembly (either
        // from explicit config or the default <ProjectPath>/Bin/Scripts/O3DE.Core.dll
        // derivation) - see LoadCoreAssembly below. Its parent directory is where
        // every managed assembly (O3DE.Core.dll and user assemblies alike) is
        // deployed, so the thunk host resolves other assemblies relative to it too.
        if (m_config.coreApiAssemblyPath.empty())
        {
            return {};
        }

        return AZ::IO::Path(m_config.coreApiAssemblyPath.c_str()).ParentPath();
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

        // Check if file exists and get its size for debugging
        AZ::u64 fileSize = 0;
        if (!AZ::IO::FileIOBase::GetInstance()->Exists(m_config.coreApiAssemblyPath.c_str()))
        {
            AZLOG_ERROR("CoralHostManager: Core API assembly not found: %s", m_config.coreApiAssemblyPath.c_str());
#if !defined(AZ_RELEASE_BUILD)
            AZLOG_ERROR("CoralHostManager: O3DE.Core.dll must be deployed to: <ProjectPath>/Bin/Scripts/O3DE.Core.dll");
            AZLOG_ERROR("CoralHostManager: To deploy, use the C# Project Manager tool or run:");
            AZLOG_ERROR(
                "  python -c \"from Gems.O3DESharp.Editor.Scripts.csharp_project_manager import CSharpProjectManager; "
                "CSharpProjectManager().deploy_o3de_core()\"");
#endif
            return false;
        }

        // Get file size for debugging
        AZ::IO::FileIOBase::GetInstance()->Size(m_config.coreApiAssemblyPath.c_str(), fileSize);
        AZLOG_INFO("CoralHostManager: File size of O3DE.Core.dll: %llu bytes", fileSize);

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
#if !defined(AZ_RELEASE_BUILD)
            AZLOG_ERROR("CoralHostManager: The file at '%s' is not the correct O3DE.Core assembly.", m_config.coreApiAssemblyPath.c_str());
            AZLOG_ERROR("CoralHostManager: Please ensure O3DE.Core.dll is correctly deployed.");
            AZLOG_ERROR("CoralHostManager: If you have a custom CoreApiAssemblyPath in your .setreg, remove it.");
#endif
        }

        return true;
    }

    bool CoralHostManager::LoadUserAssemblies()
    {
        // Build a deduplicated, ordered list of assemblies to load. The legacy
        // userAssemblyPath (if set) goes last so it doesn't override the explicit list.
        AZStd::vector<AZStd::string> toLoad;
        toLoad.reserve(m_config.userAssemblyPaths.size() + 1);

        auto alreadyQueued = [&toLoad](const AZStd::string& path) -> bool
        {
            for (const auto& existing : toLoad)
            {
                if (existing == path)
                {
                    return true;
                }
            }
            return false;
        };

        for (const auto& path : m_config.userAssemblyPaths)
        {
            if (!path.empty() && !alreadyQueued(path))
            {
                toLoad.push_back(path);
            }
        }
        if (!m_config.userAssemblyPath.empty() && !alreadyQueued(m_config.userAssemblyPath))
        {
            toLoad.push_back(m_config.userAssemblyPath);
        }

        if (toLoad.empty())
        {
            AZLOG_INFO("CoralHostManager: No user assemblies configured");
            return true; // no-op success
        }

        size_t loaded = 0;
        size_t failed = 0;
        for (const AZStd::string& assemblyPath : toLoad)
        {
            AZLOG_INFO("CoralHostManager: Loading user assembly: %s", assemblyPath.c_str());

            if (!AZ::IO::FileIOBase::GetInstance()->Exists(assemblyPath.c_str()))
            {
                AZLOG_ERROR("CoralHostManager: User assembly not found: %s", assemblyPath.c_str());
                ++failed;
                continue;
            }

            // NOTE: O3DE.Core.dll is already loaded in the same unified context
            // (m_userContext == m_coreContext), so the user assembly can resolve
            // its O3DE.Core dependency automatically.
            Coral::ManagedAssembly& assembly = m_userContext.LoadAssembly(std::string(assemblyPath.c_str()));

            if (assembly.GetLoadStatus() != Coral::AssemblyLoadStatus::Success)
            {
                AZLOG_ERROR("CoralHostManager: Failed to load user assembly: %s", assemblyPath.c_str());
                ++failed;
                continue;
            }

            m_userAssemblies.push_back(&assembly);
            AZLOG_INFO("CoralHostManager: User assembly loaded: %s", assembly.GetName().data());
            ++loaded;
        }

        // Keep m_userAssembly pointing at the first loaded assembly for back-compat
        // with anything that still calls GetUserAssembly().
        m_userAssembly = m_userAssemblies.empty() ? nullptr : m_userAssemblies.front();

        AZLOG_INFO("CoralHostManager: Loaded %zu user assembly(ies), %zu failed", loaded, failed);
        return loaded > 0 || failed == 0;
    }

    bool CoralHostManager::LoadUserAssembly()
    {
        // Legacy single-assembly entry point. Delegate to the multi-assembly loader
        // by temporarily routing the legacy path through it. This keeps the public
        // API stable while ensuring the same logic (existence check, log lines,
        // context routing) runs in one place.
        if (m_config.userAssemblyPath.empty() && m_config.userAssemblyPaths.empty())
        {
            AZLOG_INFO("CoralHostManager: No user assembly path specified");
            return false;
        }
        return LoadUserAssemblies();
    }

    void CoralHostManager::RegisterInternalCalls()
    {
        if (m_coreAssembly == nullptr)
        {
            AZLOG_ERROR("CoralHostManager::RegisterInternalCalls - Core assembly not loaded");
            return;
        }

        AZLOG_INFO("CoralHostManager: Registering internal calls...");

        // Register all hand-written C++ functions exposed to C# via ScriptBindings.
        // This sets the static function pointer fields in O3DE.InternalCalls (C#).
        // Must happen BEFORE user assemblies are loaded, in case loading triggers
        // type initialization that calls into O3DE.Core.
        ScriptBindings::RegisterAll(m_coreAssembly);

        // Register any auto-generated bindings emitted by the binding
        // generator into Code/Source/Scripting/Generated/. The placeholder
        // version of this function is a no-op; once the generator has run
        // it adds one AddInternalCall per [O3DE_EXPORT_CSHARP] method.
        // Phase 15 wired this in - same assembly object so both registries
        // populate the same InternalCalls field set on the C# side.
        Generated::RegisterBindings(m_coreAssembly);

        AZLOG_INFO("CoralHostManager: Internal calls registered successfully");
    }
} // namespace O3DESharp
