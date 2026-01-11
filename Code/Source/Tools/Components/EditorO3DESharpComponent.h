/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

#pragma once

#include <Atom/Feature/Utils/EditorRenderComponentAdapter.h>

#include <AzCore/Component/TickBus.h>
#include <AzFramework/Entity/EntityDebugDisplayBus.h>
#include <AzToolsFramework/API/ComponentEntitySelectionBus.h>
#include <AzToolsFramework/Entity/EditorEntityInfoBus.h>
#include <AzToolsFramework/ToolsComponents/EditorComponentAdapter.h>
#include <Components/O3DESharpComponent.h>

#include <O3DESharp/O3DESharpTypeIds.h>

namespace O3DESharp
{
    inline constexpr AZ::TypeId EditorComponentTypeId { "{1116D748-269A-42DE-BC69-88CAD48EDF3F}" };

    class EditorO3DESharpComponent final
        : public AZ::Render::EditorRenderComponentAdapter<O3DESharpComponentController, O3DESharpComponent, O3DESharpComponentConfig>
        , private AzToolsFramework::EditorComponentSelectionRequestsBus::Handler
        , private AzFramework::EntityDebugDisplayEventBus::Handler
        , private AZ::TickBus::Handler
        , private AzToolsFramework::EditorEntityInfoNotificationBus::Handler
    {
    public:
        using BaseClass = AZ::Render::EditorRenderComponentAdapter <O3DESharpComponentController, O3DESharpComponent, O3DESharpComponentConfig>;
        AZ_EDITOR_COMPONENT(EditorO3DESharpComponent, EditorComponentTypeId, BaseClass);

        static void Reflect(AZ::ReflectContext* context);

        EditorO3DESharpComponent();
        EditorO3DESharpComponent(const O3DESharpComponentConfig& config);

        // AZ::Component overrides
        void Activate() override;
        void Deactivate() override;

    private:

        // AZ::TickBus overrides
        void OnTick(float deltaTime, AZ::ScriptTimePoint time) override;


    };
}
