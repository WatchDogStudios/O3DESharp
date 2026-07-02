/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

#pragma once

#include <AzCore/EBus/EBus.h>
#include <AzCore/std/string/string.h>
#include <AzCore/std/containers/vector.h>
#include <AzCore/RTTI/BehaviorContext.h>

namespace O3DESharp
{
    // Mirrors SCRIPT_PICKER_CLEARED_SENTINEL in Editor/Scripts/csharp_editor_tools.py.
    // CSharpEditorToolsBus::OpenScriptPicker returns a plain AZStd::string with
    // no reserved "no value" representation, so this sentinel is the only way
    // for the Python side to tell C++ callers "the user explicitly cleared the
    // field" apart from "the user cancelled / produced nothing" (the plain
    // empty string). Keep this string in sync with the Python constant if
    // either side ever changes it. Declared here (rather than in a single
    // .cpp) because both EditorCSharpScriptComponent.cpp and
    // CSharpScriptClassPropertyHandler.cpp need to compare against it.
    inline constexpr const char* ScriptPickerClearedSentinel = "__O3DESharp_ClearSelection__";

    /**
     * Data structure representing a C# script class with metadata
     */
    struct ScriptClassInfo
    {
        AZ_TYPE_INFO(ScriptClassInfo, "{8B9A0C1D-2E3F-4051-6728-39ABCD456789}");
        AZ_CLASS_ALLOCATOR(ScriptClassInfo, AZ::SystemAllocator);

        AZStd::string m_fullName;          ///< Fully qualified name (Namespace.ClassName)
        AZStd::string m_className;         ///< Short class name
        AZStd::string m_namespace;         ///< Namespace
        AZStd::string m_projectName;       ///< Name of the containing C# project
        AZStd::string m_filePath;          ///< Full path to the .cs file
        AZStd::string m_baseClass;         ///< Base class name (e.g., "ScriptComponent")
        bool m_isScriptComponent = false;  ///< True if class derives from ScriptComponent
        bool m_isRecent = false;           ///< True if this is a recently used class

        static void Reflect(AZ::ReflectContext* context)
        {
            if (auto* serializeContext = azrtti_cast<AZ::SerializeContext*>(context))
            {
                serializeContext->Class<ScriptClassInfo>()
                    ->Version(1)
                    ->Field("FullName", &ScriptClassInfo::m_fullName)
                    ->Field("ClassName", &ScriptClassInfo::m_className)
                    ->Field("Namespace", &ScriptClassInfo::m_namespace)
                    ->Field("ProjectName", &ScriptClassInfo::m_projectName)
                    ->Field("FilePath", &ScriptClassInfo::m_filePath)
                    ->Field("BaseClass", &ScriptClassInfo::m_baseClass)
                    ->Field("IsScriptComponent", &ScriptClassInfo::m_isScriptComponent)
                    ->Field("IsRecent", &ScriptClassInfo::m_isRecent)
                    ;
            }

            if (auto* behaviorContext = azrtti_cast<AZ::BehaviorContext*>(context))
            {
                behaviorContext->Class<ScriptClassInfo>("ScriptClassInfo")
                    ->Attribute(AZ::Script::Attributes::Scope, AZ::Script::Attributes::ScopeFlags::Automation)
                    ->Attribute(AZ::Script::Attributes::Module, "editor")
                    ->Property("fullName", BehaviorValueProperty(&ScriptClassInfo::m_fullName))
                    ->Property("className", BehaviorValueProperty(&ScriptClassInfo::m_className))
                    ->Property("namespace", BehaviorValueProperty(&ScriptClassInfo::m_namespace))
                    ->Property("projectName", BehaviorValueProperty(&ScriptClassInfo::m_projectName))
                    ->Property("filePath", BehaviorValueProperty(&ScriptClassInfo::m_filePath))
                    ->Property("baseClass", BehaviorValueProperty(&ScriptClassInfo::m_baseClass))
                    ->Property("isScriptComponent", BehaviorValueProperty(&ScriptClassInfo::m_isScriptComponent))
                    ->Property("isRecent", BehaviorValueProperty(&ScriptClassInfo::m_isRecent))
                    ;
            }
        }
    };

    /**
     * Validation result for a script class name
     */
    struct ScriptValidationResult
    {
        AZ_TYPE_INFO(ScriptValidationResult, "{9C0A1D2E-3F40-5162-7839-4ABCDE567890}");
        AZ_CLASS_ALLOCATOR(ScriptValidationResult, AZ::SystemAllocator);

        bool m_isValid = false;           ///< True if class exists and is valid
        AZStd::string m_message;          ///< Validation message (error or success)
        AZStd::string m_filePath;         ///< Path to the script file (if found)
        AZStd::string m_baseClass;        ///< Base class name (if found)

        static void Reflect(AZ::ReflectContext* context)
        {
            if (auto* serializeContext = azrtti_cast<AZ::SerializeContext*>(context))
            {
                serializeContext->Class<ScriptValidationResult>()
                    ->Version(1)
                    ->Field("IsValid", &ScriptValidationResult::m_isValid)
                    ->Field("Message", &ScriptValidationResult::m_message)
                    ->Field("FilePath", &ScriptValidationResult::m_filePath)
                    ->Field("BaseClass", &ScriptValidationResult::m_baseClass)
                    ;
            }

            if (auto* behaviorContext = azrtti_cast<AZ::BehaviorContext*>(context))
            {
                behaviorContext->Class<ScriptValidationResult>("ScriptValidationResult")
                    ->Attribute(AZ::Script::Attributes::Scope, AZ::Script::Attributes::ScopeFlags::Automation)
                    ->Attribute(AZ::Script::Attributes::Module, "editor")
                    ->Property("isValid", BehaviorValueProperty(&ScriptValidationResult::m_isValid))
                    ->Property("message", BehaviorValueProperty(&ScriptValidationResult::m_message))
                    ->Property("filePath", BehaviorValueProperty(&ScriptValidationResult::m_filePath))
                    ->Property("baseClass", BehaviorValueProperty(&ScriptValidationResult::m_baseClass))
                    ;
            }
        }
    };

    /**
     * EBus for communication between C++ editor components and Python editor tools
     * 
     * This bus eliminates the need for file-based IPC and provides a direct,
     * type-safe interface for script management operations.
     */
    class CSharpEditorToolsRequests
        : public AZ::EBusTraits
    {
    public:
        //////////////////////////////////////////////////////////////////////////
        // EBusTraits overrides
        static const AZ::EBusHandlerPolicy HandlerPolicy = AZ::EBusHandlerPolicy::Single;
        static const AZ::EBusAddressPolicy AddressPolicy = AZ::EBusAddressPolicy::Single;
        //////////////////////////////////////////////////////////////////////////

        /**
         * Get all available C# script classes
         * @param scriptsOnly If true, only return classes derived from ScriptComponent
         * @return Vector of script class info, with recent classes first
         */
        virtual AZStd::vector<ScriptClassInfo> GetAvailableScriptClasses(bool scriptsOnly = true) = 0;

        /**
         * Get just the class names (for dropdown population)
         * @param scriptsOnly If true, only return classes derived from ScriptComponent
         * @return Vector of fully qualified class names, with recent classes first
         */
        virtual AZStd::vector<AZStd::string> GetScriptClassNames(bool scriptsOnly = true) = 0;

        /**
         * Validate a script class name
         * @param className Fully qualified class name to validate
         * @return Validation result with status and details
         */
        virtual ScriptValidationResult ValidateScriptClass(const AZStd::string& className) = 0;

        /**
         * Open the script class picker dialog
         * @param currentClass Currently selected class (for pre-selection)
         * @return Selected class name; the literal sentinel string
         *         "__O3DESharp_ClearSelection__" (see ScriptPickerClearedSentinel
         *         above) if the user explicitly clicked "Clear Selection"
         *         (must be blanked and re-validated by the caller); or an
         *         empty string if the user cancelled the dialog (a true
         *         no-op, callers must not warn in this case)
         */
        virtual AZStd::string OpenScriptPicker(const AZStd::string& currentClass) = 0;

        /**
         * Open the create new script dialog
         * @param defaultName Default class name
         * @param defaultNamespace Default namespace
         * @return Created class name (fully qualified), or empty string if cancelled
         */
        virtual AZStd::string CreateNewScript(const AZStd::string& defaultName = "", const AZStd::string& defaultNamespace = "") = 0;

        /**
         * Open a script file in the default IDE
         * @param className Fully qualified class name to open
         * @return True if successful
         */
        virtual bool OpenScriptInEditor(const AZStd::string& className) = 0;

        /**
         * Invalidate the script class cache (force refresh on next query)
         */
        virtual void InvalidateCache() = 0;

        /**
         * Add a class to the recent classes list
         * @param className Fully qualified class name
         */
        virtual void AddToRecentClasses(const AZStd::string& className) = 0;
    };

    using CSharpEditorToolsBus = AZ::EBus<CSharpEditorToolsRequests>;

    /**
     * BehaviorContext / Python handler binder for CSharpEditorToolsBus.
     *
     * Reflecting this type via ->Handler<CSharpEditorToolsBusHandler>() on the
     * BehaviorContext lets Python scripts implement the bus by deriving from
     * the auto-generated handler class (azlmbr.editor.CSharpEditorToolsBusHandler)
     * and calling connect(). Without this binder the bus is callable from C++
     * only and has no Python-side implementation, which is why the property
     * handler dropdown used to come back empty.
     */
    class CSharpEditorToolsBusHandler
        : public CSharpEditorToolsBus::Handler
        , public AZ::BehaviorEBusHandler
    {
    public:
        AZ_EBUS_BEHAVIOR_BINDER(
            CSharpEditorToolsBusHandler,
            "{4B5C6D7E-8F90-4213-9876-CDEF01234567}",
            AZ::SystemAllocator,
            GetAvailableScriptClasses,
            GetScriptClassNames,
            ValidateScriptClass,
            OpenScriptPicker,
            CreateNewScript,
            OpenScriptInEditor,
            InvalidateCache,
            AddToRecentClasses);

        AZStd::vector<ScriptClassInfo> GetAvailableScriptClasses(bool scriptsOnly) override
        {
            AZStd::vector<ScriptClassInfo> result;
            CallResult(result, FN_GetAvailableScriptClasses, scriptsOnly);
            return result;
        }

        AZStd::vector<AZStd::string> GetScriptClassNames(bool scriptsOnly) override
        {
            AZStd::vector<AZStd::string> result;
            CallResult(result, FN_GetScriptClassNames, scriptsOnly);
            return result;
        }

        ScriptValidationResult ValidateScriptClass(const AZStd::string& className) override
        {
            ScriptValidationResult result;
            CallResult(result, FN_ValidateScriptClass, className);
            return result;
        }

        AZStd::string OpenScriptPicker(const AZStd::string& currentClass) override
        {
            AZStd::string result;
            CallResult(result, FN_OpenScriptPicker, currentClass);
            return result;
        }

        AZStd::string CreateNewScript(const AZStd::string& defaultName, const AZStd::string& defaultNamespace) override
        {
            AZStd::string result;
            CallResult(result, FN_CreateNewScript, defaultName, defaultNamespace);
            return result;
        }

        bool OpenScriptInEditor(const AZStd::string& className) override
        {
            bool result = false;
            CallResult(result, FN_OpenScriptInEditor, className);
            return result;
        }

        void InvalidateCache() override
        {
            Call(FN_InvalidateCache);
        }

        void AddToRecentClasses(const AZStd::string& className) override
        {
            Call(FN_AddToRecentClasses, className);
        }
    };

} // namespace O3DESharp
