/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

#pragma once

#include <AzCore/base.h>
#include <AzCore/Memory/SystemAllocator.h>
#include <AzCore/RTTI/RTTI.h>
#include <AzCore/RTTI/BehaviorContext.h>
#include <AzCore/std/string/string.h>
#include <AzCore/std/containers/vector.h>
#include <AzCore/std/containers/unordered_map.h>
#include <AzCore/std/smart_ptr/shared_ptr.h>
#include <AzCore/std/any.h>

namespace O3DESharp
{
    /**
     * Represents a parameter in a reflected method
     */
    struct ReflectedParameter
    {
        AZStd::string name;
        AZ::Uuid typeId;
        AZStd::string typeName;
        bool isPointer = false;
        bool isReference = false;
        bool isConst = false;

        // For marshalling to/from C#
        enum class MarshalType
        {
            Void,
            Bool,
            Int8,
            Int16,
            Int32,
            Int64,
            UInt8,
            UInt16,
            UInt32,
            UInt64,
            Float,
            Double,
            String,
            Vector3,
            Quaternion,
            Transform,
            EntityId,
            Object,     // Complex object requiring special handling
            Unknown
        };

        MarshalType marshalType = MarshalType::Unknown;
    };

    /**
     * Represents a reflected method from BehaviorContext
     */
    struct ReflectedMethod
    {
        AZStd::string name;
        AZStd::string className;        // Empty for global methods
        bool isStatic = false;
        bool isConst = false;
        
        ReflectedParameter returnType;
        AZStd::vector<ReflectedParameter> parameters;
        
        // The actual BehaviorMethod pointer for invocation
        AZ::BehaviorMethod* behaviorMethod = nullptr;

        // Script attributes
        AZStd::string description;
        AZStd::string category;
        bool isDeprecated = false;
        AZStd::string deprecationMessage;
    };

    /**
     * Represents a reflected property from BehaviorContext
     */
    struct ReflectedProperty
    {
        AZStd::string name;
        AZStd::string className;
        
        ReflectedParameter valueType;
        
        bool hasGetter = false;
        bool hasSetter = false;
        
        // The actual BehaviorProperty pointer
        AZ::BehaviorProperty* behaviorProperty = nullptr;

        // Script attributes
        AZStd::string description;
        bool isDeprecated = false;
    };

    /**
     * Represents a reflected EBus event
     */
    struct ReflectedEBusEvent
    {
        AZStd::string name;
        AZStd::string busName;
        
        ReflectedParameter returnType;
        AZStd::vector<ReflectedParameter> parameters;
        
        // The actual event sender for invocation
        AZ::BehaviorEBusEventSender* eventSender = nullptr;
        
        bool isBroadcast = true;    // vs addressed event
    };

    /**
     * Represents a reflected EBus from BehaviorContext
     */
    struct ReflectedEBus
    {
        AZStd::string name;
        AZ::Uuid typeId;
        
        // The address type for this bus (EntityId, string, int, etc.)
        ReflectedParameter addressType;
        
        AZStd::vector<ReflectedEBusEvent> events;
        
        // The actual BehaviorEBus pointer
        AZ::BehaviorEBus* behaviorEBus = nullptr;

        // Script attributes
        AZStd::string description;
        AZStd::string category;

        // Gem source tracking
        AZStd::string sourceGemName;        // Name of the gem this EBus belongs to
        AZStd::string sourceModuleName;     // Name of the module that registered this EBus
    };

    /**
     * Represents a reflected class from BehaviorContext
     */
    struct ReflectedClass
    {
        AZStd::string name;
        AZ::Uuid typeId;
        
        // Parent class names (for inheritance)
        AZStd::vector<AZStd::string> baseClasses;
        
        // Methods (instance and static)
        AZStd::vector<ReflectedMethod> methods;
        
        // Properties
        AZStd::vector<ReflectedProperty> properties;
        
        // Constructors (methods that create instances)
        AZStd::vector<ReflectedMethod> constructors;
        
        // The actual BehaviorClass pointer
        AZ::BehaviorClass* behaviorClass = nullptr;

        // Script attributes
        AZStd::string description;
        AZStd::string category;
        bool isDeprecated = false;

        // Gem source tracking
        AZStd::string sourceGemName;        // Name of the gem this class belongs to
        AZStd::string sourceModuleName;     // Name of the module that registered this class

        // Helper to find a method by name
        const ReflectedMethod* FindMethod(const AZStd::string& methodName) const;
        const ReflectedProperty* FindProperty(const AZStd::string& propertyName) const;
    };

    /**
     * BehaviorContextReflector - Extracts and caches metadata from O3DE's BehaviorContext
     *
     * This class iterates over the BehaviorContext to extract information about:
     * - Reflected classes with their methods and properties
     * - Global methods and properties
     * - EBuses and their events
     *
     * This metadata is then used to:
     * 1. Generate C# wrapper code (at build time or on-demand)
     * 2. Enable dynamic method invocation from C# via a generic dispatcher
     * 3. Provide intellisense/autocomplete information to tools
     */
    class BehaviorContextReflector
    {
    public:
        AZ_RTTI(BehaviorContextReflector, "{C1D2E3F4-A5B6-7890-CDEF-123456789ABC}");
        AZ_CLASS_ALLOCATOR(BehaviorContextReflector, AZ::SystemAllocator);

        BehaviorContextReflector() = default;
        virtual ~BehaviorContextReflector() = default;

        /**
         * Reflects all types from the given BehaviorContext
         * @param context The BehaviorContext to reflect from
         */
        void ReflectFromContext(AZ::BehaviorContext* context);

        /**
         * Clear all cached reflection data
         */
        void Clear();

        // ============================================================
        // Accessors
        // ============================================================

        /**
         * Get all reflected class names
         */
        AZStd::vector<AZStd::string> GetClassNames() const;

        /**
         * Get a reflected class by name
         * @return Pointer to the class, or nullptr if not found
         */
        const ReflectedClass* GetClass(const AZStd::string& className) const;

        /**
         * Get all reflected EBus names
         */
        AZStd::vector<AZStd::string> GetEBusNames() const;

        /**
         * Get a reflected EBus by name
         * @return Pointer to the EBus, or nullptr if not found
         */
        const ReflectedEBus* GetEBus(const AZStd::string& busName) const;

        /**
         * Get all global methods (not part of any class)
         */
        const AZStd::vector<ReflectedMethod>& GetGlobalMethods() const { return m_globalMethods; }

        /**
         * Get all global properties
         */
        const AZStd::vector<ReflectedProperty>& GetGlobalProperties() const { return m_globalProperties; }

        /**
         * Check if a class is reflected
         */
        bool HasClass(const AZStd::string& className) const;

        /**
         * Check if an EBus is reflected
         */
        bool HasEBus(const AZStd::string& busName) const;

        // ============================================================
        // Filtering
        // ============================================================

        /**
         * Get classes that match a filter (e.g., category, module)
         * @param category The category to filter by (empty for all)
         * @param includeDeprecated Whether to include deprecated classes
         */
        AZStd::vector<const ReflectedClass*> GetClassesByCategory(
            const AZStd::string& category,
            bool includeDeprecated = false) const;

        /**
         * Get classes that derive from a base class
         */
        AZStd::vector<const ReflectedClass*> GetDerivedClasses(const AZStd::string& baseClassName) const;

        // ============================================================
        // Gem-Aware Accessors
        // ============================================================

        /**
         * Get classes that belong to a specific gem
         * @param gemName Name of the gem to filter by
         * @param includeDeprecated Whether to include deprecated classes
         */
        AZStd::vector<const ReflectedClass*> GetClassesByGem(
            const AZStd::string& gemName,
            bool includeDeprecated = false) const;

        /**
         * Get EBuses that belong to a specific gem
         * @param gemName Name of the gem to filter by
         */
        AZStd::vector<const ReflectedEBus*> GetEBusesByGem(const AZStd::string& gemName) const;

        /**
         * Get all unique gem names that have reflected types
         */
        AZStd::vector<AZStd::string> GetSourceGemNames() const;

        /**
         * Group all classes by their source gem
         * @return Map of gem name to list of classes
         */
        AZStd::unordered_map<AZStd::string, AZStd::vector<const ReflectedClass*>> GetClassesGroupedByGem() const;

        /**
         * Group all EBuses by their source gem
         * @return Map of gem name to list of EBuses
         */
        AZStd::unordered_map<AZStd::string, AZStd::vector<const ReflectedEBus*>> GetEBusesGroupedByGem() const;

        /**
         * Set the gem name for a class (used by GemDependencyResolver)
         * @param className Name of the class
         * @param gemName Name of the gem
         */
        void SetClassGemSource(const AZStd::string& className, const AZStd::string& gemName);

        /**
         * Set the gem name for an EBus (used by GemDependencyResolver)
         * @param ebusName Name of the EBus
         * @param gemName Name of the gem
         */
        void SetEBusGemSource(const AZStd::string& ebusName, const AZStd::string& gemName);

        // ============================================================
        // Statistics
        // ============================================================

        size_t GetClassCount() const { return m_classes.size(); }
        size_t GetEBusCount() const { return m_ebuses.size(); }
        size_t GetGlobalMethodCount() const { return m_globalMethods.size(); }
        size_t GetGlobalPropertyCount() const { return m_globalProperties.size(); }

    private:
        /**
         * Reflects a single BehaviorClass
         */
        void ReflectClass(const AZStd::string& name, AZ::BehaviorClass* behaviorClass);

        /**
         * Reflects a single BehaviorEBus
         */
        void ReflectEBus(const AZStd::string& name, AZ::BehaviorEBus* behaviorEBus);

        /**
         * Reflects a BehaviorMethod into our ReflectedMethod structure
         */
        ReflectedMethod ReflectMethod(const AZStd::string& name, AZ::BehaviorMethod* method, const AZStd::string& className = "");

        /**
         * Reflects a BehaviorProperty into our ReflectedProperty structure
         */
        ReflectedProperty ReflectProperty(const AZStd::string& name, AZ::BehaviorProperty* property, const AZStd::string& className = "");

        /**
         * Reflects a BehaviorParameter into our ReflectedParameter structure
         */
        ReflectedParameter ReflectParameter(const AZ::BehaviorParameter& param);

        /**
         * Determines the marshal type for a given type ID
         */
        ReflectedParameter::MarshalType DetermineMarshalType(const AZ::Uuid& typeId) const;

        /**
         * Extracts common script attributes from an attribute array
         */
        void ExtractScriptAttributes(
            const AZ::AttributeArray& attributes,
            AZStd::string& outDescription,
            AZStd::string& outCategory,
            bool& outIsDeprecated,
            AZStd::string& outDeprecationMessage) const;

        /**
         * Checks if a class/method should be exposed to scripting based on its attributes
         */
        bool ShouldExposeToScripting(const AZ::AttributeArray& attributes) const;

        /**
         * Gets the human-readable type name for a type ID
         */
        AZStd::string GetTypeName(const AZ::Uuid& typeId) const;

    private:
        // Cached reflection data
        AZStd::unordered_map<AZStd::string, ReflectedClass> m_classes;
        AZStd::unordered_map<AZStd::string, ReflectedEBus> m_ebuses;
        AZStd::vector<ReflectedMethod> m_globalMethods;
        AZStd::vector<ReflectedProperty> m_globalProperties;

        // Type ID to marshal type mapping (cached for performance)
        mutable AZStd::unordered_map<AZ::Uuid, ReflectedParameter::MarshalType> m_marshalTypeCache;

        // Reference to the behavior context (not owned)
        AZ::BehaviorContext* m_behaviorContext = nullptr;

        // Cache of gem names for quick lookup
        mutable AZStd::vector<AZStd::string> m_cachedGemNames;
        mutable bool m_gemNamesCacheDirty = true;
    };

} // namespace O3DESharp