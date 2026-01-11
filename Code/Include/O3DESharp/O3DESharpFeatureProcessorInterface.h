/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

#pragma once

#include <AzCore/base.h>
#include <Atom/RPI.Public/FeatureProcessor.h>

namespace O3DESharp
{
    class O3DESharp;

    using O3DESharpHandle = AZStd::shared_ptr<O3DESharp>;

    // O3DESharpFeatureProcessorInterface provides an interface to the feature processor for code outside of Atom
    class O3DESharpFeatureProcessorInterface
        : public AZ::RPI::FeatureProcessor
    {
    public:
        AZ_RTTI(O3DESharpFeatureProcessorInterface, "{8353D656-CAA6-4280-84A5-94B32F7F01AA}", AZ::RPI::FeatureProcessor);

    };
}
