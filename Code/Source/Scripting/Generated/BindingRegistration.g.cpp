/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 */

// PLACEHOLDER FILE - committed so fresh clones build.
//
// The binding generator (Code/Tools/BindingGenerator/O3DESharp.BindingGenerator)
// overwrites this file with per-method stub definitions and AddInternalCall
// registration calls when it runs against the O3DESharp gem's headers. Until
// the first generation pass executes, this minimal version provides empty
// Register/Unregister/HotReload entry points so the runtime gem links and
// the build is green for everyone, including new contributors who haven't
// invoked the generator yet.
//
// The hand-written core internal calls (Entity_*, Transform_*, Vector3_*, ...)
// are registered by ScriptBindings::RegisterAll. The Generated:: registry below
// is for any *additional* auto-generated bindings the generator emits when run
// against the O3DESharp public headers.

#include <Coral/Assembly.hpp>
#include <AzCore/Console/ILogger.h>
#include "O3DESharp_HotReload.g.h"

namespace O3DESharp::Generated
{
    void RegisterBindings(Coral::ManagedAssembly* assembly)
    {
        if (assembly == nullptr)
        {
            AZLOG_ERROR("Generated::RegisterBindings - Assembly is null");
            return;
        }
        // Placeholder body: no auto-generated bindings have been emitted yet.
        // After running the binding generator this function gets replaced with
        // one AddInternalCall per [O3DE_EXPORT_CSHARP]-marked method.
    }

    void UnregisterBindings(Coral::ManagedAssembly* assembly)
    {
        if (assembly == nullptr)
        {
            return;
        }
        HotReloadCallbacks::OnBeforeUnload("O3DESharp");
    }

    bool HotReload(Coral::ManagedAssembly* oldAssembly, Coral::ManagedAssembly* newAssembly)
    {
        if (oldAssembly != nullptr)
        {
            UnregisterBindings(oldAssembly);
        }
        if (newAssembly != nullptr)
        {
            RegisterBindings(newAssembly);
            HotReloadCallbacks::OnAfterLoad("O3DESharp");
            return true;
        }
        return false;
    }
}
