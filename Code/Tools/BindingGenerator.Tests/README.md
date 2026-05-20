# O3DESharp Binding Generator Tests

This project contains unit and integration tests for the ClangSharp-based C# binding generator.

## Test Structure

- `BindingGenerator.Tests.csproj` - xUnit test project
- `TestFixtures.cs` - Shared test fixtures and helper methods
- `IntegrationTests.cs` - Integration tests using real/mock gem structures

## Running Tests

### From Visual Studio

1. Open `O3DESharp.sln` in Visual Studio
2. Build the solution (Ctrl+Shift+B)
3. Open Test Explorer (Test > Test Explorer)
4. Click "Run All" to run all tests

### From Command Line

```bash
cd Code/Tools/BindingGenerator.Tests
dotnet test
```

### Run with Coverage

```bash
dotnet test --collect:"XPlat Code Coverage"
```

### Run Specific Tests

```bash
# Run specific test class
dotnet test --filter "FullyQualifiedName~IntegrationTests"

# Run specific test method
dotnet test --filter "FullyQualifiedName~IntegrationTests.CreateMockProject_ShouldCreateValidStructure"

# Run tests with specific trait
dotnet test --filter "Category=Integration"
```

## Test Categories

### Integration Tests
- Use real or mock gem structures
- Test complete binding generation pipeline
- Validate generated C# and C++ code
- May require file system access

## Test Fixtures

The `TestFixtures` class provides helper methods:

### Project/Gem Creation
- `CreateTempDirectory()` - Creates temporary test directory
- `CreateMockProject()` - Creates mock O3DE project structure
- `CreateMockGem()` - Creates mock gem with headers
- `CreateSampleHeader()` - Creates sample C++ header
- `CreateSampleGemJson()` - Creates sample gem.json
- `CreateSampleBindingConfig()` - Creates sample binding_config.json

### Real Gem Access
- `GetRealO3DESharpGemPath()` - Gets path to real O3DESharp gem
- `GetRealO3DESharpHeaders()` - Gets real O3DESharp headers

### Cleanup
- `CleanupTempDirectory()` - Removes temporary directories

## Writing New Tests

1. Create a new `.cs` file or add to existing test class
2. Use `[Fact]` for single tests or `[Theory]` for parameterized tests
3. Use FluentAssertions for readable assertions
4. Use test fixtures from `TestFixtures` class
5. Clean up resources with `IDisposable` pattern

Example:

```csharp
public class MyFeatureTests : IDisposable
{
    private readonly string _tempPath;

    public MyFeatureTests()
    {
        _tempPath = TestFixtures.CreateTempDirectory();
    }

    [Fact]
    public void MyFeature_ShouldWork()
    {
        // Arrange
        var input = "test";

        // Act
        var result = MyFeature.Process(input);

        // Assert
        result.Should().Be("expected");
    }

    public void Dispose()
    {
        TestFixtures.CleanupTempDirectory(_tempPath);
    }
}
```

## CI Integration

These tests are integrated into the O3DESharp CMake build system. They run automatically as part of the `O3DESharp.Tests.BindingGenerator` target.

To run from CMake:

```bash
cmake --build . --target O3DESharp.Tests.BindingGenerator
```

Or via CTest:

```bash
ctest -L "csharp;bindings"
```

## Dependencies

- .NET 8.0 SDK
- xUnit 2.6.2
- FluentAssertions 6.12.0
- O3DESharp.BindingGenerator project reference

## Adding New Test Files

When adding new test files to this project:

1. Create the `.cs` file in this directory
2. The project uses wildcard inclusion (`**/*.cs`)
3. Rebuild the project
4. Tests will automatically appear in Test Explorer

## Troubleshooting

### Tests Not Appearing
- Ensure the project is built successfully
- Check that test methods are marked with `[Fact]` or `[Theory]`
- Verify test class is public
- Rebuild the solution

### Tests Failing Locally
- Check that .NET 8.0 SDK is installed
- Verify O3DESharp.BindingGenerator project builds
- Ensure temp directory is writable
- Check file paths use Path.Combine for cross-platform support

### Real Gem Tests Skipped
- Tests marked with `Skip` require the real O3DESharp gem
- These run in CI but may skip in isolated test environments
- Use mock gems for most testing scenarios
