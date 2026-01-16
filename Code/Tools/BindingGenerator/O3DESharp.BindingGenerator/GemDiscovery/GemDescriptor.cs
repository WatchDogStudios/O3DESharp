/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace O3DESharp.BindingGenerator.GemDiscovery
{
    /// <summary>
    /// Represents an O3DE gem descriptor (gem.json)
    /// </summary>
    public class GemDescriptor
    {
        /// <summary>
        /// Unique gem identifier
        /// </summary>
        [JsonPropertyName("gem_name")]
        public string GemName { get; set; } = string.Empty;

        /// <summary>
        /// Display name for the gem
        /// </summary>
        [JsonPropertyName("display_name")]
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>
        /// Gem version
        /// </summary>
        [JsonPropertyName("version")]
        public string Version { get; set; } = "1.0.0";

        /// <summary>
        /// License identifier
        /// </summary>
        [JsonPropertyName("license")]
        public string License { get; set; } = string.Empty;

        /// <summary>
        /// Origin/author
        /// </summary>
        [JsonPropertyName("origin")]
        public string Origin { get; set; } = string.Empty;

        /// <summary>
        /// Gem type (Code, Asset, etc.)
        /// </summary>
        [JsonPropertyName("type")]
        public string Type { get; set; } = "Code";

        /// <summary>
        /// Brief description
        /// </summary>
        [JsonPropertyName("summary")]
        public string Summary { get; set; } = string.Empty;

        /// <summary>
        /// List of gem dependencies
        /// </summary>
        [JsonPropertyName("dependencies")]
        public List<string> Dependencies { get; set; } = new List<string>();

        /// <summary>
        /// Path to the gem directory (not in gem.json, set during discovery)
        /// </summary>
        [JsonIgnore]
        public string GemPath { get; set; } = string.Empty;

        /// <summary>
        /// Whether this gem is enabled in the project
        /// </summary>
        [JsonIgnore]
        public bool IsEnabled { get; set; } = false;

        public override string ToString()
        {
            return $"{GemName} v{Version} ({Type})";
        }
    }

    /// <summary>
    /// Represents an O3DE project descriptor (project.json)
    /// </summary>
    public class ProjectDescriptor
    {
        /// <summary>
        /// Project name
        /// </summary>
        [JsonPropertyName("project_name")]
        public string ProjectName { get; set; } = string.Empty;

        /// <summary>
        /// Project version
        /// </summary>
        [JsonPropertyName("version")]
        public string Version { get; set; } = "1.0.0";

        /// <summary>
        /// List of enabled gems
        /// </summary>
        [JsonPropertyName("gem_names")]
        public List<string> GemNames { get; set; } = new List<string>();

        /// <summary>
        /// Path to O3DE engine
        /// </summary>
        [JsonPropertyName("engine")]
        public string? Engine { get; set; }
    }
}
