//
// Copyright (c) Contributors to the Open 3D Engine Project.
// For complete copyright and license terms please see the LICENSE at the root of this distribution.
//
// SPDX-License-Identifier: Apache-2.0 OR MIT
//

using System.IO;
using O3DESharp.BindingGenerator.Configuration;

namespace O3DESharp.BindingGenerator.Tests;

/// <summary>
/// Tests for <see cref="ReflectionDataPathResolver"/> - the logic that decides
/// where the reflection backend reads reflection_data.json from when
/// --reflection-data is not passed explicitly.
///
/// Regression coverage for a real bug: the resolver's default used to point at
/// &lt;project&gt;/Cache/pc/generated/reflection_data.json, but
/// O3DESharpSystemComponent::AutoExportReflectionData actually writes to
/// &lt;project&gt;/Generated/reflection_data.json - so the default silently never
/// found the file the editor produced.
/// </summary>
public class ReflectionDataPathResolverTests
{
    [Fact]
    public void Resolve_WithoutOverride_DefaultsToGeneratedFolderUnderProject()
    {
        var projectPath = Path.Combine("C:", "Projects", "MyGame");

        var resolved = ReflectionDataPathResolver.Resolve(projectPath, overridePath: null);

        resolved.Should().Be(Path.Combine(projectPath, "Generated", "reflection_data.json"));
    }

    [Fact]
    public void Resolve_WithEmptyOverride_FallsBackToDefault()
    {
        var projectPath = Path.Combine("C:", "Projects", "MyGame");

        var resolved = ReflectionDataPathResolver.Resolve(projectPath, overridePath: "");

        resolved.Should().Be(Path.Combine(projectPath, "Generated", "reflection_data.json"));
    }

    [Fact]
    public void Resolve_WithOverride_ReturnsFullPathOfOverrideInsteadOfDefault()
    {
        var projectPath = Path.Combine("C:", "Projects", "MyGame");
        var overridePath = Path.Combine("D:", "custom", "reflection_data.json");

        var resolved = ReflectionDataPathResolver.Resolve(projectPath, overridePath);

        resolved.Should().Be(Path.GetFullPath(overridePath));
    }
}
