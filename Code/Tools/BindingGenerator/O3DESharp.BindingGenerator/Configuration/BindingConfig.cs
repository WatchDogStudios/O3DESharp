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
            "AZ_COMPILER_CLANG=1"
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
