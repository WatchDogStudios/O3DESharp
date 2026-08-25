/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

#pragma once

#include <AzCore/Memory/SystemAllocator.h>
#include <AzCore/IO/Path/Path.h>

#include <Scripting/IManagedHost.h>

namespace O3DESharp
{
    //! IManagedHost over a NativeAOT-compiled managed library. Desktop
    //! shipping builds only.
    //!
    //! The direction is INVERTED relative to CoralHost. CoralHost brings up a
    //! CLR, uploads NativeImports through Coral's AddInternalCall, and resolves
    //! managed statics by name. This host does none of that: the managed side
    //! is an ordinary native shared library, so it is loaded with
    //! LoadLibrary/dlopen and exactly ONE exported symbol is resolved -
    //! O3DESharp_GetManagedExports - which takes both ABI structs at once. C++
    //! imports the exports rather than uploading calls.
    //!
    //! There is deliberately no Coral and no hostfxr here. A NativeAOT image
    //! has no JIT and is not a hostfxr consumer; there is nothing here for a
    //! hostfxr-resolving loader to attach to. The two hosting models are
    //! mutually exclusive per build artifact, which is exactly why they are
    //! two classes behind one interface rather than one class with a flag.
    class NativeAotHost final
        : public IManagedHost
    {
    public:
        AZ_RTTI(NativeAotHost, "{5D7F1A44-9B2C-4C0E-8E5A-3B6D2F81C7A4}", IManagedHost);
        AZ_CLASS_ALLOCATOR(NativeAotHost, AZ::SystemAllocator);

        //! libraryPath is the NativeAOT shared library produced by
        //! `dotnet publish -p:PublishAot=true -p:NativeLib=Shared`, deployed
        //! next to the launcher (Bin/Scripts/aot/).
        explicit NativeAotHost(AZ::IO::Path libraryPath);
        ~NativeAotHost() override;

        // IManagedHost
        CoralHostStatus Initialize(const Abi::NativeImports& imports) override;
        const Abi::ManagedExports* GetExports() const override;
        bool SupportsHotReload() const override;
        void Shutdown() override;

    private:
        //! Signature of the single exported entry point. Must match
        //! ManagedExportsThunks.O3DESharp_GetManagedExports exactly.
        using GetManagedExportsFn = int (*)(const Abi::NativeImports*, Abi::ManagedExports*);

        AZ::IO::Path m_libraryPath;

        //! Opaque module handle (HMODULE on Windows, void* from dlopen
        //! elsewhere). void* so the header pulls in no platform headers.
        void* m_module = nullptr;

        Abi::ManagedExports m_exports{};
        bool m_exportsValid = false;
    };
} // namespace O3DESharp
