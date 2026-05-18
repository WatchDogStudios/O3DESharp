/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 */

#pragma once

#include <AzCore/Component/EntityId.h>
#include <AzCore/EBus/EBus.h>
#include <AzCore/std/containers/unordered_map.h>
#include <AzCore/std/string/string.h>

namespace O3DESharp
{
    /**
     * Notification bus broadcast by the editor-side
     * <c>CSharpExposedPropertiesHandler</c> when the user edits an
     * <c>[ExposedProperty]</c> value in the inspector.
     *
     * Addressed by <c>AZ::EntityId</c> - the runtime
     * <c>CSharpScriptComponent</c> connects with its own entity ID on
     * Activate, so a property edit on the editor entity propagates to
     * the matching runtime entity automatically (editor and runtime
     * entities share the same EntityId in O3DE's slice/prefab spawn
     * model). The runtime handler updates its config map and re-calls
     * <c>PushExposedPropertiesToScript</c> so the running managed
     * instance sees the new value without a Reload Scripts or a
     * full re-enter of Game Mode.
     *
     * The flow is:
     *   1. User edits a typed widget in the inspector
     *   2. <c>CSharpExposedPropertiesHandler::WriteGUIValuesIntoProperty</c>
     *      writes the new value into the editor component's config map
     *   3. The handler broadcasts <c>OnExposedPropertyChanged</c> here,
     *      addressed by the editor component's entity ID
     *   4. The runtime <c>CSharpScriptComponent</c> on the matching
     *      entity receives the event and updates its own config +
     *      pushes to the managed instance
     *
     * Doesn't fire during editor-only state (no game mode entered):
     * the runtime component doesn't exist yet, so the bus has no
     * subscribers. On Game Mode entry, <c>BuildGameEntity</c> already
     * copies the latest value map to the runtime config, so the
     * not-firing case is harmless.
     */
    class O3DESharpExposedPropertyNotifications
        : public AZ::EBusTraits
    {
    public:
        // One handler per entity. Edits address a specific entity's
        // runtime script component, so we don't want a broadcast model.
        static constexpr AZ::EBusAddressPolicy AddressPolicy = AZ::EBusAddressPolicy::ById;
        using BusIdType = AZ::EntityId;
        static constexpr AZ::EBusHandlerPolicy HandlerPolicy = AZ::EBusHandlerPolicy::Single;

        virtual ~O3DESharpExposedPropertyNotifications() = default;

        /**
         * Fired when the user commits a value change to any
         * <c>[ExposedProperty]</c> field on this entity's script.
         * The new map replaces the previous contents wholesale - the
         * handler may serialize the entire map, not just the diff,
         * because the underlying widget model has no concept of
         * per-field deltas.
         *
         * The runtime handler should update its local config map AND
         * push the new values to the managed instance.
         */
        virtual void OnExposedPropertyChanged(
            const AZStd::unordered_map<AZStd::string, AZStd::string>& newValues) = 0;
    };

    using O3DESharpExposedPropertyNotificationBus = AZ::EBus<O3DESharpExposedPropertyNotifications>;
} // namespace O3DESharp
