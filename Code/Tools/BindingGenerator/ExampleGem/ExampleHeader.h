/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

#pragma once

#include "../Scripting/ExportAttributes.h"
#include <AzCore/Math/Vector3.h>
#include <AzCore/Component/EntityId.h>

namespace ExampleGem
{
    /**
     * Example class showing how to use the binding generator.
     * 
     * This class will be automatically exported to C# because the default mode
     * exports all public declarations. The O3DE_EXPORT_CSHARP attribute is optional
     * (only required if you use --require-attribute mode).
     */
    class MyExampleClass
    {
    public:
        /**
         * Get the position of this object.
         * @return The position as a Vector3
         */
        AZ::Vector3 GetPosition() const;

        /**
         * Set the position of this object.
         * @param position The new position
         */
        void SetPosition(const AZ::Vector3& position);

        /**
         * Calculate the distance to another object.
         * @param other The entity ID of the other object
         * @return The distance in world units
         */
        float GetDistanceTo(AZ::EntityId other) const;

        /**
         * A simple property that will be exposed to C#
         */
        bool IsActive;

    private:
        // Private members are NOT exported
        AZ::Vector3 m_position;
        void InternalMethod();
    };

    /**
     * Example enum that will be exported to C#.
     */
    enum class ExampleState
    {
        Idle,
        Running,
        Paused,
        Stopped
    };

    /**
     * Example standalone function.
     * @param name The name to log
     */
    void LogExampleMessage(const char* name);

    /**
     * If you want to use attribute-based export (--require-attribute mode),
     * mark declarations with O3DE_EXPORT_CSHARP like this:
     */
    class O3DE_EXPORT_CSHARP MyAttributeClass
    {
    public:
        O3DE_EXPORT_CSHARP void AttributeMethod();
    };
}
