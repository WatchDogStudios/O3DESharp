//
// Copyright (c) Contributors to the Open 3D Engine Project.
// For complete copyright and license terms please see the LICENSE at the root of this distribution.
//
// SPDX-License-Identifier: Apache-2.0 OR MIT
//

using System;
using System.IO;
using O3DESharp.BindingGenerator.Configuration;

namespace O3DESharp.BindingGenerator.Tests;

/// <summary>
/// Tests for BindingConfigLoader's parse-failure diagnostics. A malformed
/// binding_config.json previously fell back to defaults silently - the
/// config load "succeeded" from the caller's point of view even though every
/// custom setting was discarded. These pin the fix: Load's new
/// loadedSuccessfully out-parameter distinguishes "file missing" and "file
/// exists but failed to parse" from a genuine successful load, and the
/// failure console message is an unambiguous WARNING rather than looking
/// like routine output.
/// </summary>
public class BindingConfigLoaderTests
{
    [Fact]
    public void Load_WithMissingFile_ReturnsDefaultAndReportsNotLoaded()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.json");

        var config = BindingConfigLoader.Load(missingPath, out bool loadedSuccessfully);

        loadedSuccessfully.Should().BeFalse();
        config.Should().NotBeNull();
    }

    [Fact]
    public void Load_WithMalformedJson_ReturnsDefaultAndReportsNotLoaded()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"malformed-config-{Guid.NewGuid():N}.json");
        File.WriteAllText(tempPath, "{ this is not valid json ");

        try
        {
            var config = BindingConfigLoader.Load(tempPath, out bool loadedSuccessfully);

            loadedSuccessfully.Should().BeFalse(
                "a file that exists but fails to parse must not be reported as successfully loaded");
            config.Should().NotBeNull();
            // Defaults seed two O3DE include paths (see BindingConfigLoader.CreateDefault) -
            // confirms we actually fell back to CreateDefault(), not a half-parsed object.
            config.Global.IncludePaths.Should().HaveCount(2);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void Load_WithMalformedJson_PrintsUnambiguousWarning()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"malformed-config-{Guid.NewGuid():N}.json");
        File.WriteAllText(tempPath, "{ not: valid, json }");

        var originalOut = Console.Out;
        try
        {
            using var writer = new StringWriter();
            Console.SetOut(writer);

            BindingConfigLoader.Load(tempPath, out _);

            var output = writer.ToString();
            output.Should().Contain("WARNING");
            output.Should().Contain(tempPath);
            output.Should().Contain("using default configuration", "message should say defaults were used, not silently imply success");
        }
        finally
        {
            Console.SetOut(originalOut);
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void Load_WithValidJson_ReturnsParsedConfigAndReportsLoaded()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"valid-config-{Guid.NewGuid():N}.json");
        File.WriteAllText(tempPath, """
        {
            "global": {
                "requireExportAttribute": true,
                "includePaths": ["/custom/include"]
            },
            "gems": {}
        }
        """);

        try
        {
            var config = BindingConfigLoader.Load(tempPath, out bool loadedSuccessfully);

            loadedSuccessfully.Should().BeTrue();
            config.Global.RequireExportAttribute.Should().BeTrue();
            config.Global.IncludePaths.Should().Contain("/custom/include");
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void Load_SingleArgOverload_StillWorksForBackwardCompatibility()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"valid-config-{Guid.NewGuid():N}.json");
        File.WriteAllText(tempPath, """{ "global": {}, "gems": {} }""");

        try
        {
            // The original single-argument Load(string) must still work for any
            // caller that doesn't need the loadedSuccessfully signal.
            var config = BindingConfigLoader.Load(tempPath);

            config.Should().NotBeNull();
        }
        finally
        {
            File.Delete(tempPath);
        }
    }
}
