//
// Copyright (c) Contributors to the Open 3D Engine Project.
// For complete copyright and license terms please see the LICENSE at the root of this distribution.
//
// SPDX-License-Identifier: Apache-2.0 OR MIT
//

using System.IO;
using FluentAssertions;
using O3DESharp.BindingGenerator.Configuration;
using Xunit;

namespace O3DESharp.BindingGenerator.Tests;

public class BindingConfigExcludedGemsTests
{
    [Fact]
    public void Load_ParsesReflectionBackendExcludedGems()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """
            {
                "reflectionBackendExcludedGems": ["HugeGem", "IrrelevantGem"]
            }
            """);

            var config = BindingConfigLoader.Load(path);

            config.ReflectionBackendExcludedGems.Should().BeEquivalentTo(new[] { "HugeGem", "IrrelevantGem" });
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_DefaultsToEmptyList_WhenKeyAbsent()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{}");

            var config = BindingConfigLoader.Load(path);

            config.ReflectionBackendExcludedGems.Should().BeEmpty();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_MissingFile_DefaultsToEmptyList()
    {
        // BindingConfigLoader.Load gracefully returns CreateDefault() for a
        // missing file (Configuration/BindingConfigLoader.cs:62-67) - the
        // reflection backend must stay usable with zero config file at all.
        var config = BindingConfigLoader.Load(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Path.GetRandomFileName() + ".json"));

        config.ReflectionBackendExcludedGems.Should().BeEmpty();
    }
}
