/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

#include "ReflectionDataExporter.h"

#include <AzCore/Console/ILogger.h>
#include <AzCore/IO/FileIO.h>
#include <AzCore/IO/SystemFile.h>
#include <AzCore/std/sort.h>

namespace O3DESharp
{
    // ============================================================
    // ReflectionExportResult
    // ============================================================

    ReflectionExportResult ReflectionExportResult::Success(const AZStd::string& json)
    {
        ReflectionExportResult result;
        result.success = true;
        result.jsonData = json;
        return result;
    }

    ReflectionExportResult ReflectionExportResult::Error(const AZStd::string& message)
    {
        ReflectionExportResult result;
        result.success = false;
        result.errorMessage = message;
        return result;
    }

    // ============================================================
    // ReflectionDataExporter Implementation
    // ============================================================

    ReflectionExportResult ReflectionDataExporter::Export(
        const BehaviorContextReflector& reflector,
        const ReflectionExportConfig& config)
    {
        AZLOG_INFO("ReflectionDataExporter: Starting export...");

        ReflectionExportResult result;
        result.success = true;

        // Generate JSON
        result.jsonData = GenerateJson(reflector, config, result);

        // Write to file if path specified
        if (!config.outputPath.empty())
        {
            if (!WriteToFile(result.jsonData, config.outputPath))
            {
                result.success = false;
                result.errorMessage = AZStd::string::format(
                    "Failed to write to file: %s", config.outputPath.c_str());
                return result;
            }
            result.outputPath = config.outputPath;
        }

        AZLOG_INFO("ReflectionDataExporter: Export complete - %zu classes, %zu EBuses",
            result.classesExported, result.ebusesExported);

        return result;
    }

    ReflectionExportResult ReflectionDataExporter::ExportFromContext(
        AZ::BehaviorContext* context,
        const ReflectionExportConfig& config)
    {
        if (!context)
        {
            return ReflectionExportResult::Error("BehaviorContext is null");
        }

        // Create and populate reflector
        BehaviorContextReflector reflector;
        reflector.ReflectFromContext(context);

        return Export(reflector, config);
    }

    AZStd::string ReflectionDataExporter::ExportToString(
        const BehaviorContextReflector& reflector,
        bool prettyPrint)
    {
        ReflectionExportConfig config;
        config.prettyPrint = prettyPrint;

        ReflectionExportResult result;
        return GenerateJson(reflector, config, result);
    }

    ReflectionExportResult ReflectionDataExporter::ExportToFile(
        const BehaviorContextReflector& reflector,
        const AZ::IO::Path& outputPath,
        const ReflectionExportConfig& config)
    {
        ReflectionExportConfig fileConfig = config;
        fileConfig.outputPath = outputPath;
        return Export(reflector, fileConfig);
    }

    // ============================================================
    // JSON Generation
    // ============================================================

    AZStd::string ReflectionDataExporter::GenerateJson(
        const BehaviorContextReflector& reflector,
        const ReflectionExportConfig& config,
        ReflectionExportResult& outResult)
    {
        AZStd::string json;
        json.reserve(1024 * 1024); // Reserve 1MB

        const int indentSize = config.prettyPrint ? config.indentSize : 0;
        const AZStd::string newline = config.prettyPrint ? "\n" : "";

        json += "{" + newline;

        // Export classes
        json += Indent(1, indentSize) + "\"classes\": [" + newline;

        AZStd::vector<AZStd::string> classNames = reflector.GetClassNames();
        AZStd::sort(classNames.begin(), classNames.end());

        bool firstClass = true;
        for (const auto& className : classNames)
        {
            const ReflectedClass* cls = reflector.GetClass(className);
            if (cls && ShouldIncludeClass(*cls, config))
            {
                if (!firstClass)
                {
                    json += "," + newline;
                }
                json += GenerateClassJson(*cls, config, 2);
                firstClass = false;
                outResult.classesExported++;
            }
        }

        json += newline + Indent(1, indentSize) + "]," + newline;

        // Export EBuses
        json += Indent(1, indentSize) + "\"ebuses\": [" + newline;

        AZStd::vector<AZStd::string> ebusNames = reflector.GetEBusNames();
        AZStd::sort(ebusNames.begin(), ebusNames.end());

        bool firstEBus = true;
        for (const auto& ebusName : ebusNames)
        {
            const ReflectedEBus* ebus = reflector.GetEBus(ebusName);
            if (ebus && ShouldIncludeEBus(*ebus, config))
            {
                if (!firstEBus)
                {
                    json += "," + newline;
                }
                json += GenerateEBusJson(*ebus, config, 2);
                firstEBus = false;
                outResult.ebusesExported++;
            }
        }

        json += newline + Indent(1, indentSize) + "]," + newline;

        // Export global methods
        json += Indent(1, indentSize) + "\"global_methods\": [" + newline;

        const auto& globalMethods = reflector.GetGlobalMethods();
        for (size_t i = 0; i < globalMethods.size(); ++i)
        {
            if (i > 0)
            {
                json += "," + newline;
            }
            json += GenerateMethodJson(globalMethods[i], config, 2);
            outResult.globalMethodsExported++;
        }

        json += newline + Indent(1, indentSize) + "]," + newline;

        // Export global properties
        json += Indent(1, indentSize) + "\"global_properties\": [" + newline;

        const auto& globalProperties = reflector.GetGlobalProperties();
        for (size_t i = 0; i < globalProperties.size(); ++i)
        {
            if (i > 0)
            {
                json += "," + newline;
            }
            json += GeneratePropertyJson(globalProperties[i], config, 2);
            outResult.globalPropertiesExported++;
        }

        json += newline + Indent(1, indentSize) + "]" + newline;

        json += "}" + newline;

        return json;
    }

    AZStd::string ReflectionDataExporter::GenerateClassJson(
        const ReflectedClass& cls,
        const ReflectionExportConfig& config,
        int indentLevel)
    {
        const int indentSize = config.prettyPrint ? config.indentSize : 0;
        const AZStd::string nl = config.prettyPrint ? "\n" : "";
        const AZStd::string ind = Indent(indentLevel, indentSize);
        const AZStd::string ind1 = Indent(indentLevel + 1, indentSize);
        const AZStd::string ind2 = Indent(indentLevel + 2, indentSize);

        AZStd::string json;
        json += ind + "{" + nl;

        // Basic properties
        json += ind1 + "\"name\": \"" + EscapeJsonString(cls.name) + "\"," + nl;

        if (config.includeTypeIds && !cls.typeId.IsNull())
        {
            json += ind1 + "\"type_id\": \"" + cls.typeId.ToFixedString().c_str() + "\"," + nl;
        }

        json += ind1 + "\"description\": \"" + EscapeJsonString(cls.description) + "\"," + nl;
        json += ind1 + "\"category\": \"" + EscapeJsonString(cls.category) + "\"," + nl;
        json += ind1 + "\"is_deprecated\": " + (cls.isDeprecated ? "true" : "false") + "," + nl;
        json += ind1 + "\"source_gem_name\": \"" + EscapeJsonString(cls.sourceGemName) + "\"," + nl;

        // Base classes
        json += ind1 + "\"base_classes\": [";
        for (size_t i = 0; i < cls.baseClasses.size(); ++i)
        {
            if (i > 0) json += ", ";
            json += "\"" + EscapeJsonString(cls.baseClasses[i]) + "\"";
        }
        json += "]," + nl;

        // Constructors
        json += ind1 + "\"constructors\": [" + nl;
        for (size_t i = 0; i < cls.constructors.size(); ++i)
        {
            if (i > 0) json += "," + nl;
            json += GenerateMethodJson(cls.constructors[i], config, indentLevel + 2);
        }
        json += nl + ind1 + "]," + nl;

        // Methods
        json += ind1 + "\"methods\": [" + nl;
        for (size_t i = 0; i < cls.methods.size(); ++i)
        {
            if (i > 0) json += "," + nl;
            json += GenerateMethodJson(cls.methods[i], config, indentLevel + 2);
        }
        json += nl + ind1 + "]," + nl;

        // Properties
        json += ind1 + "\"properties\": [" + nl;
        for (size_t i = 0; i < cls.properties.size(); ++i)
        {
            if (i > 0) json += "," + nl;
            json += GeneratePropertyJson(cls.properties[i], config, indentLevel + 2);
        }
        json += nl + ind1 + "]" + nl;

        json += ind + "}";

        return json;
    }

    AZStd::string ReflectionDataExporter::GenerateMethodJson(
        const ReflectedMethod& method,
        const ReflectionExportConfig& config,
        int indentLevel)
    {
        const int indentSize = config.prettyPrint ? config.indentSize : 0;
        const AZStd::string nl = config.prettyPrint ? "\n" : "";
        const AZStd::string ind = Indent(indentLevel, indentSize);
        const AZStd::string ind1 = Indent(indentLevel + 1, indentSize);

        AZStd::string json;
        json += ind + "{" + nl;

        json += ind1 + "\"name\": \"" + EscapeJsonString(method.name) + "\"," + nl;
        json += ind1 + "\"class_name\": \"" + EscapeJsonString(method.className) + "\"," + nl;
        json += ind1 + "\"is_static\": " + (method.isStatic ? "true" : "false") + "," + nl;
        json += ind1 + "\"is_const\": " + (method.isConst ? "true" : "false") + "," + nl;
        json += ind1 + "\"description\": \"" + EscapeJsonString(method.description) + "\"," + nl;
        json += ind1 + "\"category\": \"" + EscapeJsonString(method.category) + "\"," + nl;
        json += ind1 + "\"is_deprecated\": " + (method.isDeprecated ? "true" : "false") + "," + nl;
        json += ind1 + "\"deprecation_message\": \"" + EscapeJsonString(method.deprecationMessage) + "\"," + nl;

        // Return type
        json += ind1 + "\"return_type\": " + GenerateParameterJson(method.returnType, config, 0) + "," + nl;

        // Parameters
        json += ind1 + "\"parameters\": [" + nl;
        for (size_t i = 0; i < method.parameters.size(); ++i)
        {
            if (i > 0) json += "," + nl;
            json += Indent(indentLevel + 2, indentSize) + GenerateParameterJson(method.parameters[i], config, 0);
        }
        json += nl + ind1 + "]" + nl;

        json += ind + "}";

        return json;
    }

    AZStd::string ReflectionDataExporter::GeneratePropertyJson(
        const ReflectedProperty& property,
        const ReflectionExportConfig& config,
        int indentLevel)
    {
        const int indentSize = config.prettyPrint ? config.indentSize : 0;
        const AZStd::string nl = config.prettyPrint ? "\n" : "";
        const AZStd::string ind = Indent(indentLevel, indentSize);
        const AZStd::string ind1 = Indent(indentLevel + 1, indentSize);

        AZStd::string json;
        json += ind + "{" + nl;

        json += ind1 + "\"name\": \"" + EscapeJsonString(property.name) + "\"," + nl;
        json += ind1 + "\"class_name\": \"" + EscapeJsonString(property.className) + "\"," + nl;
        json += ind1 + "\"has_getter\": " + (property.hasGetter ? "true" : "false") + "," + nl;
        json += ind1 + "\"has_setter\": " + (property.hasSetter ? "true" : "false") + "," + nl;
        json += ind1 + "\"description\": \"" + EscapeJsonString(property.description) + "\"," + nl;
        json += ind1 + "\"is_deprecated\": " + (property.isDeprecated ? "true" : "false") + "," + nl;

        // Value type
        json += ind1 + "\"value_type\": " + GenerateParameterJson(property.valueType, config, 0) + nl;

        json += ind + "}";

        return json;
    }

    AZStd::string ReflectionDataExporter::GenerateEBusJson(
        const ReflectedEBus& ebus,
        const ReflectionExportConfig& config,
        int indentLevel)
    {
        const int indentSize = config.prettyPrint ? config.indentSize : 0;
        const AZStd::string nl = config.prettyPrint ? "\n" : "";
        const AZStd::string ind = Indent(indentLevel, indentSize);
        const AZStd::string ind1 = Indent(indentLevel + 1, indentSize);

        AZStd::string json;
        json += ind + "{" + nl;

        json += ind1 + "\"name\": \"" + EscapeJsonString(ebus.name) + "\"," + nl;

        if (config.includeTypeIds && !ebus.typeId.IsNull())
        {
            json += ind1 + "\"type_id\": \"" + ebus.typeId.ToFixedString().c_str() + "\"," + nl;
        }

        json += ind1 + "\"description\": \"" + EscapeJsonString(ebus.description) + "\"," + nl;
        json += ind1 + "\"category\": \"" + EscapeJsonString(ebus.category) + "\"," + nl;
        json += ind1 + "\"source_gem_name\": \"" + EscapeJsonString(ebus.sourceGemName) + "\"," + nl;

        // Address type
        json += ind1 + "\"address_type\": " + GenerateParameterJson(ebus.addressType, config, 0) + "," + nl;

        // Events
        json += ind1 + "\"events\": [" + nl;
        for (size_t i = 0; i < ebus.events.size(); ++i)
        {
            if (i > 0) json += "," + nl;
            json += GenerateEBusEventJson(ebus.events[i], config, indentLevel + 2);
        }
        json += nl + ind1 + "]" + nl;

        json += ind + "}";

        return json;
    }

    AZStd::string ReflectionDataExporter::GenerateEBusEventJson(
        const ReflectedEBusEvent& event,
        const ReflectionExportConfig& config,
        int indentLevel)
    {
        const int indentSize = config.prettyPrint ? config.indentSize : 0;
        const AZStd::string nl = config.prettyPrint ? "\n" : "";
        const AZStd::string ind = Indent(indentLevel, indentSize);
        const AZStd::string ind1 = Indent(indentLevel + 1, indentSize);

        AZStd::string json;
        json += ind + "{" + nl;

        json += ind1 + "\"name\": \"" + EscapeJsonString(event.name) + "\"," + nl;
        json += ind1 + "\"bus_name\": \"" + EscapeJsonString(event.busName) + "\"," + nl;
        json += ind1 + "\"is_broadcast\": " + (event.isBroadcast ? "true" : "false") + "," + nl;

        // Return type
        json += ind1 + "\"return_type\": " + GenerateParameterJson(event.returnType, config, 0) + "," + nl;

        // Parameters
        json += ind1 + "\"parameters\": [" + nl;
        for (size_t i = 0; i < event.parameters.size(); ++i)
        {
            if (i > 0) json += "," + nl;
            json += Indent(indentLevel + 2, indentSize) + GenerateParameterJson(event.parameters[i], config, 0);
        }
        json += nl + ind1 + "]" + nl;

        json += ind + "}";

        return json;
    }

    AZStd::string ReflectionDataExporter::GenerateParameterJson(
        const ReflectedParameter& param,
        const ReflectionExportConfig& config,
        [[maybe_unused]] int indentLevel)
    {
        AZStd::string json = "{";
        json += "\"name\": \"" + EscapeJsonString(param.name) + "\", ";
        json += "\"type_name\": \"" + EscapeJsonString(param.typeName) + "\", ";

        if (config.includeTypeIds && !param.typeId.IsNull())
        {
            json += "\"type_id\": \"" + AZStd::string(param.typeId.ToFixedString().c_str()) + "\", ";
        }

        json += "\"is_pointer\": " + AZStd::string(param.isPointer ? "true" : "false") + ", ";
        json += "\"is_reference\": " + AZStd::string(param.isReference ? "true" : "false") + ", ";
        json += "\"is_const\": " + AZStd::string(param.isConst ? "true" : "false");

        if (config.includeMarshalTypes)
        {
            json += ", \"marshal_type\": \"" + MarshalTypeToString(param.marshalType) + "\"";
        }

        json += "}";

        return json;
    }

    // ============================================================
    // Filtering
    // ============================================================

    bool ReflectionDataExporter::ShouldIncludeClass(
        const ReflectedClass& cls,
        const ReflectionExportConfig& config) const
    {
        // Check deprecation
        if (!config.includeDeprecated && cls.isDeprecated)
        {
            return false;
        }

        // Check exclude list
        for (const auto& excludeName : config.excludeClasses)
        {
            if (cls.name == excludeName)
            {
                return false;
            }
        }

        // Check category exclusions
        if (!cls.category.empty())
        {
            for (const auto& excludeCat : config.excludeCategories)
            {
                if (cls.category == excludeCat)
                {
                    return false;
                }
            }
        }

        // Check category inclusions
        if (!config.includeCategories.empty())
        {
            bool found = false;
            for (const auto& includeCat : config.includeCategories)
            {
                if (cls.category == includeCat || cls.category.starts_with(includeCat + "/"))
                {
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                return false;
            }
        }

        return true;
    }

    bool ReflectionDataExporter::ShouldIncludeEBus(
        const ReflectedEBus& ebus,
        const ReflectionExportConfig& config) const
    {
        // Check category exclusions
        if (!ebus.category.empty())
        {
            for (const auto& excludeCat : config.excludeCategories)
            {
                if (ebus.category == excludeCat)
                {
                    return false;
                }
            }
        }

        // Check category inclusions
        if (!config.includeCategories.empty())
        {
            bool found = false;
            for (const auto& includeCat : config.includeCategories)
            {
                if (ebus.category == includeCat || ebus.category.starts_with(includeCat + "/"))
                {
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                return false;
            }
        }

        return true;
    }

    // ============================================================
    // Utility Methods
    // ============================================================

    AZStd::string ReflectionDataExporter::Indent(int level, int indentSize) const
    {
        if (indentSize <= 0 || level <= 0)
        {
            return "";
        }
        return AZStd::string(static_cast<size_t>(level * indentSize), ' ');
    }

    AZStd::string ReflectionDataExporter::EscapeJsonString(const AZStd::string& input) const
    {
        AZStd::string result;
        result.reserve(input.size() + 16);

        for (char c : input)
        {
            switch (c)
            {
            case '"':
                result += "\\\"";
                break;
            case '\\':
                result += "\\\\";
                break;
            case '\b':
                result += "\\b";
                break;
            case '\f':
                result += "\\f";
                break;
            case '\n':
                result += "\\n";
                break;
            case '\r':
                result += "\\r";
                break;
            case '\t':
                result += "\\t";
                break;
            default:
                if (static_cast<unsigned char>(c) < 0x20)
                {
                    // Control character - output as unicode escape
                    char buf[8];
                    azsnprintf(buf, sizeof(buf), "\\u%04x", static_cast<unsigned int>(c));
                    result += buf;
                }
                else
                {
                    result += c;
                }
                break;
            }
        }

        return result;
    }

    AZStd::string ReflectionDataExporter::MarshalTypeToString(ReflectedParameter::MarshalType type) const
    {
        switch (type)
        {
        case ReflectedParameter::MarshalType::Void:
            return "Void";
        case ReflectedParameter::MarshalType::Bool:
            return "Bool";
        case ReflectedParameter::MarshalType::Int8:
            return "Int8";
        case ReflectedParameter::MarshalType::Int16:
            return "Int16";
        case ReflectedParameter::MarshalType::Int32:
            return "Int32";
        case ReflectedParameter::MarshalType::Int64:
            return "Int64";
        case ReflectedParameter::MarshalType::UInt8:
            return "UInt8";
        case ReflectedParameter::MarshalType::UInt16:
            return "UInt16";
        case ReflectedParameter::MarshalType::UInt32:
            return "UInt32";
        case ReflectedParameter::MarshalType::UInt64:
            return "UInt64";
        case ReflectedParameter::MarshalType::Float:
            return "Float";
        case ReflectedParameter::MarshalType::Double:
            return "Double";
        case ReflectedParameter::MarshalType::String:
            return "String";
        case ReflectedParameter::MarshalType::Vector3:
            return "Vector3";
        case ReflectedParameter::MarshalType::Quaternion:
            return "Quaternion";
        case ReflectedParameter::MarshalType::Transform:
            return "Transform";
        case ReflectedParameter::MarshalType::EntityId:
            return "EntityId";
        case ReflectedParameter::MarshalType::Object:
            return "Object";
        case ReflectedParameter::MarshalType::Unknown:
        default:
            return "Unknown";
        }
    }

    bool ReflectionDataExporter::WriteToFile(const AZStd::string& content, const AZ::IO::Path& path)
    {
        // Ensure parent directory exists
        AZ::IO::Path parentPath = path.ParentPath();
        if (!parentPath.empty())
        {
            AZ::IO::FileIOBase* fileIO = AZ::IO::FileIOBase::GetInstance();
            if (fileIO && !fileIO->Exists(parentPath.c_str()))
            {
                if (!fileIO->CreatePath(parentPath.c_str()))
                {
                    AZLOG_ERROR("ReflectionDataExporter: Failed to create directory: %s", parentPath.c_str());
                    return false;
                }
            }
        }

        // Write the file
        AZ::IO::SystemFile file;
        if (!file.Open(path.c_str(),
            AZ::IO::SystemFile::SF_OPEN_CREATE |
            AZ::IO::SystemFile::SF_OPEN_WRITE_ONLY |
            AZ::IO::SystemFile::SF_OPEN_CREATE_PATH))
        {
            AZLOG_ERROR("ReflectionDataExporter: Failed to open file for writing: %s", path.c_str());
            return false;
        }

        AZ::IO::SizeType bytesWritten = file.Write(content.data(), content.size());
        file.Close();

        if (bytesWritten != content.size())
        {
            AZLOG_ERROR("ReflectionDataExporter: Failed to write all data to file: %s", path.c_str());
            return false;
        }

        AZLOG_INFO("ReflectionDataExporter: Wrote %zu bytes to %s", bytesWritten, path.c_str());
        return true;
    }

} // namespace O3DESharp