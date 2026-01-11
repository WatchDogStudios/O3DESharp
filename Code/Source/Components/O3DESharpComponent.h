/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

#pragma once

#include <Components/O3DESharpComponentController.h>
#include <AzFramework/Components/ComponentAdapter.h>

namespace O3DESharp
{
    inline constexpr AZ::TypeId O3DESharpComponentTypeId { "{8D1C4761-755D-4162-963C-93FCB1A92842}" };

    class O3DESharpComponent final
        : public AzFramework::Components::ComponentAdapter<O3DESharpComponentController, O3DESharpComponentConfig>
    {
    public:
        using BaseClass = AzFramework::Components::ComponentAdapter<O3DESharpComponentController, O3DESharpComponentConfig>;
        AZ_COMPONENT(O3DESharpComponent, O3DESharpComponentTypeId, BaseClass);

        O3DESharpComponent() = default;
        O3DESharpComponent(const O3DESharpComponentConfig& config);

        static void Reflect(AZ::ReflectContext* context);
    };
}
