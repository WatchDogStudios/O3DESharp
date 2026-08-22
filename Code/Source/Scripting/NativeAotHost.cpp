/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

#include "NativeAotHost.h"

#include <AzCore/Console/ILogger.h>
#include <AzCore/PlatformIncl.h>

#if !AZ_TRAIT_OS_PLATFORM_APPLE && !defined(AZ_PLATFORM_WINDOWS)
#include <dlfcn.h>
#elif AZ_TRAIT_OS_PLATFORM_APPLE
#include <dlfcn.h>
#endif

namespace O3DESharp
{
    namespace
    {
        //! The one symbol the whole ABI travels through.
        constexpr const char* GetManagedExportsSymbol = "O3DESharp_GetManagedExports";

        void* LoadModule(const char* path)
        {
#if defined(AZ_PLATFORM_WINDOWS)
            return reinterpret_cast<void*>(::LoadLibraryA(path));
#else
            // RTLD_LOCAL so the scripting image's symbols do not leak into the
            // global namespace and collide with the launcher's.
            return ::dlopen(path, RTLD_NOW | RTLD_LOCAL);
#endif
        }

        void* FindSymbol(void* module, const char* name)
        {
            if (module == nullptr)
            {
                return nullptr;
            }
#if defined(AZ_PLATFORM_WINDOWS)
            return reinterpret_cast<void*>(::GetProcAddress(reinterpret_cast<HMODULE>(module), name));
#else
            return ::dlsym(module, name);
#endif
        }

        void UnloadModule(void* module)
        {
            if (module == nullptr)
            {
                return;
            }
#if defined(AZ_PLATFORM_WINDOWS)
            ::FreeLibrary(reinterpret_cast<HMODULE>(module));
#else
            ::dlclose(module);
#endif
        }
    } // namespace

    NativeAotHost::NativeAotHost(AZ::IO::Path libraryPath)
        : m_libraryPath(AZStd::move(libraryPath))
    {
    }

    NativeAotHost::~NativeAotHost()
    {
        Shutdown();
    }

    CoralHostStatus NativeAotHost::Initialize(const Abi::NativeImports& imports)
    {
        if (m_module != nullptr)
        {
            return CoralHostStatus::AlreadyInitialized;
        }

        if (imports.version != Abi::HostAbiVersion)
        {
            AZLOG_ERROR(
                "NativeAotHost: refusing to initialize - NativeImports version %u, host ABI version %u",
                imports.version,
                Abi::HostAbiVersion);
            return CoralHostStatus::CoralInitError;
        }

        AZLOG_INFO("NativeAotHost: loading %s", m_libraryPath.c_str());
        m_module = LoadModule(m_libraryPath.c_str());
        if (m_module == nullptr)
        {
            AZLOG_ERROR(
                "NativeAotHost: could not load the NativeAOT scripting library at %s. "
                "It is produced by the O3DESharp.PublishNativeAot build target and deployed "
                "to Bin/Scripts/aot/.",
                m_libraryPath.c_str());
            return CoralHostStatus::CoralManagedNotFound;
        }

        auto getExports = reinterpret_cast<GetManagedExportsFn>(
            FindSymbol(m_module, GetManagedExportsSymbol));
        if (getExports == nullptr)
        {
            AZLOG_ERROR(
                "NativeAotHost: %s does not export %s. The library was almost certainly published "
                "without -p:O3DESharpHostMode=NativeAot, so HostExportsGenerator emitted no entry point.",
                m_libraryPath.c_str(),
                GetManagedExportsSymbol);
            UnloadModule(m_module);
            m_module = nullptr;
            return CoralHostStatus::AssemblyLoadFailed;
        }

        // The entire ABI exchange - one call, both structs. This is the
        // inverted direction: nothing is uploaded, the exports are imported.
        Abi::ManagedExports exports{};
        if (getExports(&imports, &exports) != 1)
        {
            AZLOG_ERROR(
                "NativeAotHost: %s rejected the import table. The shipping image was built "
                "against a different ABI version than this host.",
                GetManagedExportsSymbol);
            UnloadModule(m_module);
            m_module = nullptr;
            return CoralHostStatus::CoralInitError;
        }

        if (exports.version != Abi::HostAbiVersion)
        {
            AZLOG_ERROR(
                "NativeAotHost: ManagedExports version %u, host ABI version %u - refusing rather "
                "than reinterpreting the table",
                exports.version,
                Abi::HostAbiVersion);
            UnloadModule(m_module);
            m_module = nullptr;
            return CoralHostStatus::CoralInitError;
        }

        // Unlike the Coral path there is NO fallback here: without a CLR there
        // is no ManagedObject::InvokeMethod to degrade to. A null pointer in a
        // version-matched table means the image is malformed, so fail loudly at
        // startup rather than at the first dispatch.
        if (exports.CreateInstance == nullptr || exports.InvokeLifecycle == nullptr ||
            exports.DispatchEBusEvent == nullptr || exports.DestroyInstance == nullptr ||
            exports.HotReloadSwap == nullptr)
        {
            AZLOG_ERROR("NativeAotHost: ManagedExports contains a null entry - the image is malformed");
            UnloadModule(m_module);
            m_module = nullptr;
            return CoralHostStatus::AssemblyLoadFailed;
        }

        m_exports = exports;
        m_exportsValid = true;
        AZLOG_INFO("NativeAotHost: initialized (ABI v%u, no CoreCLR, no hostfxr)", exports.version);
        return CoralHostStatus::Success;
    }

    const Abi::ManagedExports* NativeAotHost::GetExports() const
    {
        return m_exportsValid ? &m_exports : nullptr;
    }

    // Unconditional, not a probe: hot-reload is editor-only by design and
    // there is no AssemblyLoadContext in a NativeAOT image to swap.
    bool NativeAotHost::SupportsHotReload() const
    {
        return false;
    }

    void NativeAotHost::Shutdown()
    {
        m_exportsValid = false;
        m_exports = {};

        if (m_module != nullptr)
        {
            UnloadModule(m_module);
            m_module = nullptr;
            AZLOG_INFO("NativeAotHost: shutdown complete");
        }
    }
} // namespace O3DESharp
