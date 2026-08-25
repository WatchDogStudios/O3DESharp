/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

#pragma once

#include <AzCore/RTTI/RTTI.h>
#include <AzCore/Interface/Interface.h>

#include <Scripting/HostAbi.h>

namespace O3DESharp
{
    //! The one seam between the C++ gem and whatever is hosting the managed
    //! scripting layer.
    //!
    //! Implementations differ ONLY in how the two ABI structs are exchanged:
    //!
    //!   CoralHost     - editor. NativeImports go up through Coral's
    //!                   AddInternalCall/UploadInternalCalls; ManagedExports
    //!                   come back as [UnmanagedCallersOnly] statics resolved
    //!                   by name through CoralNativeThunkHost. Hot-reload
    //!                   re-resolves the exports per ALC swap.
    //!
    //!   NativeAotHost - desktop shipping. The managed side is a NativeAOT
    //!                   native library; C++ dlopen/LoadLibrary's it and
    //!                   resolves one exported symbol. The direction is
    //!                   inverted - C++ IMPORTS exports rather than uploading
    //!                   calls - and there is no Coral and no hostfxr at all.
    //!
    //! Kept to exactly four methods on purpose: every additional one is
    //! something every future backend has to implement.
    class IManagedHost
    {
    public:
        AZ_RTTI(IManagedHost, "{2E4E4E1B-6C3B-4E5B-9E3B-0B1D9C6A5F21}");

        virtual ~IManagedHost() = default;

        //! Hand the native import table to the managed side and bring the host
        //! up. The caller builds the struct with
        //! ScriptBindings::MakeNativeImports().
        virtual CoralHostStatus Initialize(const Abi::NativeImports& imports) = 0;

        //! The managed export table, or nullptr before a successful Initialize
        //! (or after a failed export resolve). Callers MUST null-check: on the
        //! Coral path a failed resolve is survivable and falls back to
        //! ManagedObject::InvokeMethod, exactly as SP-1a established.
        virtual const Abi::ManagedExports* GetExports() const = 0;

        //! False on shipping AOT backends. Hot-reload is editor-only by design:
        //! a NativeAOT image has no AssemblyLoadContext to reload into.
        virtual bool SupportsHotReload() const = 0;

        //! Tear the host down. Must be safe to call when Initialize failed or
        //! was never called.
        virtual void Shutdown() = 0;
    };

    using ManagedHostInterface = AZ::Interface<IManagedHost>;
} // namespace O3DESharp
