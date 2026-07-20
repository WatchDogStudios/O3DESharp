/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

#include "CoralNativeThunkHost.h"

#include <AzCore/Console/ILogger.h>
#include <AzCore/std/string/conversions.h>

// Coral's PUBLIC Core.hpp - gives us CORAL_STR / CORAL_WIDE_CHARS / UCChar /
// UCString / CORAL_HOSTFXR_NAME / CORAL_UNMANAGED_CALLERS_ONLY without
// reaching into Coral's Source/ (private) tree. This header lives under
// Coral.Native/Include, already on this gem's include path via the
// Coral.Native CMake link target (see o3desharp_private_files.cmake's
// BUILD_DEPENDENCIES).
#include <Coral/Core.hpp>

// Microsoft's PUBLIC .NET hosting headers, vendored alongside Coral in its
// NetCore/ directory. These are NOT Coral-proprietary (they ship verbatim
// from dotnet/runtime's nethost package) - Coral.Native's own HostInstance.cpp
// includes the exact same two headers to do the exact same
// hdt_load_assembly_and_get_function_pointer dance for ITS OWN bootstrap
// (see HostInstance.cpp's LoadCoralManagedFunctionPtr). NetCore/ is
// Coral.Native's SYSTEM PRIVATE include dir (not re-exported), so
// Code/CMakeLists.txt adds ${coral_SOURCE_DIR}/NetCore directly to this
// gem's Private.Object target - see that file's comment for why.
#include <hostfxr.h>
#include <coreclr_delegates.h>

#include <filesystem>
#include <array>

#if defined(AZ_PLATFORM_WINDOWS)
#include <AzCore/PlatformIncl.h>
#endif

namespace O3DESharp
{
    namespace
    {
        // Mirrors Coral::StatusCode's two "still success" codes (see
        // Coral.Native/Source/Coral/HostFXRErrorCodes.hpp - a PRIVATE header
        // this gem cannot include). These are Microsoft's own documented
        // hostfxr status codes (learn.microsoft.com "corehost_error_codes"),
        // not Coral-proprietary values, so duplicating the two we need here
        // is duplicating a stable public contract, not vendoring Coral
        // internals.
        constexpr int32_t k_hostfxrSuccess = 0;
        constexpr int32_t k_hostfxrSuccessHostAlreadyInitialized = 0x00000001;
        constexpr int32_t k_hostfxrSuccessDifferentRuntimeProperties = 0x00000002;

        bool IsSuccessStatus(int32_t status)
        {
            return status == k_hostfxrSuccess ||
                   status == k_hostfxrSuccessHostAlreadyInitialized ||
                   status == k_hostfxrSuccessDifferentRuntimeProperties;
        }

        // Re-derive the hostfxr.dll path the same way Coral::HostInstance's
        // internal (private, un-reachable) GetHostFXRPath does: search
        // "%ProgramFiles%/dotnet/host/fxr/<version>/hostfxr.dll" for the
        // highest '9.x' version directory. Duplicated here (rather than
        // reused) because HostInstance::LoadHostFXR/GetHostFXRPath are
        // private with no public accessor - see this class's header comment
        // for the full "why not extend Coral in place" rationale.
        std::filesystem::path FindHostFxrPath()
        {
#if defined(AZ_PLATFORM_WINDOWS)
            wchar_t programFiles[MAX_PATH]{};
            if (SHGetSpecialFolderPathW(nullptr, programFiles, CSIDL_PROGRAM_FILES, FALSE) == FALSE)
            {
                return {};
            }

            std::filesystem::path basePath = programFiles;
            basePath /= L"dotnet\\host\\fxr\\";

            if (!std::filesystem::exists(basePath))
            {
                return {};
            }

            std::filesystem::path best;
            for (const auto& dir : std::filesystem::directory_iterator(basePath))
            {
                if (!dir.is_directory())
                {
                    continue;
                }
                const auto dirName = dir.path().filename().string();
                if (dirName.empty() || dirName[0] != '9')
                {
                    continue;
                }
                auto candidate = dir.path() / CORAL_HOSTFXR_NAME;
                if (std::filesystem::exists(candidate))
                {
                    // Lexicographic compare on the version-directory name is
                    // good enough here (we only ever expect one 9.x SDK
                    // installed in the common case; Coral's own resolver
                    // makes the same simplifying assumption - see
                    // HostInstance.cpp's GetHostFXRPath, which takes the
                    // FIRST '9'-prefixed directory it finds via
                    // recursive_directory_iterator with no explicit
                    // "newest wins" comparison either).
                    if (best.empty() || dir.path().filename().string() > best.filename().string())
                    {
                        best = candidate;
                    }
                }
            }
            return best;
#else
            // Non-Windows resolution deferred - PART 2 of this task is
            // scoped to the platform this branch is authored/verified on
            // (see the task's HARD CONSTRAINTS: author-only, no build).
            // Coral's own GetHostFXRPath has the POSIX search-path table
            // this would mirror when ported.
            return {};
#endif
        }

        template<typename TFunc>
        TFunc LoadFn(void* library, const char* name)
        {
#if defined(AZ_PLATFORM_WINDOWS)
            return reinterpret_cast<TFunc>(GetProcAddress(reinterpret_cast<HMODULE>(library), name));
#else
            AZ_UNUSED(library);
            AZ_UNUSED(name);
            return nullptr;
#endif
        }
    } // namespace

    CoralNativeThunkHost::CoralNativeThunkHost() = default;

    CoralNativeThunkHost::~CoralNativeThunkHost()
    {
        Shutdown();
    }

    bool CoralNativeThunkHost::Initialize(const AZStd::string& coralDirectory)
    {
        if (m_initialized)
        {
            AZLOG_WARN("CoralNativeThunkHost::Initialize - already initialized");
            return true;
        }

        const std::filesystem::path runtimeConfigPath =
            std::filesystem::path(coralDirectory.c_str()) / "Coral.Managed.runtimeconfig.json";
        if (!std::filesystem::exists(runtimeConfigPath))
        {
            AZLOG_ERROR(
                "CoralNativeThunkHost::Initialize - Coral.Managed.runtimeconfig.json not found at '%s'. "
                "This host must be initialized AFTER CoralHostManager::Initialize has already brought up "
                "the CLR against this same directory.",
                coralDirectory.c_str());
            return false;
        }

        const std::filesystem::path hostfxrPath = FindHostFxrPath();
        if (hostfxrPath.empty())
        {
            AZLOG_ERROR("CoralNativeThunkHost::Initialize - could not locate hostfxr (.NET 9 SDK not found)");
            return false;
        }

#if defined(AZ_PLATFORM_WINDOWS)
        // LoadLibraryW on an already-loaded module (Coral's own HostInstance
        // already loaded this exact hostfxr.dll) returns the SAME module
        // handle and increments its reference count - this does not load a
        // second copy of hostfxr or spin up a second CLR. This is the crux
        // of why this class can exist without touching Coral's internals:
        // hostfxr.dll's process-wide state (including the already-running
        // CoreCLR) is shared, we're just obtaining our OWN handle to the
        // SAME already-loaded module + re-deriving our OWN context handle
        // against it.
        HMODULE library = LoadLibraryW(hostfxrPath.c_str());
#else
        void* library = nullptr;
#endif
        if (library == nullptr)
        {
            AZLOG_ERROR("CoralNativeThunkHost::Initialize - failed to load hostfxr at '%s'", hostfxrPath.string().c_str());
            return false;
        }
        m_hostfxrLibrary = library;

        auto initForConfig = LoadFn<hostfxr_initialize_for_runtime_config_fn>(m_hostfxrLibrary, "hostfxr_initialize_for_runtime_config");
        auto getRuntimeDelegate = LoadFn<hostfxr_get_runtime_delegate_fn>(m_hostfxrLibrary, "hostfxr_get_runtime_delegate");
        auto closeHostFxr = LoadFn<hostfxr_close_fn>(m_hostfxrLibrary, "hostfxr_close");

        if (initForConfig == nullptr || getRuntimeDelegate == nullptr || closeHostFxr == nullptr)
        {
            AZLOG_ERROR("CoralNativeThunkHost::Initialize - failed to resolve hostfxr entry points");
            return false;
        }

        hostfxr_handle ctx = nullptr;
        const int32_t initStatus = initForConfig(runtimeConfigPath.c_str(), nullptr, &ctx);

        // A second hostfxr_initialize_for_runtime_config call against a
        // runtime config that's ALREADY governing a running CLR in this
        // process is documented .NET hosting behavior to return
        // Success_HostAlreadyInitialized (or _DifferentRuntimeProperties if
        // some property already diverges - still treated as success, same
        // tolerance Coral's own HostInstance.cpp applies at the equivalent
        // call). We EXPECT this call to be the SECOND init in the process
        // (Coral's own HostInstance::InitializeCoralManaged already ran
        // first) - see Initialize()'s header doc for the ordering
        // requirement this relies on.
        if (!IsSuccessStatus(initStatus) || ctx == nullptr)
        {
            AZLOG_ERROR(
                "CoralNativeThunkHost::Initialize - hostfxr_initialize_for_runtime_config failed (status=0x%x). "
                "Was this called before CoralHostManager::Initialize brought up the CLR?",
                static_cast<unsigned int>(initStatus));
            return false;
        }
        m_hostfxrContext = ctx;

        void* delegateFn = nullptr;
        const int32_t delegateStatus = getRuntimeDelegate(
            ctx, hostfxr_delegate_type::hdt_load_assembly_and_get_function_pointer, &delegateFn);
        if (!IsSuccessStatus(delegateStatus) || delegateFn == nullptr)
        {
            AZLOG_ERROR(
                "CoralNativeThunkHost::Initialize - hostfxr_get_runtime_delegate(hdt_load_assembly_and_get_function_pointer) "
                "failed (status=0x%x)",
                static_cast<unsigned int>(delegateStatus));
            return false;
        }
        m_loadAssemblyAndGetFunctionPointerFn = delegateFn;

        m_initialized = true;
        AZLOG_INFO("CoralNativeThunkHost: initialized (native->managed pinned-thunk resolver ready)");
        return true;
    }

    void CoralNativeThunkHost::Shutdown()
    {
        m_thunkCache.clear();
        m_loadAssemblyAndGetFunctionPointerFn = nullptr;

        // Deliberately do NOT call hostfxr_close(m_hostfxrContext): the
        // context is shared with the SAME running CLR Coral's own
        // HostInstance manages, and Coral owns the runtime's shutdown
        // sequence via its own HostInstance::Shutdown. Closing our context
        // handle here is safe re: hostfxr's own refcounting (closing a
        // secondary handle to an already-initialized host does not tear
        // down the CLR - only the LAST close does), but this class doesn't
        // own the "is anyone else still using the CLR" answer, so it leaves
        // teardown entirely to CoralHostManager and just drops its own
        // references. FreeLibrary is similarly skipped for the same
        // shared-module-refcount reason (Coral's own HostInstance keeps its
        // own handle open for the process lifetime).
        m_hostfxrContext = nullptr;
        m_hostfxrLibrary = nullptr;
        m_initialized = false;
    }

    CoralNativeThunkHost::PinnedThunk CoralNativeThunkHost::GetPinnedThunk(
        const AZStd::string& assemblyQualifiedTypeName, const AZStd::string& methodName)
    {
        if (!m_initialized || m_loadAssemblyAndGetFunctionPointerFn == nullptr)
        {
            AZLOG_ERROR("CoralNativeThunkHost::GetPinnedThunk - host not initialized");
            return nullptr;
        }

        const AZStd::string cacheKey = assemblyQualifiedTypeName + "::" + methodName;
        auto cached = m_thunkCache.find(cacheKey);
        if (cached != m_thunkCache.end())
        {
            return cached->second;
        }

        auto fn = reinterpret_cast<load_assembly_and_get_function_pointer_fn>(m_loadAssemblyAndGetFunctionPointerFn);

#if defined(CORAL_WIDE_CHARS)
        const AZStd::wstring wideType = AZStd::to_wstring(assemblyQualifiedTypeName.c_str());
        const AZStd::wstring wideMethod = AZStd::to_wstring(methodName.c_str());
        const Coral::UCChar* typeArg = reinterpret_cast<const Coral::UCChar*>(wideType.c_str());
        const Coral::UCChar* methodArg = reinterpret_cast<const Coral::UCChar*>(wideMethod.c_str());
#else
        const Coral::UCChar* typeArg = assemblyQualifiedTypeName.c_str();
        const Coral::UCChar* methodArg = methodName.c_str();
#endif

        void* delegatePtr = nullptr;

        // assembly_path is null here (last-loaded-wins / already-loaded
        // resolution - the sim-lane assembly must already be loaded into
        // the SAME AssemblyLoadContext Coral itself loads user assemblies
        // into, exactly the way Coral's own LoadCoralManagedFunctionPtr
        // resolves Coral.Managed.dll's own types without a fresh
        // assembly_path once the assembly is already loaded via the
        // default context). delegate_type_name is
        // CORAL_UNMANAGED_CALLERS_ONLY, requiring the managed method be
        // decorated [UnmanagedCallersOnly] - hostfxr enforces this at
        // resolve time and fails (returns non-success + null delegate) if
        // it isn't.
        const int32_t status = fn(
            nullptr,
            typeArg,
            methodArg,
            CORAL_UNMANAGED_CALLERS_ONLY,
            nullptr,
            &delegatePtr);

        if (!IsSuccessStatus(status) || delegatePtr == nullptr)
        {
            AZLOG_WARN(
                "CoralNativeThunkHost::GetPinnedThunk - failed to resolve '%s.%s' (status=0x%x). "
                "Confirm the method is `static` and marked [UnmanagedCallersOnly], and that its "
                "assembly is already loaded.",
                assemblyQualifiedTypeName.c_str(), methodName.c_str(), static_cast<unsigned int>(status));
            m_thunkCache[cacheKey] = nullptr;
            return nullptr;
        }

        m_thunkCache[cacheKey] = delegatePtr;
        return delegatePtr;
    }

    void CoralNativeThunkHost::InvalidateCache()
    {
        m_thunkCache.clear();
    }

} // namespace O3DESharp
