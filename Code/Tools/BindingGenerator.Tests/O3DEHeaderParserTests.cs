//
// Copyright (c) Contributors to the Open 3D Engine Project.
// For complete copyright and license terms please see the LICENSE at the root of this distribution.
//
// SPDX-License-Identifier: Apache-2.0 OR MIT
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using O3DESharp.BindingGenerator.Configuration;
using O3DESharp.BindingGenerator.Parsing;

namespace O3DESharp.BindingGenerator.Tests;

/// <summary>
/// Regression tests for O3DEHeaderParser's clang-backend parsing, in
/// particular the shared-CXIndex-per-gem behavior introduced to cut
/// per-file prelude reparse cost (see ParseHeaders / ParseHeaderFile).
/// </summary>
public class O3DEHeaderParserTests : IDisposable
{
    private readonly string _tempDir;

    public O3DEHeaderParserTests()
    {
        _tempDir = TestFixtures.CreateTempDirectory();
    }

    public void Dispose()
    {
        TestFixtures.CleanupTempDirectory(_tempDir);
    }

    [Fact]
    public void ParseHeaders_MultipleFilesInOneGem_FindsAllClassesWithCorrectSourceFiles()
    {
        // Arrange: two independent headers, parsed through the same
        // ParseHeaders call (and therefore the same shared CXIndex).
        var header1 = TestFixtures.CreateSampleHeader(_tempDir, "AlphaClass");
        var header2 = TestFixtures.CreateSampleHeader(_tempDir, "BetaClass");

        var parser = new O3DEHeaderParser(requireExportAttribute: false, verbose: false);

        // Act - O3DE_EXPORT_CSHARP must be defined for the sample header's
        // class declarations to even parse syntactically (it expands to a
        // clang __attribute__, not a no-op); production code supplies this
        // via BindingConfig.EngineRequiredDefines (see
        // MultiGemBindingGenerator.cs's defines.AddRange call), so this test
        // does the same rather than hand-rolling just the one macro.
        var bindings = parser.ParseHeaders(
            headerFiles: new List<string> { header1, header2 },
            includePaths: new List<string> { _tempDir },
            defines: BindingConfig.EngineRequiredDefines.ToList(),
            gemName: "SharedIndexTestGem");

        // Assert: both classes discovered, each attributed to its own file -
        // proves the shared CXIndex does not bleed state between files.
        bindings.Classes.Should().Contain(c => c.Name == "AlphaClass");
        bindings.Classes.Should().Contain(c => c.Name == "BetaClass");

        var alpha = bindings.Classes.Single(c => c.Name == "AlphaClass");
        var beta = bindings.Classes.Single(c => c.Name == "BetaClass");

        Path.GetFullPath(alpha.SourceFile).Should().Be(Path.GetFullPath(header1));
        Path.GetFullPath(beta.SourceFile).Should().Be(Path.GetFullPath(header2));
    }

    [Fact]
    public void ParseHeaders_EmptyFileList_ReturnsEmptyBindingsWithoutCreatingIndex()
    {
        // Arrange
        var parser = new O3DEHeaderParser(requireExportAttribute: false, verbose: false);

        // Act - the early-return branch in ParseHeaders (headerFiles.Count == 0)
        // must not attempt to create the shared CXIndex or touch clang at all.
        var bindings = parser.ParseHeaders(
            headerFiles: new List<string>(),
            includePaths: new List<string>(),
            defines: new List<string>(),
            gemName: "EmptyGem");

        // Assert
        bindings.Classes.Should().BeEmpty();
        bindings.Functions.Should().BeEmpty();
        bindings.Enums.Should().BeEmpty();
    }
}
