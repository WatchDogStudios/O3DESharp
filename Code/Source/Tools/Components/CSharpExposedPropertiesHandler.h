/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

#pragma once

#include <AzCore/Component/EntityId.h>
#include <AzCore/std/containers/unordered_map.h>
#include <AzCore/std/string/string.h>
#include <AzToolsFramework/UI/PropertyEditor/PropertyEditorAPI.h>

#include <QWidget>

namespace O3DESharp
{
    /**
     * Property handler that renders <c>[ExposedProperty]</c>-decorated fields
     * of a script class as typed inspector widgets (Phase 14).
     *
     * Wraps an <c>AZStd::unordered_map&lt;AZStd::string, AZStd::string&gt;</c>
     * (the <c>CSharpScriptComponentConfig::m_exposedPropertyValues</c> field).
     * Registered with <c>PropertyTypeRegistrationMessages</c> under
     * <c>AZ_CRC_CE("CSharpExposedProperties")</c>.
     *
     * The widget tree is built from the schema JSON returned by
     * <c>O3DESharpRequests::GetExposedPropertySchemaJson</c> for the script
     * class currently selected in the sibling <c>m_scriptClassName</c> field.
     * The class name is plumbed in via a custom Edit attribute
     * (<c>AZ_CRC_CE("ScriptClassNameAttr")</c>) so the handler doesn't have
     * to navigate <c>InstanceDataNode</c> pointers to find its sibling.
     *
     * Supported widget types this slice: <c>QCheckBox</c> (bool),
     * <c>QSpinBox</c> (int/uint/short/ushort/byte/sbyte), <c>QDoubleSpinBox</c>
     * (float/double, with a wide range so it doesn't artificially clamp),
     * <c>QLineEdit</c> (string + fallback for any unknown type tag).
     * Larger integer types (long / ulong) also use QLineEdit pending a
     * 64-bit-range spin widget.
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

    private:
        /// <summary>
        /// Walk up the InstanceDataNode chain from the field node to
        /// find the EditorCSharpScriptComponent that owns the config,
        /// then return its entity id. Used by WriteGUIValuesIntoProperty
        /// to broadcast property changes to the matching runtime
        /// CSharpScriptComponent over O3DESharpExposedPropertyNotificationBus.
        /// Returns an invalid EntityId if the walk fails (rare; usually
        /// indicates we're being called outside the normal inspector flow).
        /// </summary>
        AZ::EntityId ResolveOwningEntityId(AzToolsFramework::InstanceDataNode* startNode) const;
    };
} // namespace O3DESharp
