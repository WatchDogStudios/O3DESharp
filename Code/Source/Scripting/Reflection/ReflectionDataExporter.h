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
#include <AzCore/IO/Path/Path.h>

#include <Scripting/Reflection/BehaviorContextReflector.h>

namespace O3DESharp
{
    /**
     * Configuration for reflection data export
     */
    struct ReflectionExportConfig
    {
        // Output file path (empty = return as string only)
        AZ::IO::Path outputPath;

        // Whether to pretty-print the JSON output
        bool prettyPrint = true;

        // Indentation for pretty printing (spaces)
        int indentSize = 2;

        // Whether to include deprecated members
        bool includeDeprecated = true;

        // Whether to include internal/hidden members
        bool includeInternal = false;

        // Whether to include type IDs (UUIDs)
        bool includeTypeIds = true;

        // Whether to include marshal type hints
        bool includeMarshalTypes = true;

        // Categories to include (empty = all)
        AZStd::vector<AZStd::string> includeCategories;

        // Categories to exclude
        AZStd::vector<AZStd::string> excludeCategories;

        // Class names to exclude
        AZStd::vector<AZStd::string> excludeClasses;
    };

    /**
     * Result of a reflection data export operation
     */
    struct ReflectionExportResult
    {
        bool success = false;
        AZStd::string errorMessage;

        // The exported JSON data
        AZStd::string jsonData;

        // Statistics
        size_t classesExported = 0;
        size_t ebusesExported = 0;
        size_t globalMethodsExported = 0;
        size_t globalPropertiesExported = 0;

        // Output file path (if written)
        AZ::IO::Path outputPath;

        static ReflectionExportResult Success(const AZStd::string& json);
        static ReflectionExportResult Error(const AZStd::string& message);
    };

    /**
     * ReflectionDataExporter - Exports BehaviorContext reflection data to JSON
     *
     * This class takes the metadata extracted by BehaviorContextReflector and
     * exports it to a JSON format that can be consumed by the Python binding
     * generator.
     *
     * The JSON format includes:
     * - All reflected classes with methods, properties, and constructors
     * - All reflected EBuses with events
     * - Global methods and properties
     * - Type information and marshal hints
     * - Source gem names (if resolved)
     *
     * Usage:
     * ```cpp
     * // Reflect from BehaviorContext
     * BehaviorContextReflector reflector;
     * reflector.ReflectFromContext(behaviorContext);
     *
     * // Export to JSON
     * ReflectionDataExporter exporter;
     * ReflectionExportConfig config;
     * config.outputPath = "reflection_data.json";
     * config.prettyPrint = true;
     *
     * ReflectionExportResult result = exporter.Export(reflector, config);
     *
     * if (result.success)
     * {
     *     // JSON written to file and available in result.jsonData
     * }
     * ```
     *
     * The exported JSON can then be processed by the Python binding generator:
     * ```bash
     * python generate_bindings.py --reflection-data reflection_data.json --output Generated
     * ```
     */
    class ReflectionDataExporter
    {
    public:
        AZ_RTTI(ReflectionDataExporter, "{D4E5F6A7-B8C9-0123-CDEF-456789ABCDEF}");
        AZ_CLASS_ALLOCATOR(ReflectionDataExporter, AZ::SystemAllocator);

        ReflectionDataExporter() = default;
        ~ReflectionDataExporter() = default;

        /**
         * Export reflection data to JSON
         * @param reflector The BehaviorContextReflector with extracted metadata
         * @param config Configuration for the export
         * @return Result containing JSON data and statistics
         */
        ReflectionExportResult Export(
            const BehaviorContextReflector& reflector,
            const ReflectionExportConfig& config = ReflectionExportConfig());

        /**
         * Export reflection data directly from a BehaviorContext
         * This is a convenience method that creates a temporary reflector
         * @param context The BehaviorContext to export
         * @param config Configuration for the export
         * @return Result containing JSON data and statistics
         */
        ReflectionExportResult ExportFromContext(
            AZ::BehaviorContext* context,
            const ReflectionExportConfig& config = ReflectionExportConfig());

        /**
         * Export to a JSON string (no file output)
         * @param reflector The BehaviorContextReflector with extracted metadata
         * @param prettyPrint Whether to format the output
         * @return JSON string representation of the reflection data
         */
        AZStd::string ExportToString(
            const BehaviorContextReflector& reflector,
            bool prettyPrint = true);

        /**
         * Export to a file
         * @param reflector The BehaviorContextReflector with extracted metadata
         * @param outputPath Path to write the JSON file
         * @param config Additional export configuration
         * @return Result with success/failure status
         */
        ReflectionExportResult ExportToFile(
            const BehaviorContextReflector& reflector,
            const AZ::IO::Path& outputPath,
            const ReflectionExportConfig& config = ReflectionExportConfig());

    private:
        // ============================================================
        // JSON Generation
        // ============================================================

        /**
         * Generate the complete JSON document
         */
        AZStd::string GenerateJson(
            const BehaviorContextReflector& reflector,
            const ReflectionExportConfig& config,
            ReflectionExportResult& outResult);

        /**
         * Generate JSON for a single class
         */
        AZStd::string GenerateClassJson(
            const ReflectedClass& cls,
            const ReflectionExportConfig& config,
            int indentLevel);

        /**
         * Generate JSON for a method
         */
        AZStd::string GenerateMethodJson(
            const ReflectedMethod& method,
            const ReflectionExportConfig& config,
            int indentLevel);

        /**
         * Generate JSON for a property
         */
        AZStd::string GeneratePropertyJson(
            const ReflectedProperty& property,
            const ReflectionExportConfig& config,
            int indentLevel);

        /**
         * Generate JSON for an EBus
         */
        AZStd::string GenerateEBusJson(
            const ReflectedEBus& ebus,
            const ReflectionExportConfig& config,
            int indentLevel);

        /**
         * Generate JSON for an EBus event
         */
        AZStd::string GenerateEBusEventJson(
            const ReflectedEBusEvent& event,
            const ReflectionExportConfig& config,
            int indentLevel);

        /**
         * Generate JSON for a parameter
         */
        AZStd::string GenerateParameterJson(
            const ReflectedParameter& param,
            const ReflectionExportConfig& config,
            int indentLevel);

        // ============================================================
        // Filtering
        // ============================================================

        /**
         * Check if a class should be included in the export
         */
        bool ShouldIncludeClass(
            const ReflectedClass& cls,
            const ReflectionExportConfig& config) const;

        /**
         * Check if an EBus should be included in the export
         */
        bool ShouldIncludeEBus(
            const ReflectedEBus& ebus,
            const ReflectionExportConfig& config) const;

        // ============================================================
        // Utility Methods
        // ============================================================

        /**
         * Generate indentation string
         */
        AZStd::string Indent(int level, int indentSize) const;

        /**
         * Escape a string for JSON
         */
        AZStd::string EscapeJsonString(const AZStd::string& input) const;

        /**
         * Convert marshal type to string
         */
        AZStd::string MarshalTypeToString(ReflectedParameter::MarshalType type) const;

        /**
         * Write JSON content to a file
         */
        bool WriteToFile(const AZStd::string& content, const AZ::IO::Path& path);
    };

    // ============================================================
    // EBus Interface for Editor/Script Access
    // ============================================================

    /**
     * EBus interface for requesting reflection data export
     * This allows scripts and editor tools to trigger export operations
     */
    class ReflectionDataExportRequests
        : public AZ::EBusTraits
    {
    public:
        static const AZ::EBusHandlerPolicy HandlerPolicy = AZ::EBusHandlerPolicy::Single;
        static const AZ::EBusAddressPolicy AddressPolicy = AZ::EBusAddressPolicy::Single;

        virtual ~ReflectionDataExportRequests() = default;

        /**
         * Export reflection data to a JSON file
         * @param outputPath Path to write the JSON file
         * @return True if successful
         */
        virtual bool ExportReflectionData(const AZStd::string& outputPath) = 0;

        /**
         * Get reflection data as a JSON string
         * @return JSON string containing all reflection data
         */
        virtual AZStd::string GetReflectionDataJson() = 0;

        /**
         * Get reflection data for a specific category
         * @param category The category to filter by
         * @return JSON string containing filtered reflection data
         */
        virtual AZStd::string GetReflectionDataForCategory(const AZStd::string& category) = 0;

        /**
         * Get a list of all reflected class names
         * @return Vector of class names
         */
        virtual AZStd::vector<AZStd::string> GetReflectedClassNames() = 0;

        /**
         * Get a list of all reflected EBus names
         * @return Vector of EBus names
         */
        virtual AZStd::vector<AZStd::string> GetReflectedEBusNames() = 0;

        /**
         * Get all unique categories in the reflection data
         * @return Vector of category names
         */
        virtual AZStd::vector<AZStd::string> GetReflectedCategories() = 0;
    };

    using ReflectionDataExportRequestBus = AZ::EBus<ReflectionDataExportRequests>;

} // namespace O3DESharp