/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

using System;
using System.IO;

namespace O3DESharp.BindingGenerator.Configuration
{
    /// <summary>
    /// Resolves the reflection_data.json path the reflection backend reads from
    /// when --reflection-data is not passed explicitly.
    /// </summary>
    public static class ReflectionDataPathResolver
    {
        /// <summary>
        /// Resolves the path to reflection_data.json. If <paramref name="overridePath"/>
        /// is supplied (the CLI's --reflection-data), it wins. Otherwise defaults to
        /// &lt;projectPath&gt;/Generated/reflection_data.json - the same "Generated" folder
        /// O3DESharpSystemComponent::AutoExportReflectionData writes to, and the same
        /// root this tool's own --output default (Generated/CSharp) already uses. This
        /// is deliberately NOT under Cache/&lt;platform&gt;/ - that folder is populated by
        /// O3DE's AssetProcessor pipeline for cataloged/processed assets, and nothing
        /// registers an asset builder for this ad hoc JSON dump.
        /// </summary>
        /// <param name="projectPath">Absolute path to the O3DE project directory.</param>
        /// <param name="overridePath">Explicit --reflection-data value, if any.</param>
        public static string Resolve(string projectPath, string? overridePath)
        {
            if (!string.IsNullOrEmpty(overridePath))
            {
                return Path.GetFullPath(overridePath);
            }

            return Path.Combine(projectPath, "Generated", "reflection_data.json");
        }
    }
}
