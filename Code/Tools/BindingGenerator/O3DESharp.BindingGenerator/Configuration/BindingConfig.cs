/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

using System.Collections.Generic;

namespace O3DESharp.BindingGenerator.Configuration
{
    /// <summary>
    /// Root configuration for the binding generator
    /// </summary>
    public class BindingConfig
    {
        /// <summary>
        /// Global settings that apply to all gems
        /// </summary>
        public GlobalSettings Global { get; set; } = new GlobalSettings();

        /// <summary>
        /// Per-gem specific configuration
        /// </summary>
        public Dictionary<string, GemSettings> Gems { get; set; } = new Dictionary<string, GemSettings>();
    }

    /// <summary>
    /// Global settings that apply to all generated bindings
    /// </summary>
    public class GlobalSettings
    {
        /// <summary>
        /// C# namespace for generated bindings (default: "O3DE")
        /// </summary>
        public string CSharpNamespace { get; set; } = "O3DE";

        /// <summary>
        /// Output directory for generated C# code (relative to gem root)
        /// </summary>
        public string CSharpOutputPath { get; set; } = "Assets/Scripts/{GemName}";

        /// <summary>
        /// Output directory for generated C++ code (relative to gem root)
        /// </summary>
        public string CppOutputPath { get; set; } = "Code/Source/Scripting/Generated";

        /// <summary>
        /// Additional include directories (system-wide)
        /// </summary>
        public List<string> IncludePaths { get; set; } = new List<string>();

        /// <summary>
        /// Platform-specific preprocessor defines
        /// </summary>
        public List<string> Defines { get; set; } = new List<string>
        {
            "O3DE_EXPORT_CSHARP=__attribute__((annotate(\"export_csharp\")))",
            "AZ_COMPILER_CLANG=1",
            // Platform defines for Windows parsing
            "AZ_PLATFORM_WINDOWS=1",
            "AZ_TRAIT_OS_IS_HOST_OS_PLATFORM=1",
            "_WIN64=1",
            "_MSC_VER=1900",
            "_ALLOW_COMPILER_AND_STL_VERSION_MISMATCH=1",
            // O3DE DLL export/import stubs for parsing
            "AZ_DLL_EXPORT=",
            "AZ_DLL_IMPORT=",
            "AZ_DLL=",
            // Common O3DE macros that need to be defined for parsing
            "AZ_RTTI(...)=",
            "AZ_RTTI_NO_TYPE_INFO_DECL(...)=",
            "AZ_TYPE_INFO(...)=",
            "AZ_TYPE_INFO_WITH_NAME(...)=",
            "AZ_CLASS_ALLOCATOR(...)=",
            "AZ_CLASS_ALLOCATOR_DECL=",
            "AZ_COMPONENT(...)=",
            "AZ_COMPONENT_DECL(...)=",
            "AZ_COMPONENT_BASE(...)=",
            // EBus macros
            "AZ_EBUS_BEHAVIOR_BINDER(...)=",
            "AZ_EBUS_BEHAVIOR_BINDER_WITH_DOC(...)=",
            // Disable warnings macros
            "AZ_PUSH_DISABLE_WARNING(...)=",
            "AZ_POP_DISABLE_WARNING=",
            "AZ_PUSH_DISABLE_DLL_EXPORT_BASECLASS_WARNING=",
            "AZ_POP_DISABLE_DLL_EXPORT_BASECLASS_WARNING=",
            "AZ_PUSH_DISABLE_DLL_EXPORT_MEMBER_WARNING=",
            "AZ_POP_DISABLE_DLL_EXPORT_MEMBER_WARNING=",
            // Misc macros
            "AZ_FORCE_INLINE=inline",
            "AZ_INLINE=inline",
            "AZ_DEPRECATED(...)=",
            "AZ_DISABLE_COPY(...)=",
            "AZ_DISABLE_MOVE(...)=",
            "AZ_DISABLE_COPY_MOVE(...)=",
            "AZ_DEFAULT_COPY(...)=",
            "AZ_DEFAULT_MOVE(...)=",
            "AZ_DEFAULT_COPY_MOVE(...)=",
            // Reflection macros
            "AZ_SERIALIZE_FRIEND(...)=",
            "AZ_BEHAVIOR_CONTEXT_FRIEND=",
        };

        /// <summary>
        /// Enable verbose logging
        /// </summary>
        public bool Verbose { get; set; } = false;

        /// <summary>
        /// Enable incremental builds (use file hash caching)
        /// </summary>
        public bool IncrementalBuild { get; set; } = true;

        /// <summary>
        /// Cache file path for incremental builds
        /// </summary>
        public string CacheFilePath { get; set; } = ".binding_cache.json";

        /// <summary>
        /// Require O3DE_EXPORT_CSHARP attribute on declarations.
        /// If false, all public declarations will be exported.
        /// </summary>
        public bool RequireExportAttribute { get; set; } = false;

        /// <summary>
        /// Generate fluent API extension methods (e.g., WithPosition, WithRotation)
        /// </summary>
        public bool GenerateExtensionMethods { get; set; } = true;

        /// <summary>
        /// Generate runtime metadata for hot reload and reflection
        /// </summary>
        public bool GenerateMetadata { get; set; } = true;
    }

    /// <summary>
    /// Per-gem configuration settings
    /// </summary>
    public class GemSettings
    {
        /// <summary>
        /// Whether to generate bindings for this gem
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Additional include directories specific to this gem
        /// </summary>
        public List<string> IncludePaths { get; set; } = new List<string>();

        /// <summary>
        /// Header files to parse (supports wildcards like **/*.h)
        /// </summary>
        public List<string> HeaderPatterns { get; set; } = new List<string>
        {
            "Code/Include/**/*.h",
            "Code/Source/**/*.h"
        };

        /// <summary>
        /// Header files to exclude (supports wildcards)
        /// </summary>
        public List<string> ExcludePatterns { get; set; } = new List<string>
        {
            "**/Platform/**",
            "**/Tests/**"
        };

        /// <summary>
        /// C# namespace override for this gem
        /// </summary>
        public string? CSharpNamespace { get; set; }

        /// <summary>
        /// Additional preprocessor defines for this gem
        /// </summary>
        public List<string> Defines { get; set; } = new List<string>();

        /// <summary>
        /// Require O3DE_EXPORT_CSHARP attribute on declarations for this gem.
        /// If null, uses the global setting. If false, all public declarations will be exported.
        /// </summary>
        public bool? RequireExportAttribute { get; set; }
    }
}
