# O3DESharp Python Tests

This directory contains unit and integration tests for the O3DESharp Python binding generator scripts.

## Test Structure

- `conftest.py` - Pytest configuration and shared fixtures
- `test_clangsharp_invoker.py` - Tests for ClangSharp tool invocation
- `test_type_mapper.py` - Tests for C++ to C# type mapping
- `test_gem_dependency_resolver.py` - Tests for gem discovery and dependency resolution
- `pytest.ini` - Pytest configuration file

## Running Tests

### Install Dependencies

```bash
pip install pytest pytest-mock
```

### Run All Tests

```bash
cd Editor/Tests
pytest
```

### Run Specific Test Categories

```bash
# Run only unit tests (mocked dependencies)
pytest -m unit

# Run only integration tests (uses real gems)
pytest -m integration

# Run specific test file
pytest test_clangsharp_invoker.py

# Run with verbose output
pytest -v

# Run with coverage (requires pytest-cov)
pytest --cov=../Scripts --cov-report=html
```

## Test Categories

### Unit Tests (`@pytest.mark.unit`)
- Fast-running tests with mocked dependencies
- Test individual functions and classes in isolation
- No file system or network access

### Integration Tests (`@pytest.mark.integration`)
- Use real gem.json files from workspace
- Test gem discovery with actual O3DE gems
- May be slower due to file system access

## Fixtures

The `conftest.py` file provides several fixtures for testing:

- `gem_root` - Path to O3DESharp gem root
- `sample_gem_json` - Real O3DESharp gem.json data
- `sample_cpp_header` - Sample C++ header for testing
- `sample_binding_config` - Sample binding_config.json
- `o3desharp_headers` - List of real O3DESharp header files
- `workspace_gems` - Dictionary of discovered gems in workspace
- `real_gem_dependencies` - O3DESharp gem dependencies

## Writing New Tests

1. Create a new `test_*.py` file
2. Import pytest and modules to test
3. Use fixtures from `conftest.py`
4. Mark tests with `@pytest.mark.unit` or `@pytest.mark.integration`
5. Follow naming convention: `test_<what_is_being_tested>`

Example:

```python
import pytest
from csharp_binding_generator import ClangSharpInvoker

@pytest.mark.unit
class TestMyFeature:
    def test_basic_functionality(self):
        # Arrange
        invoker = ClangSharpInvoker()
        
        # Act
        result = invoker.check_tool_available()
        
        # Assert
        assert isinstance(result, tuple)
```

## CI Integration

These tests are designed to run in CI/CD pipelines. They are integrated into the CMake build system via the `O3DESharp.Tests.Python` target.

To run from CMake:

```bash
cmake --build . --target O3DESharp.Tests.Python
```

Or via CTest:

```bash
ctest -L "python;bindings"
```
