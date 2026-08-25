/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */
#pragma once

#include <AzCore/IO/Path/Path.h>
#include <AzCore/std/string/string.h>
#include <AzCore/std/string/string_view.h>
#include <AzCore/std/containers/unordered_map.h>

namespace Coral
{
    class HostInstance;
}

namespace O3DESharp
{
    //! Memoizing cache over Coral::HostInstance::GetFunctionPointer.
    //!
    //! Coral's only native->managed call path, ManagedObject::InvokeMethod,
    //! does a managed-side string lookup plus reflection dispatch on EVERY
    //! call. Resolving an [UnmanagedCallersOnly] static to a raw function
    //! pointer once removes that cost from every subsequent call.
    //!
    //! Get() returns nullptr when the host is unset or resolution fails.
    //! Callers MUST treat nullptr as "use InvokeMethod instead" - the
    //! fallback is a first-class path, not an error case.
    class CoralNativeThunkHost
    {
    public:
        using PinnedThunk = void*;

        //! Set the live host and the directory managed assemblies are deployed
        //! to (the Bin/Scripts dir holding O3DE.Core.dll). Must be called after
        //! CoralHostManager has brought up the CLR. Changing either clears the
        //! cache.
        void SetHost(Coral::HostInstance* host, const AZ::IO::Path& scriptsDir);

        //! Resolve (and memoize) an [UnmanagedCallersOnly] managed static.
        //! Returns nullptr when unavailable - callers MUST fall back to
        //! InvokeMethod; that is a first-class path, not an error.
        //!
        //! assemblyFileName          e.g. "O3DE.Core.dll" (resolved under scriptsDir)
        //! assemblyQualifiedTypeName e.g. "O3DE.Interop.ScriptComponentBridge, O3DE.Core"
        //! methodName                e.g. "Invoke"
        PinnedThunk Get(
            AZStd::string_view assemblyFileName,
            AZStd::string_view assemblyQualifiedTypeName,
            AZStd::string_view methodName);

        //! Drop all cached pointers. MUST be called on assembly reload -
        //! a pointer into an unloaded ALC is dangling.
        void InvalidateCache();

        //! Diagnostic: number of currently memoized thunks.
        size_t CachedCount() const;

    private:
        Coral::HostInstance* m_host = nullptr;
        AZ::IO::Path m_scriptsDir;
        AZStd::unordered_map<AZStd::string, PinnedThunk> m_cache;
    };
} // namespace O3DESharp
