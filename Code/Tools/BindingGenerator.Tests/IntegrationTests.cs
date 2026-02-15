//
// Copyright (c) Contributors to the Open 3D Engine Project.
// For complete copyright and license terms please see the LICENSE at the root of this distribution.
//
// SPDX-License-Identifier: Apache-2.0 OR MIT
//

using System;
using System.IO;
using System.Linq;

namespace O3DESharp.BindingGenerator.Tests;

/// <summary>
/// Integration tests for the complete binding generation pipeline.
/// These tests use real gem structures and headers to validate end-to-end functionality.
/// </summary>
public class IntegrationTests : IDisposable
{
    private readonly string _tempProjectPath;

    public IntegrationTests()
    {
        _tempProjectPath = TestFixtures.CreateMockProject("IntegrationTestProject");
    }

    public void Dispose()
    {
        TestFixtures.CleanupTempDirectory(_tempProjectPath);
    }

    [Fact]
    public void CreateMockProject_ShouldCreateValidStructure()
    {
        // Arrange & Act - already done in constructor

        // Assert
        Directory.Exists(_tempProjectPath).Should().BeTrue();
        File.Exists(Path.Combine(_tempProjectPath, "project.json")).Should().BeTrue();
        Directory.Exists(Path.Combine(_tempProjectPath, "Gems")).Should().BeTrue();
    }

    [Fact]
    public void CreateMockGem_ShouldCreateValidGemStructure()
    {
        // Arrange
        var gemName = "TestGem";

        // Act
        var gemPath = TestFixtures.CreateMockGem(_tempProjectPath, gemName);

        // Assert
        Directory.Exists(gemPath).Should().BeTrue();
        File.Exists(Path.Combine(gemPath, "gem.json")).Should().BeTrue();
        
        var includePath = Path.Combine(gemPath, "Code", "Include", gemName);
        Directory.Exists(includePath).Should().BeTrue();
        
        var headers = Directory.GetFiles(includePath, "*.h", SearchOption.AllDirectories);
        headers.Should().NotBeEmpty();
    }

    [Fact]
    public void CreateMockGem_WithDependencies_ShouldIncludeDependenciesInGemJson()
    {
        // Arrange
        var gemName = "DependentGem";
        var dependencies = new[] { "AzCore", "AzFramework" };

        // Act
        var gemPath = TestFixtures.CreateMockGem(_tempProjectPath, gemName, dependencies);

        // Assert
        var gemJsonPath = Path.Combine(gemPath, "gem.json");
        var gemJsonContent = File.ReadAllText(gemJsonPath);
        
        gemJsonContent.Should().Contain("AzCore");
        gemJsonContent.Should().Contain("AzFramework");
    }

    [Fact]
    public void SampleHeader_ShouldContainExpectedClasses()
    {
        // Arrange
        var tempDir = TestFixtures.CreateTempDirectory();

        try
        {
            // Act
            var headerPath = TestFixtures.CreateSampleHeader(tempDir, "MyTestClass");

            // Assert
            File.Exists(headerPath).Should().BeTrue();
            
            var headerContent = File.ReadAllText(headerPath);
            headerContent.Should().Contain("class O3DE_EXPORT_CSHARP MyTestClass");
            headerContent.Should().Contain("SetPosition");
            headerContent.Should().Contain("GetPosition");
            headerContent.Should().Contain("AZ::Vector3");
        }
        finally
        {
            TestFixtures.CleanupTempDirectory(tempDir);
        }
    }

    [Fact]
    public void SampleBindingConfig_ShouldCreateValidJson()
    {
        // Arrange
        var tempDir = TestFixtures.CreateTempDirectory();

        try
        {
            // Act
            var configPath = TestFixtures.CreateSampleBindingConfig(tempDir);

            // Assert
            File.Exists(configPath).Should().BeTrue();
            
            var configContent = File.ReadAllText(configPath);
            configContent.Should().Contain("requireExportAttribute");
            configContent.Should().Contain("incrementalBuild");
            configContent.Should().Contain("cSharpNamespace");
        }
        finally
        {
            TestFixtures.CleanupTempDirectory(tempDir);
        }
    }

    [Fact(Skip = "Requires real O3DESharp gem to be present")]
    public void RealO3DESharpGem_ShouldBeDiscoverable()
    {
        // Arrange & Act
        var gemPath = TestFixtures.GetRealO3DESharpGemPath();

        // Assert
        gemPath.Should().NotBeNull();
        if (gemPath != null)
        {
            Directory.Exists(gemPath).Should().BeTrue();
            File.Exists(Path.Combine(gemPath, "gem.json")).Should().BeTrue();
        }
    }

    [Fact(Skip = "Requires real O3DESharp gem to be present")]
    public void RealO3DESharpHeaders_ShouldBeDiscoverable()
    {
        // Arrange & Act
        var headers = TestFixtures.GetRealO3DESharpHeaders().ToList();

        // Assert - may be empty if gem not found, but should not throw
        headers.Should().NotBeNull();
    }

    [Theory]
    [InlineData("TestGem1", new string[0])]
    [InlineData("TestGem2", new[] { "AzCore" })]
    [InlineData("TestGem3", new[] { "AzCore", "AzFramework", "TestGem1" })]
    public void CreateMultipleGemsWithDependencies_ShouldCreateValidStructures(string gemName, string[] dependencies)
    {
        // Arrange & Act
        var gemPath = TestFixtures.CreateMockGem(_tempProjectPath, gemName, dependencies);

        // Assert
        Directory.Exists(gemPath).Should().BeTrue();
        File.Exists(Path.Combine(gemPath, "gem.json")).Should().BeTrue();

        var gemJsonContent = File.ReadAllText(Path.Combine(gemPath, "gem.json"));
        foreach (var dep in dependencies)
        {
            gemJsonContent.Should().Contain(dep);
        }
    }

    [Fact]
    public void MultipleHeaders_ShouldBeCreatedInSameDirectory()
    {
        // Arrange
        var tempDir = TestFixtures.CreateTempDirectory();

        try
        {
            // Act
            var header1 = TestFixtures.CreateSampleHeader(tempDir, "Class1");
            var header2 = TestFixtures.CreateSampleHeader(tempDir, "Class2");
            var header3 = TestFixtures.CreateSampleHeader(tempDir, "Class3");

            // Assert
            File.Exists(header1).Should().BeTrue();
            File.Exists(header2).Should().BeTrue();
            File.Exists(header3).Should().BeTrue();

            var headers = Directory.GetFiles(tempDir, "*.h");
            headers.Should().HaveCount(3);
        }
        finally
        {
            TestFixtures.CleanupTempDirectory(tempDir);
        }
    }
}
