/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

#include "CSharpExposedPropertiesHandler.h"

#include <O3DESharp/O3DESharpBus.h>

#include <AzCore/Console/ILogger.h>

#include <QLabel>
#include <QVBoxLayout>

namespace O3DESharp
{
    CSharpExposedPropertiesHandler::CSharpExposedPropertiesHandler() = default;

    AZ::u32 CSharpExposedPropertiesHandler::GetHandlerName() const
    {
        return AZ_CRC_CE("CSharpExposedProperties");
    }

    QWidget* CSharpExposedPropertiesHandler::CreateGUI(QWidget* parent)
    {
        // Phase 10 placeholder. The Phase 7 generic key/value editor is what
        // the user actually sees in the inspector today; this widget only
        // appears when something opts in by setting the DataElement
        // UIHandler to AZ_CRC_CE("CSharpExposedProperties"), which the gem
        // currently never does (intentional - see header doc).
        //
        // When the follow-up lands, this is where per-schema-entry typed
        // editors get built (QDoubleSpinBox for float / double, QSpinBox
        // for int / long, QCheckBox for bool, QLineEdit for string, etc.).
        // The schema itself comes from
        //   O3DESharpRequestBus::GetExposedPropertySchemaJson(className)
        // which already works; the only missing piece is the Qt widget tree.

        auto* container = new QWidget(parent);
        auto* layout = new QVBoxLayout(container);
        layout->setContentsMargins(0, 0, 0, 0);
        auto* placeholder = new QLabel(
            QStringLiteral("Typed exposed-property editor - placeholder. "
                           "Generic key/value editor is the current default; "
                           "switch the DataElement UIHandler to "
                           "AZ_CRC_CE(\"CSharpExposedProperties\") to opt in."),
            container);
        placeholder->setWordWrap(true);
        layout->addWidget(placeholder);
        return container;
    }

    void CSharpExposedPropertiesHandler::ConsumeAttribute(
        [[maybe_unused]] QWidget* widget,
        [[maybe_unused]] AZ::u32 attrib,
        [[maybe_unused]] AzToolsFramework::PropertyAttributeReader* attrValue,
        [[maybe_unused]] const char* debugName)
    {
        // No attributes consumed yet; per-field widget configuration will
        // come from the schema JSON, not from inspector attributes.
    }

    void CSharpExposedPropertiesHandler::WriteGUIValuesIntoProperty(
        [[maybe_unused]] size_t index,
        [[maybe_unused]] QWidget* GUI,
        [[maybe_unused]] property_t& instance,
        [[maybe_unused]] AzToolsFramework::InstanceDataNode* node)
    {
        // No-op: the placeholder widget has nothing to write back. Real
        // typed widgets will collect their values here and write them into
        // the map keyed by [ExposedProperty] member name.
    }

    bool CSharpExposedPropertiesHandler::ReadValuesIntoGUI(
        [[maybe_unused]] size_t index,
        [[maybe_unused]] QWidget* GUI,
        [[maybe_unused]] const property_t& instance,
        [[maybe_unused]] AzToolsFramework::InstanceDataNode* node)
    {
        // Same here - real implementation pulls each value out of the map
        // and seeds the corresponding typed widget.
        return true;
    }
} // namespace O3DESharp
