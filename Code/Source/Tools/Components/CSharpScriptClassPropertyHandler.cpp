/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

#include "CSharpScriptClassPropertyHandler.h"
#include "Tools/CSharpEditorToolsBus.h"

#include <AzCore/Serialization/EditContext.h>
#include <AzToolsFramework/UI/PropertyEditor/PropertyQTConstants.h>

#include <QVBoxLayout>
#include <QHBoxLayout>
#include <QLabel>
#include <QPushButton>
#include <QToolButton>
#include <QLineEdit>
#include <QStandardItemModel>
#include <QStyledItemDelegate>
#include <QPainter>
#include <QStyle>

namespace O3DESharp
{
    /**
     * Custom item delegate for displaying script classes with rich formatting
     */
    class ScriptClassItemDelegate : public QStyledItemDelegate
    {
    public:
        explicit ScriptClassItemDelegate(QObject* parent = nullptr)
            : QStyledItemDelegate(parent)
        {
        }

        void paint(QPainter* painter, const QStyleOptionViewItem& option, const QModelIndex& index) const override
        {
            QStyleOptionViewItem opt = option;
            
            // Get the text
            QString text = index.data(Qt::DisplayRole).toString();
            
            // Check if this is a recent item (we'll mark these with a prefix in the data)
            bool isRecent = index.data(Qt::UserRole).toBool();
            
            // Draw background
            if (option.state & QStyle::State_Selected)
            {
                painter->fillRect(option.rect, option.palette.highlight());
            }
            else if (option.state & QStyle::State_MouseOver)
            {
                QColor hoverColor = option.palette.highlight().color();
                hoverColor.setAlpha(50);
                painter->fillRect(option.rect, hoverColor);
            }
            
            // Draw text with recent indicator
            painter->save();
            
            if (option.state & QStyle::State_Selected)
            {
                painter->setPen(option.palette.highlightedText().color());
            }
            else
            {
                painter->setPen(option.palette.text().color());
            }
            
            QRect textRect = option.rect.adjusted(5, 2, -5, -2);
            
            if (isRecent)
            {
                // Draw recent indicator
                QFont boldFont = option.font;
                boldFont.setBold(true);
                painter->setFont(boldFont);
                painter->drawText(textRect, Qt::AlignLeft | Qt::AlignVCenter, "🕐 " + text);
            }
            else
            {
                painter->drawText(textRect, Qt::AlignLeft | Qt::AlignVCenter, text);
            }
            
            painter->restore();
        }
    };

    CSharpScriptClassPropertyHandler::CSharpScriptClassPropertyHandler()
    {
    }

    AZ::u32 CSharpScriptClassPropertyHandler::GetHandlerName() const
    {
        return AZ_CRC_CE("CSharpScriptClass");
    }

    QWidget* CSharpScriptClassPropertyHandler::CreateGUI(QWidget* parent)
    {
        QWidget* container = new QWidget(parent);
        QVBoxLayout* mainLayout = new QVBoxLayout(container);
        mainLayout->setContentsMargins(0, 0, 0, 0);
        mainLayout->setSpacing(2);
        
        // Top row: ComboBox with edit capability
        QComboBox* comboBox = new QComboBox(container);
        comboBox->setEditable(true);
        comboBox->setInsertPolicy(QComboBox::NoInsert);
        comboBox->setMinimumWidth(200);
        comboBox->setSizePolicy(QSizePolicy::Expanding, QSizePolicy::Fixed);
        
        // Set custom item delegate for rich formatting
        comboBox->setItemDelegate(new ScriptClassItemDelegate(comboBox));
        
        // Enable auto-completion
        QCompleter* completer = new QCompleter(comboBox);
        completer->setCaseSensitivity(Qt::CaseInsensitive);
        completer->setFilterMode(Qt::MatchContains);
        comboBox->setCompleter(completer);
        
        // Populate the combo box
        PopulateComboBox(comboBox);
        
        mainLayout->addWidget(comboBox);
        
        // Bottom row: Action buttons
        QHBoxLayout* buttonLayout = new QHBoxLayout();
        buttonLayout->setContentsMargins(0, 0, 0, 0);
        buttonLayout->setSpacing(2);
        
        QPushButton* browseBtn = new QPushButton("Browse", container);
        browseBtn->setToolTip("Open full script browser with search and create options");
        browseBtn->setMaximumWidth(80);
        
        QPushButton* createBtn = new QPushButton("Create", container);
        createBtn->setToolTip("Create a new C# script file");
        createBtn->setMaximumWidth(80);
        
        QToolButton* editBtn = new QToolButton(container);
        editBtn->setText("New File");
        editBtn->setToolTip("Open script in default IDE");
        editBtn->setMaximumWidth(30);
        
        buttonLayout->addWidget(browseBtn);
        buttonLayout->addWidget(createBtn);
        buttonLayout->addWidget(editBtn);
        buttonLayout->addStretch();
        
        mainLayout->addLayout(buttonLayout);
        
        // Connect button signals
        QObject::connect(browseBtn, &QPushButton::clicked, [comboBox]()
        {
            AZStd::string currentClass = comboBox->currentText().toUtf8().constData();
            AZStd::string selectedClass;
            
            CSharpEditorToolsBus::BroadcastResult(selectedClass, &CSharpEditorToolsBus::Events::OpenScriptPicker, currentClass);
            
            if (!selectedClass.empty())
            {
                comboBox->setCurrentText(selectedClass.c_str());
                comboBox->lineEdit()->selectAll();
            }
        });
        
        QObject::connect(createBtn, &QPushButton::clicked, [this, comboBox]()
        {
            AZStd::string createdClass;
            CSharpEditorToolsBus::BroadcastResult(createdClass, &CSharpEditorToolsBus::Events::CreateNewScript, "", "");
            
            if (!createdClass.empty())
            {
                // Refresh combo box to include new script
                PopulateComboBox(comboBox);
                comboBox->setCurrentText(createdClass.c_str());
                comboBox->lineEdit()->selectAll();
            }
        });
        
        QObject::connect(editBtn, &QToolButton::clicked, [comboBox]()
        {
            AZStd::string currentClass = comboBox->currentText().toUtf8().constData();
            if (!currentClass.empty())
            {
                CSharpEditorToolsBus::Broadcast(&CSharpEditorToolsBus::Events::OpenScriptInEditor, currentClass);
            }
        });
        
        // Store comboBox as the primary widget for property access
        container->setProperty("comboBox", QVariant::fromValue(static_cast<QWidget*>(comboBox)));
        
        return container;
    }

    void CSharpScriptClassPropertyHandler::ConsumeAttribute(
        QWidget* widget, 
        AZ::u32 attrib, 
        AzToolsFramework::PropertyAttributeReader* attrValue, 
        [[maybe_unused]] const char* debugName)
    {
        // Get the combo box from the container
        QComboBox* comboBox = widget->property("comboBox").value<QWidget*>()
            ? qobject_cast<QComboBox*>(widget->property("comboBox").value<QWidget*>())
            : nullptr;
        
        if (!comboBox)
        {
            return;
        }
        
        if (attrib == AZ::Edit::Attributes::ReadOnly)
        {
            bool readOnly = false;
            if (attrValue->Read<bool>(readOnly))
            {
                comboBox->setEnabled(!readOnly);
            }
        }
        else if (attrib == AZ_CRC_CE("RefreshScriptList"))
        {
            // Refresh attribute triggers a reload of the script list
            PopulateComboBox(comboBox);
        }
    }

    void CSharpScriptClassPropertyHandler::WriteGUIValuesIntoProperty(
        [[maybe_unused]] size_t index,
        QWidget* GUI,
        property_t& instance,
        [[maybe_unused]] AzToolsFramework::InstanceDataNode* node)
    {
        QComboBox* comboBox = GUI->property("comboBox").value<QWidget*>()
            ? qobject_cast<QComboBox*>(GUI->property("comboBox").value<QWidget*>())
            : nullptr;
        
        if (comboBox)
        {
            QString text = comboBox->currentText();
            instance = text.toUtf8().constData();
            
            // Add to recent classes
            if (!text.isEmpty())
            {
                AZStd::string className = text.toUtf8().constData();
                CSharpEditorToolsBus::Broadcast(&CSharpEditorToolsBus::Events::AddToRecentClasses, className);
            }
        }
    }

    bool CSharpScriptClassPropertyHandler::ReadValuesIntoGUI(
        [[maybe_unused]] size_t index,
        QWidget* GUI,
        const property_t& instance,
        [[maybe_unused]] AzToolsFramework::InstanceDataNode* node)
    {
        QComboBox* comboBox = GUI->property("comboBox").value<QWidget*>()
            ? qobject_cast<QComboBox*>(GUI->property("comboBox").value<QWidget*>())
            : nullptr;
        
        if (comboBox)
        {
            // Block signals while updating
            QSignalBlocker blocker(comboBox);
            
            QString text = instance.c_str();
            
            // Find the item in the combo box
            int foundIndex = comboBox->findText(text);
            if (foundIndex >= 0)
            {
                comboBox->setCurrentIndex(foundIndex);
            }
            else
            {
                // Not in list - set the text directly (allows manual entry)
                comboBox->setCurrentText(text);
            }
            
            return true;
        }
        
        return false;
    }

    void CSharpScriptClassPropertyHandler::PopulateComboBox(QComboBox* comboBox)
    {
        if (!comboBox)
        {
            return;
        }
        
        // Block signals while populating
        QSignalBlocker blocker(comboBox);
        
        // Save current selection
        QString currentText = comboBox->currentText();
        
        // Clear existing items
        comboBox->clear();
        
        // Add empty option
        comboBox->addItem("(No Script)", QVariant(false));
        
        // Get available script classes from Python via EBus
        AZStd::vector<AZStd::string> classNames;
        CSharpEditorToolsBus::BroadcastResult(classNames, &CSharpEditorToolsBus::Events::GetScriptClassNames, true);
        
        // Get full class info to check for recent items
        AZStd::vector<ScriptClassInfo> classInfos;
        CSharpEditorToolsBus::BroadcastResult(classInfos, &CSharpEditorToolsBus::Events::GetAvailableScriptClasses, true);
        
        // Create a map of class name to isRecent flag
        AZStd::unordered_map<AZStd::string, bool> recentMap;
        for (const auto& info : classInfos)
        {
            recentMap[info.m_fullName] = info.m_isRecent;
        }
        
        // Add separator after recent items (if any exist)
        bool hasRecent = false;
        for (const auto& className : classNames)
        {
            bool isRecent = recentMap[className];
            if (isRecent)
            {
                hasRecent = true;
                break;
            }
        }
        
        int separatorIndex = -1;
        for (size_t i = 0; i < classNames.size(); ++i)
        {
            const auto& className = classNames[i];
            bool isRecent = recentMap[className];
            
            // Add separator before first non-recent item
            if (hasRecent && separatorIndex < 0 && !isRecent)
            {
                comboBox->insertSeparator(static_cast<int>(comboBox->count()));
                separatorIndex = static_cast<int>(comboBox->count()) - 1;
            }
            
            comboBox->addItem(className.c_str(), QVariant(isRecent));
        }
        
        // Restore previous selection if it still exists
        if (!currentText.isEmpty())
        {
            int index = comboBox->findText(currentText);
            if (index >= 0)
            {
                comboBox->setCurrentIndex(index);
            }
            else
            {
                comboBox->setCurrentText(currentText);
            }
        }
        
        // Update completer model
        if (comboBox->completer())
        {
            comboBox->completer()->setModel(comboBox->model());
        }
    }

} // namespace O3DESharp
