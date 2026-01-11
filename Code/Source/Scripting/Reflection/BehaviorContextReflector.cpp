/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

#include "BehaviorContextReflector.h"

#include <AzCore/Console/ILogger.h>
#include <AzCore/std/containers/unordered_set.h>
#include <AzCore/std/sort.h>
#include <AzCore/Math/Vector3.h>
#include <AzCore/Math/Quaternion.h>
#include <AzCore/Math/Transform.h>
#include <AzCore/Component/EntityId.h>
#include <AzCore/Script/ScriptContextAttributes.h>
#include <AzCore/RTTI/AttributeReader.h>

namespace O3DESharp
{
    // ============================================================
    // ReflectedClass helpers
    // ============================================================

    const ReflectedMethod* ReflectedClass::FindMethod(const AZStd::string& methodName) const
    {
        for (const auto& method : methods)
        {
            if (method.name == methodName)
            {
                return &method;
            }
        }
        return nullptr;
    }

    const ReflectedProperty* ReflectedClass::FindProperty(const AZStd::string& propertyName) const
    {
        for (const auto& prop : properties)
        {
            if (prop.name == propertyName)
            {
                return &prop;
            }
        }
        return nullptr;
    }

    // ============================================================
    // BehaviorContextReflector Implementation
    // ============================================================

    void BehaviorContextReflector::ReflectFromContext(AZ::BehaviorContext* context)
    {
        if (!context)
        {
            AZLOG_ERROR("BehaviorContextReflector: Cannot reflect from null BehaviorContext");
            return;
        }

        Clear();
        m_behaviorContext = context;

        AZLOG_INFO("BehaviorContextReflector: Beginning reflection from BehaviorContext...");

        // Reflect all classes
        for (const auto& classPair : context->m_classes)
        {
            if (classPair.second && ShouldExposeToScripting(classPair.second->m_attributes))
            {
                ReflectClass(classPair.first, classPair.second);
            }
        }

        // Reflect all EBuses
        for (const auto& ebusPair : context->m_ebuses)
        {
            if (ebusPair.second && ShouldExposeToScripting(ebusPair.second->m_attributes))
            {
                ReflectEBus(ebusPair.first, ebusPair.second);
            }
        }

        // Reflect global methods
        for (const auto& methodPair : context->m_methods)
        {
            if (methodPair.second && ShouldExposeToScripting(methodPair.second->m_attributes))
            {
                ReflectedMethod method = ReflectMethod(methodPair.first, methodPair.second);
                m_globalMethods.push_back(AZStd::move(method));
            }
        }

        // Reflect global properties
        for (const auto& propPair : context->m_properties)
        {
            if (propPair.second && ShouldExposeToScripting(propPair.second->m_attributes))
            {
                ReflectedProperty prop = ReflectProperty(propPair.first, propPair.second);
                m_globalProperties.push_back(AZStd::move(prop));
            }
        }

        AZLOG_INFO("BehaviorContextReflector: Reflection complete - %zu classes, %zu EBuses, %zu global methods, %zu global properties",
            m_classes.size(), m_ebuses.size(), m_globalMethods.size(), m_globalProperties.size());
    }

    void BehaviorContextReflector::Clear()
    {
        m_classes.clear();
        m_ebuses.clear();
        m_globalMethods.clear();
        m_globalProperties.clear();
        m_marshalTypeCache.clear();
        m_behaviorContext = nullptr;
    }

    AZStd::vector<AZStd::string> BehaviorContextReflector::GetClassNames() const
    {
        AZStd::vector<AZStd::string> names;
        names.reserve(m_classes.size());
        for (const auto& pair : m_classes)
        {
            names.push_back(pair.first);
        }
        return names;
    }

    const ReflectedClass* BehaviorContextReflector::GetClass(const AZStd::string& className) const
    {
        auto it = m_classes.find(className);
        if (it != m_classes.end())
        {
            return &it->second;
        }
        return nullptr;
    }

    AZStd::vector<AZStd::string> BehaviorContextReflector::GetEBusNames() const
    {
        AZStd::vector<AZStd::string> names;
        names.reserve(m_ebuses.size());
        for (const auto& pair : m_ebuses)
        {
            names.push_back(pair.first);
        }
        return names;
    }

    const ReflectedEBus* BehaviorContextReflector::GetEBus(const AZStd::string& busName) const
    {
        auto it = m_ebuses.find(busName);
        if (it != m_ebuses.end())
        {
            return &it->second;
        }
        return nullptr;
    }

    bool BehaviorContextReflector::HasClass(const AZStd::string& className) const
    {
        return m_classes.find(className) != m_classes.end();
    }

    bool BehaviorContextReflector::HasEBus(const AZStd::string& busName) const
    {
        return m_ebuses.find(busName) != m_ebuses.end();
    }

    AZStd::vector<const ReflectedClass*> BehaviorContextReflector::GetClassesByCategory(
        const AZStd::string& category,
        bool includeDeprecated) const
    {
        AZStd::vector<const ReflectedClass*> result;
        for (const auto& pair : m_classes)
        {
            const ReflectedClass& cls = pair.second;
            
            // Filter by deprecated status
            if (!includeDeprecated && cls.isDeprecated)
            {
                continue;
            }

            // Filter by category (empty category matches all)
            if (category.empty() || cls.category == category)
            {
                result.push_back(&cls);
            }
        }
        return result;
    }

    AZStd::vector<const ReflectedClass*> BehaviorContextReflector::GetDerivedClasses(const AZStd::string& baseClassName) const
    {
        AZStd::vector<const ReflectedClass*> result;
        for (const auto& pair : m_classes)
        {
            const ReflectedClass& cls = pair.second;
            for (const auto& baseName : cls.baseClasses)
            {
                if (baseName == baseClassName)
                {
                    result.push_back(&cls);
                    break;
                }
            }
        }
        return result;
    }

    void BehaviorContextReflector::ReflectClass(const AZStd::string& name, AZ::BehaviorClass* behaviorClass)
    {
        if (!behaviorClass)
        {
            return;
        }

        ReflectedClass reflectedClass;
        reflectedClass.name = name;
        reflectedClass.typeId = behaviorClass->m_typeId;
        reflectedClass.behaviorClass = behaviorClass;

        // Extract script attributes
        AZStd::string deprecationMessage;
        ExtractScriptAttributes(
            behaviorClass->m_attributes,
            reflectedClass.description,
            reflectedClass.category,
            reflectedClass.isDeprecated,
            deprecationMessage);

        // Reflect base classes
        for (const auto& baseTypeId : behaviorClass->m_baseClasses)
        {
            // Look up the base class name from the behavior context
            if (m_behaviorContext)
            {
                for (const auto& classPair : m_behaviorContext->m_classes)
                {
                    if (classPair.second && classPair.second->m_typeId == baseTypeId)
                    {
                        reflectedClass.baseClasses.push_back(classPair.first);
                        break;
                    }
                }
            }
        }

        // Reflect methods
        for (const auto& methodPair : behaviorClass->m_methods)
        {
            if (methodPair.second && ShouldExposeToScripting(methodPair.second->m_attributes))
            {
                ReflectedMethod method = ReflectMethod(methodPair.first, methodPair.second, name);
                
                // Determine if this is a static method
                // In O3DE, static methods typically don't have 'this' as the first parameter
                if (methodPair.second->GetNumArguments() == 0 ||
                    !methodPair.second->IsMember())
                {
                    method.isStatic = true;
                }
                
                reflectedClass.methods.push_back(AZStd::move(method));
            }
        }

        // Reflect properties
        for (const auto& propPair : behaviorClass->m_properties)
        {
            if (propPair.second && ShouldExposeToScripting(propPair.second->m_attributes))
            {
                ReflectedProperty prop = ReflectProperty(propPair.first, propPair.second, name);
                reflectedClass.properties.push_back(AZStd::move(prop));
            }
        }

        // Reflect constructors
        if (behaviorClass->m_constructors.size() > 0)
        {
            int constructorIndex = 0;
            for (AZ::BehaviorMethod* constructor : behaviorClass->m_constructors)
            {
                if (constructor)
                {
                    AZStd::string constructorName = AZStd::string::format("Constructor_%d", constructorIndex);
                    ReflectedMethod method = ReflectMethod(constructorName, constructor, name);
                    method.isStatic = true; // Constructors are called as static factory methods
                    reflectedClass.constructors.push_back(AZStd::move(method));
                    constructorIndex++;
                }
            }
        }

        m_classes[name] = AZStd::move(reflectedClass);

        AZLOG_INFO("BehaviorContextReflector: Reflected class '%s' with %zu methods, %zu properties",
            name.c_str(), reflectedClass.methods.size(), reflectedClass.properties.size());
    }

    void BehaviorContextReflector::ReflectEBus(const AZStd::string& name, AZ::BehaviorEBus* behaviorEBus)
    {
        if (!behaviorEBus)
        {
            return;
        }

        ReflectedEBus reflectedEBus;
        reflectedEBus.name = name;
        reflectedEBus.behaviorEBus = behaviorEBus;

        // Extract script attributes
        AZStd::string deprecationMessage;
        bool isDeprecated = false;
        ExtractScriptAttributes(
            behaviorEBus->m_attributes,
            reflectedEBus.description,
            reflectedEBus.category,
            isDeprecated,
            deprecationMessage);

        // Get the address type if this is an addressed bus
        if (behaviorEBus->m_idParam.m_typeId != AZ::Uuid::CreateNull())
        {
            reflectedEBus.addressType = ReflectParameter(behaviorEBus->m_idParam);
        }

        // Reflect events
        for (const auto& eventPair : behaviorEBus->m_events)
        {
            const AZ::BehaviorEBusEventSender& eventSender = eventPair.second;
            
            // Create event entry
            ReflectedEBusEvent reflectedEvent;
            reflectedEvent.name = eventPair.first;
            reflectedEvent.busName = name;
            
            // Get the broadcast or event method
            AZ::BehaviorMethod* method = nullptr;
            if (eventSender.m_broadcast)
            {
                method = eventSender.m_broadcast;
                reflectedEvent.isBroadcast = true;
            }
            else if (eventSender.m_event)
            {
                method = eventSender.m_event;
                reflectedEvent.isBroadcast = false;
            }

            if (method)
            {
                // Reflect return type
                if (method->HasResult())
                {
                    reflectedEvent.returnType = ReflectParameter(*method->GetResult());
                }
                else
                {
                    reflectedEvent.returnType.marshalType = ReflectedParameter::MarshalType::Void;
                    reflectedEvent.returnType.typeName = "void";
                }

                // Reflect parameters (skip 'this' for event handlers)
                size_t startIndex = reflectedEvent.isBroadcast ? 0 : 1;
                for (size_t i = startIndex; i < method->GetNumArguments(); ++i)
                {
                    const AZ::BehaviorParameter* param = method->GetArgument(i);
                    if (param)
                    {
                        reflectedEvent.parameters.push_back(ReflectParameter(*param));
                    }
                }
            }

            reflectedEBus.events.push_back(AZStd::move(reflectedEvent));
        }

        m_ebuses[name] = AZStd::move(reflectedEBus);

        AZLOG_INFO("BehaviorContextReflector: Reflected EBus '%s' with %zu events",
            name.c_str(), reflectedEBus.events.size());
    }

    ReflectedMethod BehaviorContextReflector::ReflectMethod(
        const AZStd::string& name,
        AZ::BehaviorMethod* method,
        const AZStd::string& className)
    {
        ReflectedMethod reflectedMethod;
        reflectedMethod.name = name;
        reflectedMethod.className = className;
        reflectedMethod.behaviorMethod = method;

        if (!method)
        {
            return reflectedMethod;
        }

        // Extract script attributes
        AZStd::string deprecationMessage;
        ExtractScriptAttributes(
            method->m_attributes,
            reflectedMethod.description,
            reflectedMethod.category,
            reflectedMethod.isDeprecated,
            deprecationMessage);

        // Reflect return type
        if (method->HasResult())
        {
            reflectedMethod.returnType = ReflectParameter(*method->GetResult());
        }
        else
        {
            reflectedMethod.returnType.marshalType = ReflectedParameter::MarshalType::Void;
            reflectedMethod.returnType.typeName = "void";
        }

        // Reflect parameters
        // For member methods, the first parameter is typically 'this' - we skip it
        size_t startIndex = method->IsMember() ? 1 : 0;
        
        for (size_t i = startIndex; i < method->GetNumArguments(); ++i)
        {
            const AZ::BehaviorParameter* param = method->GetArgument(i);
            if (param)
            {
                ReflectedParameter reflectedParam = ReflectParameter(*param);
                
                // Try to get parameter name from metadata
                if (i < method->GetNumArguments())
                {
                    const AZStd::string* paramName = method->GetArgumentName(i);
                    if (paramName && !paramName->empty())
                    {
                        reflectedParam.name = *paramName;
                    }
                    else
                    {
                        reflectedParam.name = AZStd::string::format("arg%zu", i - startIndex);
                    }
                }

                reflectedMethod.parameters.push_back(AZStd::move(reflectedParam));
            }
        }

        return reflectedMethod;
    }

    ReflectedProperty BehaviorContextReflector::ReflectProperty(
        const AZStd::string& name,
        AZ::BehaviorProperty* property,
        const AZStd::string& className)
    {
        ReflectedProperty reflectedProperty;
        reflectedProperty.name = name;
        reflectedProperty.className = className;
        reflectedProperty.behaviorProperty = property;

        if (!property)
        {
            return reflectedProperty;
        }

        // Extract script attributes
        AZStd::string deprecationMessage;
        AZStd::string category;
        ExtractScriptAttributes(
            property->m_attributes,
            reflectedProperty.description,
            category,
            reflectedProperty.isDeprecated,
            deprecationMessage);

        // Check for getter
        if (property->m_getter)
        {
            reflectedProperty.hasGetter = true;
            if (property->m_getter->HasResult())
            {
                reflectedProperty.valueType = ReflectParameter(*property->m_getter->GetResult());
            }
        }

        // Check for setter
        if (property->m_setter)
        {
            reflectedProperty.hasSetter = true;
            // If we don't have the type from getter, get it from setter's argument
            if (reflectedProperty.valueType.marshalType == ReflectedParameter::MarshalType::Unknown)
            {
                // Setter's first arg after 'this' is the value
                size_t valueArgIndex = property->m_setter->IsMember() ? 1 : 0;
                if (property->m_setter->GetNumArguments() > valueArgIndex)
                {
                    const AZ::BehaviorParameter* param = property->m_setter->GetArgument(valueArgIndex);
                    if (param)
                    {
                        reflectedProperty.valueType = ReflectParameter(*param);
                    }
                }
            }
        }

        return reflectedProperty;
    }

    ReflectedParameter BehaviorContextReflector::ReflectParameter(const AZ::BehaviorParameter& param)
    {
        ReflectedParameter reflectedParam;
        reflectedParam.typeId = param.m_typeId;
        reflectedParam.typeName = GetTypeName(param.m_typeId);
        
        // Check traits
        reflectedParam.isPointer = (param.m_traits & AZ::BehaviorParameter::TR_POINTER) != 0;
        reflectedParam.isReference = (param.m_traits & AZ::BehaviorParameter::TR_REFERENCE) != 0;
        reflectedParam.isConst = (param.m_traits & AZ::BehaviorParameter::TR_CONST) != 0;
        
        // Determine marshal type
        reflectedParam.marshalType = DetermineMarshalType(param.m_typeId);

        // Get name if available
        if (param.m_name)
        {
            reflectedParam.name = param.m_name;
        }

        return reflectedParam;
    }

    ReflectedParameter::MarshalType BehaviorContextReflector::DetermineMarshalType(const AZ::Uuid& typeId) const
    {
        // Check cache first
        auto it = m_marshalTypeCache.find(typeId);
        if (it != m_marshalTypeCache.end())
        {
            return it->second;
        }

        ReflectedParameter::MarshalType marshalType = ReflectedParameter::MarshalType::Unknown;

        // Check against known types
        if (typeId == AZ::Uuid::CreateNull() || typeId == azrtti_typeid<void>())
        {
            marshalType = ReflectedParameter::MarshalType::Void;
        }
        else if (typeId == azrtti_typeid<bool>())
        {
            marshalType = ReflectedParameter::MarshalType::Bool;
        }
        else if (typeId == azrtti_typeid<AZ::s8>())
        {
            marshalType = ReflectedParameter::MarshalType::Int8;
        }
        else if (typeId == azrtti_typeid<AZ::s16>())
        {
            marshalType = ReflectedParameter::MarshalType::Int16;
        }
        else if (typeId == azrtti_typeid<AZ::s32>() || typeId == azrtti_typeid<int>())
        {
            marshalType = ReflectedParameter::MarshalType::Int32;
        }
        else if (typeId == azrtti_typeid<AZ::s64>())
        {
            marshalType = ReflectedParameter::MarshalType::Int64;
        }
        else if (typeId == azrtti_typeid<AZ::u8>())
        {
            marshalType = ReflectedParameter::MarshalType::UInt8;
        }
        else if (typeId == azrtti_typeid<AZ::u16>())
        {
            marshalType = ReflectedParameter::MarshalType::UInt16;
        }
        else if (typeId == azrtti_typeid<AZ::u32>() || typeId == azrtti_typeid<unsigned int>())
        {
            marshalType = ReflectedParameter::MarshalType::UInt32;
        }
        else if (typeId == azrtti_typeid<AZ::u64>())
        {
            marshalType = ReflectedParameter::MarshalType::UInt64;
        }
        else if (typeId == azrtti_typeid<float>())
        {
            marshalType = ReflectedParameter::MarshalType::Float;
        }
        else if (typeId == azrtti_typeid<double>())
        {
            marshalType = ReflectedParameter::MarshalType::Double;
        }
        else if (typeId == azrtti_typeid<AZStd::string>() || typeId == azrtti_typeid<const char*>())
        {
            marshalType = ReflectedParameter::MarshalType::String;
        }
        else if (typeId == azrtti_typeid<AZ::Vector3>())
        {
            marshalType = ReflectedParameter::MarshalType::Vector3;
        }
        else if (typeId == azrtti_typeid<AZ::Quaternion>())
        {
            marshalType = ReflectedParameter::MarshalType::Quaternion;
        }
        else if (typeId == azrtti_typeid<AZ::Transform>())
        {
            marshalType = ReflectedParameter::MarshalType::Transform;
        }
        else if (typeId == azrtti_typeid<AZ::EntityId>())
        {
            marshalType = ReflectedParameter::MarshalType::EntityId;
        }
        else
        {
            // Check if it's a reflected class
            if (m_behaviorContext)
            {
                for (const auto& classPair : m_behaviorContext->m_classes)
                {
                    if (classPair.second && classPair.second->m_typeId == typeId)
                    {
                        marshalType = ReflectedParameter::MarshalType::Object;
                        break;
                    }
                }
            }
        }

        // Cache the result
        m_marshalTypeCache[typeId] = marshalType;

        return marshalType;
    }

    void BehaviorContextReflector::ExtractScriptAttributes(
        const AZ::AttributeArray& attributes,
        AZStd::string& outDescription,
        AZStd::string& outCategory,
        bool& outIsDeprecated,
        AZStd::string& outDeprecationMessage) const
    {
        outDescription.clear();
        outCategory.clear();
        outIsDeprecated = false;
        outDeprecationMessage.clear();

        for (const auto& attributePair : attributes)
        {
            const AZ::Crc32 attributeId = attributePair.first;
            AZ::Attribute* attribute = attributePair.second;

            if (!attribute)
            {
                continue;
            }

            // Check for common script attributes
            if (attributeId == AZ::Script::Attributes::Category)
            {
                AZ::AttributeReader reader(nullptr, attribute);
                reader.Read<AZStd::string>(outCategory);
            }
            else if (attributeId == AZ::Script::Attributes::Deprecated)
            {
                AZ::AttributeReader reader(nullptr, attribute);
                reader.Read<bool>(outIsDeprecated);
            }
            // Note: There may be additional attributes for description, deprecation message, etc.
            // that can be extracted based on the specific O3DE version
        }
    }

    bool BehaviorContextReflector::ShouldExposeToScripting(const AZ::AttributeArray& attributes) const
    {
        // By default, expose everything
        bool shouldExpose = true;

        for (const auto& attributePair : attributes)
        {
            const AZ::Crc32 attributeId = attributePair.first;
            AZ::Attribute* attribute = attributePair.second;

            if (!attribute)
            {
                continue;
            }

            // Check for ExcludeFrom attribute
            if (attributeId == AZ::Script::Attributes::ExcludeFrom)
            {
                AZ::Script::Attributes::ExcludeFlags excludeFlags = static_cast<AZ::Script::Attributes::ExcludeFlags>(0);
                AZ::AttributeReader reader(nullptr, attribute);
                reader.Read<AZ::Script::Attributes::ExcludeFlags>(excludeFlags);

                // Check if excluded from all scripts or specifically from List/Documentation
                if ((excludeFlags & AZ::Script::Attributes::ExcludeFlags::All) != static_cast<AZ::Script::Attributes::ExcludeFlags>(0))
                {
                    shouldExpose = false;
                    break;
                }
            }

            // Check scope - we want Common and Automation scope
            if (attributeId == AZ::Script::Attributes::Scope)
            {
                AZ::Script::Attributes::ScopeFlags scopeFlags = AZ::Script::Attributes::ScopeFlags::Common;
                AZ::AttributeReader reader(nullptr, attribute);
                reader.Read<AZ::Script::Attributes::ScopeFlags>(scopeFlags);

                // Expose if Common or Automation scope
                constexpr auto commonAndAutomation = static_cast<AZ::u64>(AZ::Script::Attributes::ScopeFlags::Common) | static_cast<AZ::u64>(AZ::Script::Attributes::ScopeFlags::Automation);
                if ((static_cast<AZ::u64>(scopeFlags) & commonAndAutomation) == 0)
                {
                    shouldExpose = false;
                    break;
                }
            }
        }

        return shouldExpose;
    }

    AZStd::string BehaviorContextReflector::GetTypeName(const AZ::Uuid& typeId) const
    {
        // Check for built-in types first
        if (typeId == AZ::Uuid::CreateNull() || typeId == azrtti_typeid<void>())
        {
            return "void";
        }
        if (typeId == azrtti_typeid<bool>())
        {
            return "bool";
        }
        if (typeId == azrtti_typeid<AZ::s8>())
        {
            return "int8";
        }
        if (typeId == azrtti_typeid<AZ::s16>())
        {
            return "int16";
        }
        if (typeId == azrtti_typeid<AZ::s32>() || typeId == azrtti_typeid<int>())
        {
            return "int32";
        }
        if (typeId == azrtti_typeid<AZ::s64>())
        {
            return "int64";
        }
        if (typeId == azrtti_typeid<AZ::u8>())
        {
            return "uint8";
        }
        if (typeId == azrtti_typeid<AZ::u16>())
        {
            return "uint16";
        }
        if (typeId == azrtti_typeid<AZ::u32>() || typeId == azrtti_typeid<unsigned int>())
        {
            return "uint32";
        }
        if (typeId == azrtti_typeid<AZ::u64>())
        {
            return "uint64";
        }
        if (typeId == azrtti_typeid<float>())
        {
            return "float";
        }
        if (typeId == azrtti_typeid<double>())
        {
            return "double";
        }
        if (typeId == azrtti_typeid<AZStd::string>() || typeId == azrtti_typeid<const char*>())
        {
            return "string";
        }
        if (typeId == azrtti_typeid<AZ::Vector3>())
        {
            return "Vector3";
        }
        if (typeId == azrtti_typeid<AZ::Quaternion>())
        {
            return "Quaternion";
        }
        if (typeId == azrtti_typeid<AZ::Transform>())
        {
            return "Transform";
        }
        if (typeId == azrtti_typeid<AZ::EntityId>())
        {
            return "EntityId";
        }

        // Look up in behavior context
        if (m_behaviorContext)
        {
            for (const auto& classPair : m_behaviorContext->m_classes)
            {
                if (classPair.second && classPair.second->m_typeId == typeId)
                {
                    return classPair.first;
                }
            }
        }

        // Fall back to the UUID string
        return typeId.ToFixedString().c_str();
    }

    // ============================================================
    // Gem-Aware Accessors
    // ============================================================

    AZStd::vector<const ReflectedClass*> BehaviorContextReflector::GetClassesByGem(
        const AZStd::string& gemName,
        bool includeDeprecated) const
    {
        AZStd::vector<const ReflectedClass*> result;
        for (const auto& pair : m_classes)
        {
            const ReflectedClass& cls = pair.second;

            // Filter by deprecated status
            if (!includeDeprecated && cls.isDeprecated)
            {
                continue;
            }

            // Filter by gem name
            if (cls.sourceGemName == gemName)
            {
                result.push_back(&cls);
            }
        }
        return result;
    }

    AZStd::vector<const ReflectedEBus*> BehaviorContextReflector::GetEBusesByGem(const AZStd::string& gemName) const
    {
        AZStd::vector<const ReflectedEBus*> result;
        for (const auto& pair : m_ebuses)
        {
            const ReflectedEBus& ebus = pair.second;
            if (ebus.sourceGemName == gemName)
            {
                result.push_back(&ebus);
            }
        }
        return result;
    }

    AZStd::vector<AZStd::string> BehaviorContextReflector::GetSourceGemNames() const
    {
        if (!m_gemNamesCacheDirty)
        {
            return m_cachedGemNames;
        }

        AZStd::unordered_set<AZStd::string> gemNames;

        for (const auto& pair : m_classes)
        {
            if (!pair.second.sourceGemName.empty())
            {
                gemNames.insert(pair.second.sourceGemName);
            }
        }

        for (const auto& pair : m_ebuses)
        {
            if (!pair.second.sourceGemName.empty())
            {
                gemNames.insert(pair.second.sourceGemName);
            }
        }

        m_cachedGemNames.clear();
        m_cachedGemNames.reserve(gemNames.size());
        for (const auto& name : gemNames)
        {
            m_cachedGemNames.push_back(name);
        }

        // Sort alphabetically for consistent ordering
        AZStd::sort(m_cachedGemNames.begin(), m_cachedGemNames.end());

        m_gemNamesCacheDirty = false;
        return m_cachedGemNames;
    }

    AZStd::unordered_map<AZStd::string, AZStd::vector<const ReflectedClass*>> BehaviorContextReflector::GetClassesGroupedByGem() const
    {
        AZStd::unordered_map<AZStd::string, AZStd::vector<const ReflectedClass*>> result;

        for (const auto& pair : m_classes)
        {
            const ReflectedClass& cls = pair.second;
            AZStd::string gemName = cls.sourceGemName.empty() ? "Unknown" : cls.sourceGemName;
            result[gemName].push_back(&cls);
        }

        return result;
    }

    AZStd::unordered_map<AZStd::string, AZStd::vector<const ReflectedEBus*>> BehaviorContextReflector::GetEBusesGroupedByGem() const
    {
        AZStd::unordered_map<AZStd::string, AZStd::vector<const ReflectedEBus*>> result;

        for (const auto& pair : m_ebuses)
        {
            const ReflectedEBus& ebus = pair.second;
            AZStd::string gemName = ebus.sourceGemName.empty() ? "Unknown" : ebus.sourceGemName;
            result[gemName].push_back(&ebus);
        }

        return result;
    }

    void BehaviorContextReflector::SetClassGemSource(const AZStd::string& className, const AZStd::string& gemName)
    {
        auto it = m_classes.find(className);
        if (it != m_classes.end())
        {
            it->second.sourceGemName = gemName;
            m_gemNamesCacheDirty = true;
        }
    }

    void BehaviorContextReflector::SetEBusGemSource(const AZStd::string& ebusName, const AZStd::string& gemName)
    {
        auto it = m_ebuses.find(ebusName);
        if (it != m_ebuses.end())
        {
            it->second.sourceGemName = gemName;
            m_gemNamesCacheDirty = true;
        }
    }

} // namespace O3DESharp