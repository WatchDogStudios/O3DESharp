/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

#include "CoralHost.h"

#include <AzCore/Console/ILogger.h>

namespace O3DESharp
{
    namespace
    {
        // Assembly-qualified name of the generated thunk holder. Emitted by
        // HostExportsGenerator into O3DE.Core as
        // O3DE.Interop.ManagedExportsThunks.
        constexpr const char* ExportsAssembly = "O3DE.Core.dll";
        constexpr const char* ExportsTypeName = "O3DE.Interop.ManagedExportsThunks, O3DE.Core";
    } // namespace

    CoralHost::CoralHost(CoralHostManager& manager, const CoralHostConfig& config)
        : m_manager(manager)
        , m_config(config)
    {
    }

    CoralHost::~CoralHost()
    {
        // Deliberately does NOT call Shutdown(): the manager is owned by
        // O3DESharpSystemComponent, which shuts it down on its own schedule.
        // Tearing down someone else's host from a destructor would change the
        // editor's shutdown ordering, which M3 must not do.
        InvalidateExports();
    }

    CoralHostStatus CoralHost::Initialize(const Abi::NativeImports& imports)
    {
        if (imports.version != Abi::HostAbiVersion)
        {
            AZLOG_ERROR(
                "CoralHost::Initialize - NativeImports version %u does not match host ABI version %u; refusing to run",
                imports.version,
                Abi::HostAbiVersion);
            return CoralHostStatus::CoralInitError;
        }

        // The manager owns CLR bring-up, assembly loading and the
        // AddInternalCall/UploadInternalCalls upload of exactly these pointers.
        // Nothing about that path changes here.
        const CoralHostStatus status = m_manager.Initialize(m_config);
        if (status != CoralHostStatus::Success)
        {
            return status;
        }

        m_thunkHost.SetHost(m_manager.GetHostInstance(), m_manager.GetScriptsDirectory());

        if (!ResolveExports())
        {
            // Survivable: GetExports() returns nullptr and callers fall back to
            // ManagedObject::InvokeMethod, exactly as SP-1a established. A
            // missing thunk costs speed, never correctness.
            AZLOG_WARN(
                "CoralHost: ManagedExports could not be fully resolved - callers will fall back to InvokeMethod");
        }

        return CoralHostStatus::Success;
    }

    bool CoralHost::ResolveExports()
    {
        m_exportsValid = false;
        m_exports = {};

        auto resolve = [this](const char* methodName) -> void*
        {
            return m_thunkHost.Get(ExportsAssembly, ExportsTypeName, methodName);
        };

        Abi::ManagedExports exports{};
        exports.version = Abi::HostAbiVersion;
        exports.CreateInstance = resolve("O3DESharp_CreateInstance");
        exports.InvokeLifecycle = resolve("O3DESharp_InvokeLifecycle");
        exports.DispatchEBusEvent = resolve("O3DESharp_DispatchEBusEvent");
        exports.DestroyInstance = resolve("O3DESharp_DestroyInstance");
        exports.HotReloadSwap = resolve("O3DESharp_HotReloadSwap");

        // All-or-nothing on purpose. A partially-populated table would let a
        // caller find one pointer, skip its fallback, and then hit a null on
        // the next one - a much harder failure to read than "no exports".
        if (exports.CreateInstance == nullptr || exports.InvokeLifecycle == nullptr ||
            exports.DispatchEBusEvent == nullptr || exports.DestroyInstance == nullptr ||
            exports.HotReloadSwap == nullptr)
        {
            return false;
        }

        m_exports = exports;
        m_exportsValid = true;
        AZLOG_INFO("CoralHost: resolved all %d ManagedExports thunks", 5);
        return true;
    }

    const Abi::ManagedExports* CoralHost::GetExports() const
    {
        return m_exportsValid ? &m_exports : nullptr;
    }

    bool CoralHost::SupportsHotReload() const
    {
        // Reports the existing gate rather than a hard-coded true:
        // O3DESharpSystemComponent sets enableHotReload on in Debug/Profile and
        // off in Release (O3DESharpSystemComponent.cpp:793-797).
        return m_config.enableHotReload;
    }

    void CoralHost::InvalidateExports()
    {
        m_exportsValid = false;
        m_exports = {};
        m_thunkHost.InvalidateCache();
    }

    void CoralHost::Shutdown()
    {
        InvalidateExports();
        m_manager.Shutdown();
    }
} // namespace O3DESharp
