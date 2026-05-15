/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

#pragma once

#include <AzCore/std/containers/unordered_map.h>
#include <AzCore/std/string/string.h>
#include <AzToolsFramework/UI/PropertyEditor/PropertyEditorAPI.h>

#include <QWidget>

namespace O3DESharp
{
    /**
     * Phase 10 scaffolding for the typed exposed-property inspector widgets.
     *
     * Wraps a `AZStd::unordered_map<AZStd::string, AZStd::string>` (the field
     * `CSharpScriptComponentConfig::m_exposedPropertyValues` stores) and is
     * registered with `PropertyTypeRegistrationMessages::RegisterPropertyType`
     * under the CRC `"CSharpExposedProperties"`.
     *
     * **This handler is intentionally NOT wired up to the EditContext yet.**
     * The Phase 7 generic key/value editor remains the user-visible inspector
     * surface; switching the `DataElement` UIHandler from `Default` to
     * `AZ_CRC_CE("CSharpExposedProperties")` is a follow-up commit that
     * needs in-editor validation per typed widget. This file exists so that
     * follow-up has somewhere to drop the per-type Qt widget code without
     * having to re-figure-out the property-handler registration pattern.
     *
     * The placeholder `CreateGUI` queries the schema via
     * `O3DESharpRequestBus::GetExposedPropertySchemaJson` and shows a
     * one-line summary so the handler is observably wired without
     * pretending to render real editors yet.
     */
    class CSharpExposedPropertiesHandler
        : public AzToolsFramework::PropertyHandler<AZStd::unordered_map<AZStd::string, AZStd::string>, QWidget>
    {
    public:
        AZ_CLASS_ALLOCATOR(CSharpExposedPropertiesHandler, AZ::SystemAllocator);

        CSharpExposedPropertiesHandler();

        AZ::u32 GetHandlerName() const override;

        QWidget* CreateGUI(QWidget* parent) override;

        void ConsumeAttribute(
            QWidget* widget,
            AZ::u32 attrib,
            AzToolsFramework::PropertyAttributeReader* attrValue,
            const char* debugName) override;

        void WriteGUIValuesIntoProperty(
            size_t index,
            QWidget* GUI,
            property_t& instance,
            AzToolsFramework::InstanceDataNode* node) override;

        bool ReadValuesIntoGUI(
            size_t index,
            QWidget* GUI,
            const property_t& instance,
            AzToolsFramework::InstanceDataNode* node) override;
    };
} // namespace O3DESharp
