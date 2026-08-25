/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

#pragma once

#include <AzCore/Memory/SystemAllocator.h>

#include <Scripting/IManagedHost.h>
#include <Scripting/CoralHostManager.h>
#include <Scripting/CoralNativeThunkHost.h>

namespace O3DESharp
{
    //! IManagedHost over the existing CoralHostManager.
    //!
    //! A WRAPPING refactor, not a rewrite: this class owns nothing. It holds a
    //! reference to the CoralHostManager O3DESharpSystemComponent already
    //! creates and delegates to it, so every behaviour of the editor path -
    //! ALC lifecycle, the unified core/user context, hot-reload broadcast
    //! ordering - is exactly what it was.
    //!
    //! What it adds is the seam: Initialize takes the frozen NativeImports
    //! struct, and GetExports resolves the five ManagedExports thunks once
    //! through CoralNativeThunkHost (the SP-1a memoizing cache over Coral's
    //! GetFunctionPointer).
    //!
    //! Deliberate and worth stating plainly: under Coral the NativeImports
    //! struct is DESCRIPTIVE. The transport is still
    //! assembly->AddInternalCall + UploadInternalCalls in
    //! ScriptBindings::RegisterAll, untouched. Building the struct anyway is
    //! what proves both ends agree on the frozen field order (the golden test
    //! in Editor/Tests/test_host_abi_contract.py checks the declarations; this
    //! checks the population). It becomes the sole transport only under
    //! NativeAotHost.
    class CoralHost final
        : public IManagedHost
    {
    public:
        AZ_RTTI(CoralHost, "{9C1F0F2A-2B77-4E33-9E2A-77B0E2C4A913}", IManagedHost);
        AZ_CLASS_ALLOCATOR(CoralHost, AZ::SystemAllocator);

        //! The manager must outlive this adapter; O3DESharpSystemComponent owns
        //! both and destroys the adapter first.
        explicit CoralHost(CoralHostManager& manager, const CoralHostConfig& config);
        ~CoralHost() override;

        // IManagedHost
        CoralHostStatus Initialize(const Abi::NativeImports& imports) override;
        const Abi::ManagedExports* GetExports() const override;
        bool SupportsHotReload() const override;
        void Shutdown() override;

        //! Drop the resolved export pointers. MUST be called on assembly reload
        //! - an [UnmanagedCallersOnly] pointer into an unloaded ALC is
        //! dangling, and the failure is a crash in managed code with no obvious
        //! link back to the missing call.
        void InvalidateExports();

    private:
        //! Resolve the five thunks. Returns false if any is unavailable, in
        //! which case m_exportsValid stays false and GetExports returns nullptr
        //! so callers fall back to InvokeMethod (SP-1a's first-class fallback).
        bool ResolveExports();

        CoralHostManager& m_manager;
        CoralHostConfig m_config;
        CoralNativeThunkHost m_thunkHost;
        Abi::ManagedExports m_exports{};
        bool m_exportsValid = false;
    };
} // namespace O3DESharp
