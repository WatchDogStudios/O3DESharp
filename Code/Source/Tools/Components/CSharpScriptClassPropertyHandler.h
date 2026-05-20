/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

#pragma once

#include <AzToolsFramework/UI/PropertyEditor/PropertyEditorAPI.h>
#include <QComboBox>
#include <QCompleter>
#include <QSortFilterProxyModel>

namespace O3DESharp
{
    /**
     * Property handler for C# script class selection
     * 
     * Provides a combo box with:
     * - Type-to-filter functionality
     * - Recent classes shown first
     * - Visual indicators for validation state
     * - Integration with browse/create/edit buttons
     */
    class CSharpScriptClassPropertyHandler
        : public AzToolsFramework::PropertyHandler<AZStd::string, QWidget>
    {
    public:
        AZ_CLASS_ALLOCATOR(CSharpScriptClassPropertyHandler, AZ::SystemAllocator);

        CSharpScriptClassPropertyHandler();

        AZ::u32 GetHandlerName() const override;
        QWidget* CreateGUI(QWidget* parent) override;
        void ConsumeAttribute(QWidget* widget, AZ::u32 attrib, AzToolsFramework::PropertyAttributeReader* attrValue, const char* debugName) override;
        void WriteGUIValuesIntoProperty(size_t index, QWidget* GUI, property_t& instance, AzToolsFramework::InstanceDataNode* node) override;
        bool ReadValuesIntoGUI(size_t index, QWidget* GUI, const property_t& instance, AzToolsFramework::InstanceDataNode* node) override;

    protected:
        void PopulateComboBox(QComboBox* comboBox);
        void OnComboBoxTextChanged(QComboBox* comboBox, const AZStd::string& text);
    };

} // namespace O3DESharp
