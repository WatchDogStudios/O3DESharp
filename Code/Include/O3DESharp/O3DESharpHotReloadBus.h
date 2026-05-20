/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

#pragma once

#include <AzCore/EBus/EBus.h>

namespace O3DESharp
{
    /**
     * Notification bus broadcast by <c>CoralHostManager::ReloadUserAssemblies</c>
     * around an assembly-context unload + reload cycle.
     *
     * Phase 13 addresses a real correctness risk identified in the audit:
     * <c>m_userContext</c> aliased <c>m_coreContext</c>, so reloading user
     * assemblies tore down O3DE.Core as well. Every <c>CSharpScriptComponent</c>
     * cached a <c>Coral::Type*</c> in <c>m_scriptType</c> that became a
     * dangling pointer after the context unload, plus a <c>Coral::ManagedObject</c>
     * in <c>m_scriptInstance</c> with the same problem. The first call into
     * either after the reload would dereference freed memory.
     *
     * Coral can't easily separate the contexts (per the comment in
     * <c>CoralHostManager::Initialize</c>: Coral uses MemoryMappedFile which
     * locks DLLs, and cross-context type resolution is not in this Coral
     * version), so this bus is the alternative: every script component
     * tears down its managed state on <c>OnBeforeUserAssemblyReload</c>
     * and rebuilds it on <c>OnAfterUserAssemblyReload</c>. The notification
     * order matches Coral's lifecycle - "before" fires before
     * <c>UnloadAssemblyLoadContext</c>, "after" fires after the new context
     * + assemblies are ready and internal calls are re-registered.
     */
    class O3DESharpHotReloadNotifications
        : public AZ::EBusTraits
    {
    public:
        static constexpr AZ::EBusHandlerPolicy HandlerPolicy = AZ::EBusHandlerPolicy::Multiple;
        static constexpr AZ::EBusAddressPolicy AddressPolicy = AZ::EBusAddressPolicy::Single;

        virtual ~O3DESharpHotReloadNotifications() = default;

        /**
         * Fired before the Coral assembly context is unloaded. Handlers must
         * release any <c>Coral::ManagedObject</c> / <c>Coral::Type*</c> /
         * <c>Coral::ManagedAssembly*</c> they cached - those handles all
         * become dangling pointers the instant the context unload runs.
         */
        virtual void OnBeforeUserAssemblyReload() {}

        /**
         * Fired after the new context is up, O3DE.Core has been reloaded,
         * internal calls have been re-registered, and the user assemblies
         * have been re-loaded. Handlers can safely re-resolve types and
         * reconstruct their managed state.
         */
        virtual void OnAfterUserAssemblyReload() {}
    };

    using O3DESharpHotReloadNotificationBus = AZ::EBus<O3DESharpHotReloadNotifications>;
} // namespace O3DESharp
