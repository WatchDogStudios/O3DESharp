/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

#include "O3DESharpFeatureProcessor.h"

namespace O3DESharp
{
    void O3DESharpFeatureProcessor::Reflect(AZ::ReflectContext* context)
    {
        if (auto* serializeContext = azrtti_cast<AZ::SerializeContext*>(context))
        {
            // Guard against double-reflection (can happen when both runtime and editor modules load)
            if (serializeContext->FindClassData(azrtti_typeid<O3DESharpFeatureProcessor>()))
            {
                return;
            }

            serializeContext
                ->Class<O3DESharpFeatureProcessor, FeatureProcessor>()
                ;
        }
    }

    void O3DESharpFeatureProcessor::Activate()
    {

    }

    void O3DESharpFeatureProcessor::Deactivate()
    {
        
    }

    void O3DESharpFeatureProcessor::Simulate([[maybe_unused]] const FeatureProcessor::SimulatePacket& packet)
    {
        
    }    
}
