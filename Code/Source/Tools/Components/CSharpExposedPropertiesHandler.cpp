/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

#include "CSharpExposedPropertiesHandler.h"

#include <O3DESharp/O3DESharpBus.h>
#include <O3DESharp/O3DESharpExposedPropertyBus.h>

#include <AzCore/Console/ILogger.h>
#include <AzCore/JSON/document.h>
#include <AzCore/JSON/rapidjson.h>
#include <AzCore/std/string/conversions.h>

// O3DE-styled wrappers around the corresponding Qt widgets. SpinBox /
// DoubleSpinBox derive from QSpinBox / QDoubleSpinBox so all the qobject_cast
// checks below continue to work; LineEdit and CheckBox are stylers that
// operate on raw QLineEdit / QCheckBox instances. The widget choices are
// intentionally narrow - the inspector framework expects these specific
// widget kinds for its inline-edit conventions.
#include <AzQtComponents/Components/Widgets/CheckBox.h>
#include <AzQtComponents/Components/Widgets/LineEdit.h>
#include <AzQtComponents/Components/Widgets/SpinBox.h>

#include <QCheckBox>
#include <QFormLayout>
#include <QLabel>
#include <QLineEdit>
#include <QVBoxLayout>
#include <QVariant>

namespace O3DESharp
{
    namespace
    {
        // The Qt-property keys we attach to the container widget to remember
        // (a) which class we're rendering for and (b) the per-field widget
        // pointers so ReadValuesIntoGUI / WriteGUIValuesIntoProperty can
        // round-trip values without re-parsing the schema every call.
        constexpr const char* kClassNamePropKey = "o3desharp.scriptClassName";
        constexpr const char* kBuiltForClassPropKey = "o3desharp.builtForClass";

        // Each rendered field stores its (name, type tag, widget*) tuple as
        // dynamic Qt properties on the container. We also keep a single list
        // of property names in order so the editor's writeback walks them
        // deterministically.
        constexpr const char* kFieldNamesPropKey = "o3desharp.fieldNames";

        // Per-field widget pointer is stored under a key derived from the
        // property name: "o3desharp.field.<name>" -> QWidget*.
        // The type tag is stored under "o3desharp.fieldType.<name>".
        QString FieldWidgetKey(const QString& name) { return QStringLiteral("o3desharp.field.") + name; }
        QString FieldTypeKey(const QString& name) { return QStringLiteral("o3desharp.fieldType.") + name; }

        // Parse the schema JSON the C# side emits via
        // ExposedPropertyHelpers.GetSchemaJson - a flat array of
        // {name, displayName, type, default, tooltip} string objects.
        struct SchemaEntry
        {
            AZStd::string name;
            AZStd::string displayName;
            AZStd::string typeTag;
            AZStd::string defaultValue;
            AZStd::string tooltip;
        };

        AZStd::vector<SchemaEntry> ParseSchemaJson(const AZStd::string& json)
        {
            AZStd::vector<SchemaEntry> result;
            if (json.empty() || json == "[]")
            {
                return result;
            }

            rapidjson::Document doc;
            doc.Parse(json.c_str());
            if (doc.HasParseError() || !doc.IsArray())
            {
                AZ_Warning("O3DESharp", false,
                    "CSharpExposedPropertiesHandler: failed to parse schema JSON; falling back to empty schema. JSON: %s",
                    json.c_str());
                return result;
            }

            auto pickString = [](const rapidjson::Value& obj, const char* field) -> AZStd::string
            {
                if (!obj.HasMember(field)) return {};
                const auto& v = obj[field];
                if (!v.IsString()) return {};
                return AZStd::string(v.GetString(), v.GetStringLength());
            };

            result.reserve(doc.Size());
            for (rapidjson::SizeType i = 0; i < doc.Size(); ++i)
            {
                const auto& obj = doc[i];
                if (!obj.IsObject()) continue;
                SchemaEntry e;
                e.name = pickString(obj, "name");
                if (e.name.empty()) continue;
                e.displayName = pickString(obj, "displayName");
                if (e.displayName.empty()) e.displayName = e.name;
                e.typeTag = pickString(obj, "type");
                e.defaultValue = pickString(obj, "default");
                e.tooltip = pickString(obj, "tooltip");
                result.push_back(AZStd::move(e));
            }
            return result;
        }

        // Build the typed editor widget for a single schema entry. Returns a
        // freshly-allocated QWidget owned by the form layout; nullptr only on
        // unrecoverable failure (caller falls back to a styled line edit).
        // Uses AzQtComponents wrappers so the widgets pick up the editor
        // theme and behave the same as the rest of the inspector.
        QWidget* BuildWidgetForType(const SchemaEntry& entry, const QString& initialValue, QWidget* parent)
        {
            const auto& t = entry.typeTag;
            if (t == "bool")
            {
                // CheckBox is a styling helper that operates on a stock
                // QCheckBox; the editor's default style applies automatically
                // since the parent inherits from an AzQt-themed root, so no
                // explicit applyXxxStyle call is needed here.
                auto* cb = new QCheckBox(parent);
                cb->setChecked(initialValue.compare(QStringLiteral("true"), Qt::CaseInsensitive) == 0);
                return cb;
            }
            if (t == "int" || t == "short" || t == "sbyte")
            {
                auto* sb = new AzQtComponents::SpinBox(parent);
                sb->setRange(std::numeric_limits<int>::lowest(), std::numeric_limits<int>::max());
                bool ok = false;
                int v = initialValue.toInt(&ok);
                if (ok) sb->setValue(v);
                return sb;
            }
            if (t == "uint" || t == "ushort" || t == "byte")
            {
                auto* sb = new AzQtComponents::SpinBox(parent);
                sb->setRange(0, std::numeric_limits<int>::max()); // Qt's QSpinBox is int-only; uint clamps here
                bool ok = false;
                int v = initialValue.toInt(&ok);
                if (ok && v >= 0) sb->setValue(v);
                return sb;
            }
            if (t == "float" || t == "double")
            {
                auto* sb = new AzQtComponents::DoubleSpinBox(parent);
                sb->setRange(-1.0e12, 1.0e12);
                sb->setDecimals(6);
                bool ok = false;
                double v = initialValue.toDouble(&ok);
                if (ok) sb->setValue(v);
                return sb;
            }
            // string, long, ulong, or any unknown type -> stock QLineEdit.
            // AzQtComponents::LineEdit is a styling helper applied via static
            // method calls; the parent's themed root cascades the default
            // appearance to a regular QLineEdit, so we only need an explicit
            // call here if we wanted error/search styling, which we don't
            // for first-pass typed fields. 64-bit integer types deliberately
            // use QLineEdit because Qt's spin widgets are bounded to int;
            // the C# side parses strings either way.
            auto* le = new QLineEdit(parent);
            le->setText(initialValue);
            return le;
        }

        // Read the current value out of a typed widget as a string in the
        // format the C# side parses.
        AZStd::string ReadWidgetValue(QWidget* widget, const AZStd::string& typeTag)
        {
            if (auto* cb = qobject_cast<QCheckBox*>(widget))
            {
                return cb->isChecked() ? AZStd::string("true") : AZStd::string("false");
            }
            if (auto* sb = qobject_cast<QSpinBox*>(widget))
            {
                return AZStd::string::format("%d", sb->value());
            }
            if (auto* dsb = qobject_cast<QDoubleSpinBox*>(widget))
            {
                // "%g" gives us a short round-trip-safe representation
                // matching what C# ExposedPropertyHelpers.SerializeValue emits.
                return AZStd::string::format("%.6g", dsb->value());
            }
            if (auto* le = qobject_cast<QLineEdit*>(widget))
            {
                return AZStd::string(le->text().toUtf8().constData());
            }
            AZ_UNUSED(typeTag);
            return {};
        }
    } // anonymous namespace

    CSharpExposedPropertiesHandler::CSharpExposedPropertiesHandler() = default;

    AZ::u32 CSharpExposedPropertiesHandler::GetHandlerName() const
    {
        return AZ_CRC_CE("CSharpExposedProperties");
    }

    QWidget* CSharpExposedPropertiesHandler::CreateGUI(QWidget* parent)
    {
        // The container owns a QVBoxLayout. The body is a QFormLayout we
        // rebuild every time the script class changes; the rest of the time
        // ReadValuesIntoGUI just updates widget values without recreating
        // the layout.
        auto* container = new QWidget(parent);
        auto* outer = new QVBoxLayout(container);
        outer->setContentsMargins(0, 0, 0, 0);

        // Placeholder line shown until ReadValuesIntoGUI has run at least once.
        auto* placeholder = new QLabel(QStringLiteral("(no script class selected)"), container);
        placeholder->setEnabled(false);
        outer->addWidget(placeholder);

        container->setProperty(kClassNamePropKey, QString{});
        container->setProperty(kBuiltForClassPropKey, QString{});
        container->setProperty(kFieldNamesPropKey, QStringList{});
        return container;
    }

    void CSharpExposedPropertiesHandler::ConsumeAttribute(
        QWidget* widget,
        AZ::u32 attrib,
        AzToolsFramework::PropertyAttributeReader* attrValue,
        [[maybe_unused]] const char* debugName)
    {
        // The EditContext attaches the sibling m_scriptClassName via the
        // AZ_CRC_CE("ScriptClassNameAttr") attribute. ReadValuesIntoGUI uses
        // it to query the schema and (re)build the per-field widgets.
        if (widget == nullptr || attrValue == nullptr) return;
        if (attrib == AZ_CRC_CE("ScriptClassNameAttr"))
        {
            AZStd::string className;
            if (attrValue->Read<AZStd::string>(className))
            {
                widget->setProperty(kClassNamePropKey, QString::fromUtf8(className.c_str()));
            }
        }
    }

    bool CSharpExposedPropertiesHandler::ReadValuesIntoGUI(
        [[maybe_unused]] size_t index,
        QWidget* GUI,
        const property_t& instance,
        [[maybe_unused]] AzToolsFramework::InstanceDataNode* node)
    {
        if (GUI == nullptr) return false;

        const QString className = GUI->property(kClassNamePropKey).toString();
        const QString lastBuiltFor = GUI->property(kBuiltForClassPropKey).toString();

        // If we haven't built the typed widget tree for THIS class yet,
        // (re)build it from the schema. Tearing down the previous layout
        // happens by deleting the child widget Qt object the outer layout
        // holds.
        if (className != lastBuiltFor)
        {
            auto* outerLayout = qobject_cast<QVBoxLayout*>(GUI->layout());
            if (outerLayout == nullptr) return false;

            // Remove existing children
            while (QLayoutItem* item = outerLayout->takeAt(0))
            {
                if (QWidget* w = item->widget())
                {
                    w->deleteLater();
                }
                delete item;
            }

            QStringList fieldNames;

            if (className.isEmpty())
            {
                auto* placeholder = new QLabel(
                    QStringLiteral("(no script class selected - pick one in the Script Class field above)"),
                    GUI);
                placeholder->setEnabled(false);
                outerLayout->addWidget(placeholder);
            }
            else
            {
                AZStd::string schemaJson = "[]";
                O3DESharpRequestBus::BroadcastResult(
                    schemaJson,
                    &O3DESharpRequests::GetExposedPropertySchemaJson,
                    AZStd::string(className.toUtf8().constData()));

                const auto schema = ParseSchemaJson(schemaJson);
                if (schema.empty())
                {
                    auto* placeholder = new QLabel(
                        QStringLiteral("(no [ExposedProperty] fields on this class, or schema not available)"),
                        GUI);
                    placeholder->setEnabled(false);
                    outerLayout->addWidget(placeholder);
                }
                else
                {
                    auto* form = new QFormLayout();
                    form->setContentsMargins(0, 0, 0, 0);
                    for (const auto& entry : schema)
                    {
                        // Seed value: explicit map entry wins, else the
                        // schema default the C# side reported.
                        AZStd::string seed = entry.defaultValue;
                        auto it = instance.find(entry.name);
                        if (it != instance.end())
                        {
                            seed = it->second;
                        }

                        QWidget* w = BuildWidgetForType(entry, QString::fromUtf8(seed.c_str()), GUI);
                        if (w == nullptr) continue;

                        if (!entry.tooltip.empty())
                        {
                            w->setToolTip(QString::fromUtf8(entry.tooltip.c_str()));
                        }

                        QLabel* label = new QLabel(QString::fromUtf8(entry.displayName.c_str()), GUI);
                        if (!entry.tooltip.empty())
                        {
                            label->setToolTip(QString::fromUtf8(entry.tooltip.c_str()));
                        }
                        form->addRow(label, w);

                        const QString qName = QString::fromUtf8(entry.name.c_str());
                        fieldNames.append(qName);
                        GUI->setProperty(FieldWidgetKey(qName).toUtf8().constData(), QVariant::fromValue<QWidget*>(w));
                        GUI->setProperty(FieldTypeKey(qName).toUtf8().constData(), QString::fromUtf8(entry.typeTag.c_str()));
                    }
                    outerLayout->addLayout(form);
                }
            }

            GUI->setProperty(kBuiltForClassPropKey, className);
            GUI->setProperty(kFieldNamesPropKey, fieldNames);
            return true;
        }

        // Layout already matches the class: refresh widget values from the
        // map without rebuilding. Defaults stay where the layout last seeded
        // them so we don't clobber user edits with an empty map entry.
        const QStringList fieldNames = GUI->property(kFieldNamesPropKey).toStringList();
        for (const QString& qName : fieldNames)
        {
            auto it = instance.find(AZStd::string(qName.toUtf8().constData()));
            if (it == instance.end()) continue;

            QWidget* w = GUI->property(FieldWidgetKey(qName).toUtf8().constData()).value<QWidget*>();
            if (w == nullptr) continue;

            const QString seed = QString::fromUtf8(it->second.c_str());
            if (auto* cb = qobject_cast<QCheckBox*>(w))
            {
                cb->setChecked(seed.compare(QStringLiteral("true"), Qt::CaseInsensitive) == 0);
            }
            else if (auto* sb = qobject_cast<QSpinBox*>(w))
            {
                bool ok = false;
                int v = seed.toInt(&ok);
                if (ok) sb->setValue(v);
            }
            else if (auto* dsb = qobject_cast<QDoubleSpinBox*>(w))
            {
                bool ok = false;
                double v = seed.toDouble(&ok);
                if (ok) dsb->setValue(v);
            }
            else if (auto* le = qobject_cast<QLineEdit*>(w))
            {
                if (le->text() != seed) le->setText(seed);
            }
        }
        return true;
    }

    void CSharpExposedPropertiesHandler::WriteGUIValuesIntoProperty(
        [[maybe_unused]] size_t index,
        QWidget* GUI,
        property_t& instance,
        [[maybe_unused]] AzToolsFramework::InstanceDataNode* node)
    {
        if (GUI == nullptr) return;
        const QStringList fieldNames = GUI->property(kFieldNamesPropKey).toStringList();
        for (const QString& qName : fieldNames)
        {
            QWidget* w = GUI->property(FieldWidgetKey(qName).toUtf8().constData()).value<QWidget*>();
            if (w == nullptr) continue;
            const QString qTypeTag = GUI->property(FieldTypeKey(qName).toUtf8().constData()).toString();

            const AZStd::string name(qName.toUtf8().constData());
            const AZStd::string typeTag(qTypeTag.toUtf8().constData());
            instance[name] = ReadWidgetValue(w, typeTag);
        }

        // Push the freshly-committed value map to any live runtime
        // CSharpScriptComponent on the same entity. The
        // O3DESharpExposedPropertyNotificationBus is addressed by
        // entity id; the runtime component connects with its own id
        // on Activate, so this propagates the inspector edit to the
        // running managed instance without requiring a Reload Scripts
        // or a re-enter of Game Mode.
        //
        // Entity-id discovery walks the InstanceDataNode chain from
        // this field (m_exposedPropertyValues) up through
        // EditorCSharpScriptConfig and finds the
        // EditorCSharpScriptComponent that owns the config. The
        // component knows its entity id natively. If the walk fails
        // (unlikely outside hot-reload / shutdown edge cases), we
        // skip the broadcast - the next BuildGameEntity will pick up
        // the new values from the editor config the standard way.
        if (node != nullptr)
        {
            const AZ::EntityId entityId = ResolveOwningEntityId(node);
            if (entityId.IsValid())
            {
                O3DESharpExposedPropertyNotificationBus::Event(
                    entityId,
                    &O3DESharpExposedPropertyNotifications::OnExposedPropertyChanged,
                    instance);
            }
        }
    }

    AZ::EntityId CSharpExposedPropertiesHandler::ResolveOwningEntityId(
        AzToolsFramework::InstanceDataNode* startNode) const
    {
        // Walk up the InstanceDataNode chain looking for a node whose
        // class data identifies an AZ::Component subclass. Cast the
        // instance pointer at that level to AZ::Component and read
        // its entity id. This is the standard InstanceDataNode -> owning
        // component pattern used in O3DE tooling.
        for (AzToolsFramework::InstanceDataNode* n = startNode; n != nullptr; n = n->GetParent())
        {
            const AZ::SerializeContext::ClassData* classData = n->GetClassMetadata();
            if (classData == nullptr || classData->m_azRtti == nullptr) continue;

            // We want EditorCSharpScriptComponent specifically. Its
            // RTTI chains up to AZ::Component, but matching against
            // EditorCSharpScriptComponent's exact RTTI gives us the
            // component instance pointer without ambiguity. We can't
            // include the header without a circular dep so we compare
            // by class name string instead.
            if (classData->m_name != nullptr &&
                strcmp(classData->m_name, "EditorCSharpScriptComponent") == 0)
            {
                void* instancePtr = n->FirstInstance();
                if (instancePtr != nullptr)
                {
                    // EditorCSharpScriptComponent inherits from
                    // AZ::Component, which has GetEntityId. Cast via
                    // AzRtti so we don't need the full type.
                    auto* component = classData->m_azRtti->Cast<AZ::Component>(
                        instancePtr, AZ::Component::TYPEINFO_Uuid());
                    if (component != nullptr)
                    {
                        return component->GetEntityId();
                    }
                }
            }
        }
        return AZ::EntityId();
    }
} // namespace O3DESharp
