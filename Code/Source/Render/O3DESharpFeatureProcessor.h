/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

#pragma once

#include <O3DESharp/O3DESharpFeatureProcessorInterface.h>

namespace O3DESharp
{
    class O3DESharpFeatureProcessor final
        : public O3DESharpFeatureProcessorInterface
    {
    public:
        AZ_RTTI(O3DESharpFeatureProcessor, "{36E79648-2933-40AA-871F-20B52F088E71}", O3DESharpFeatureProcessorInterface);
        AZ_CLASS_ALLOCATOR(O3DESharpFeatureProcessor, AZ::SystemAllocator)

        static void Reflect(AZ::ReflectContext* context);

        O3DESharpFeatureProcessor() = default;
        virtual ~O3DESharpFeatureProcessor() = default;

        // FeatureProcessor overrides
        void Activate() override;
        void Deactivate() override;
        void Simulate(const FeatureProcessor::SimulatePacket& packet) override;

    };
}
