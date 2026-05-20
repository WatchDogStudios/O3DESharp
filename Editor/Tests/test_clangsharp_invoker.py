#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
"""
Unit tests for ClangSharpInvoker class.

Tests the command-line argument construction and subprocess execution
for the ClangSharp binding generator tool.
"""

import pytest
from pathlib import Path
from unittest.mock import Mock, patch, MagicMock
import subprocess

from csharp_binding_generator import (
    ClangSharpInvoker,
    BindingGeneratorConfig,
    BindingGeneratorResult,
)


@pytest.mark.unit
class TestClangSharpInvoker:
    """Test suite for ClangSharpInvoker class."""

    def test_find_tool_in_common_locations(self, gem_root):
        """Test that _find_tool searches common locations."""
        with patch.object(Path, "exists") as mock_exists:
            # Mock that tool is found in Code/Tools/BindingGenerator
            def exists_side_effect(self):
                return "Code/Tools/BindingGenerator" in str(self)
            
            mock_exists.side_effect = exists_side_effect
            
            invoker = ClangSharpInvoker()
            assert invoker.tool_path is not None
            assert "BindingGenerator" in str(invoker.tool_path)

    def test_find_tool_not_found(self):
        """Test behavior when tool is not found."""
        with patch.object(Path, "exists", return_value=False):
            invoker = ClangSharpInvoker()
            assert invoker.tool_path is None

    def test_build_arguments_basic(self, tmp_path):
        """Test basic command-line argument construction."""
        invoker = ClangSharpInvoker(str(tmp_path / "BindingGenerator.csproj"))
        config = BindingGeneratorConfig()
        
        args = invoker._build_arguments("/path/to/project", config, False)
        
        assert "dotnet" in args
        assert "run" in args
        assert "--project" in args
        assert "generate" in args
        assert "/path/to/project" in args

    def test_build_arguments_with_gems(self, tmp_path):
        """Test argument construction with specific gems."""
        invoker = ClangSharpInvoker(str(tmp_path / "BindingGenerator.csproj"))
        config = BindingGeneratorConfig()
        config.include_gems = ["PhysX", "Atom"]
        
        args = invoker._build_arguments("/path/to/project", config, False)
        
        assert "--gems" in args
        assert "PhysX" in args
        assert "Atom" in args

    def test_build_arguments_force_regenerate(self, tmp_path):
        """Test argument construction with force regenerate."""
        invoker = ClangSharpInvoker(str(tmp_path / "BindingGenerator.csproj"))
        config = BindingGeneratorConfig()
        
        args = invoker._build_arguments("/path/to/project", config, True)
        
        assert "--no-incremental" in args

    def test_build_arguments_require_attribute(self, tmp_path):
        """Test argument construction with require attribute option."""
        invoker = ClangSharpInvoker(str(tmp_path / "BindingGenerator.csproj"))
        config = BindingGeneratorConfig()
        config.require_export_attribute = True
        
        args = invoker._build_arguments("/path/to/project", config, False)
        
        assert "--require-attribute" in args

    def test_build_arguments_verbose(self, tmp_path):
        """Test argument construction with verbose option."""
        invoker = ClangSharpInvoker(str(tmp_path / "BindingGenerator.csproj"))
        config = BindingGeneratorConfig()
        config.verbose = True
        
        args = invoker._build_arguments("/path/to/project", config, False)
        
        assert "--verbose" in args

    def test_build_arguments_output_directory(self, tmp_path):
        """Test argument construction with custom output directory."""
        invoker = ClangSharpInvoker(str(tmp_path / "BindingGenerator.csproj"))
        config = BindingGeneratorConfig()
        config.output_directory = "/custom/output"
        
        args = invoker._build_arguments("/path/to/project", config, False)
        
        assert "--output" in args
        assert "/custom/output" in args

    @patch("subprocess.run")
    def test_generate_bindings_success(self, mock_run, tmp_path, mock_clangsharp_output):
        """Test successful binding generation."""
        mock_run.return_value = Mock(returncode=0, stdout=mock_clangsharp_output, stderr="")
        
        invoker = ClangSharpInvoker(str(tmp_path / "BindingGenerator.csproj"))
        config = BindingGeneratorConfig()
        
        result = invoker.generate_bindings("/path/to/project", config)
        
        assert result.success
        assert result.classes_generated == 15
        assert result.files_written == 18
        assert "O3DESharp" in result.processed_gems

    @patch("subprocess.run")
    def test_generate_bindings_failure(self, mock_run, tmp_path):
        """Test failed binding generation."""
        mock_run.return_value = Mock(
            returncode=1, 
            stdout="Error occurred", 
            stderr="Build failed"
        )
        
        invoker = ClangSharpInvoker(str(tmp_path / "BindingGenerator.csproj"))
        config = BindingGeneratorConfig()
        
        result = invoker.generate_bindings("/path/to/project", config)
        
        assert not result.success
        assert "failed with exit code 1" in result.error_message

    @patch("subprocess.run")
    def test_generate_bindings_timeout(self, mock_run, tmp_path):
        """Test binding generation timeout."""
        mock_run.side_effect = subprocess.TimeoutExpired("dotnet", 300)
        
        invoker = ClangSharpInvoker(str(tmp_path / "BindingGenerator.csproj"))
        config = BindingGeneratorConfig()
        
        result = invoker.generate_bindings("/path/to/project", config)
        
        assert not result.success
        assert "timed out" in result.error_message

    def test_generate_bindings_tool_not_found(self):
        """Test binding generation when tool is not found."""
        invoker = ClangSharpInvoker(None)
        config = BindingGeneratorConfig()
        
        result = invoker.generate_bindings("/path/to/project", config)
        
        assert not result.success
        assert "tool not found" in result.error_message

    def test_parse_success_output(self, tmp_path, mock_clangsharp_output):
        """Test parsing of ClangSharp tool output."""
        invoker = ClangSharpInvoker(str(tmp_path / "BindingGenerator.csproj"))
        config = BindingGeneratorConfig()
        
        result = invoker._parse_success_output(mock_clangsharp_output, config)
        
        assert result.success
        assert result.classes_generated == 15
        assert result.files_written == 18
        assert "O3DESharp" in result.processed_gems

    def test_parse_success_output_with_warnings(self, tmp_path):
        """Test parsing output with warnings."""
        output = """
Generated 10 classes
Wrote 12 files
Warning: Header 'test.h' not found
Warning: Could not resolve type 'UnknownType'
"""
        invoker = ClangSharpInvoker(str(tmp_path / "BindingGenerator.csproj"))
        config = BindingGeneratorConfig()
        
        result = invoker._parse_success_output(output, config)
        
        assert result.success
        assert len(result.warnings) == 2
        assert "not found" in result.warnings[0]

    @patch("subprocess.run")
    def test_check_tool_available_with_dotnet(self, mock_run, tmp_path):
        """Test checking tool availability when dotnet is installed."""
        mock_run.return_value = Mock(returncode=0, stdout="8.0.100", stderr="")
        
        invoker = ClangSharpInvoker(str(tmp_path / "BindingGenerator.csproj"))
        available, message = invoker.check_tool_available()
        
        assert available
        assert "8.0.100" in message

    @patch("subprocess.run")
    def test_check_tool_available_no_dotnet(self, mock_run, tmp_path):
        """Test checking tool availability when dotnet is not installed."""
        mock_run.return_value = Mock(returncode=1, stdout="", stderr="")
        
        invoker = ClangSharpInvoker(str(tmp_path / "BindingGenerator.csproj"))
        available, message = invoker.check_tool_available()
        
        assert not available
        assert "not found" in message

    def test_check_tool_available_not_found(self):
        """Test checking tool availability when tool doesn't exist."""
        invoker = ClangSharpInvoker(None)
        available, message = invoker.check_tool_available()
        
        assert not available
        assert "not found" in message


@pytest.mark.unit
class TestClangSharpInvokerEdgeCases:
    """Test edge cases and error handling for ClangSharpInvoker."""

    def test_empty_config(self, tmp_path):
        """Test with minimal/empty configuration."""
        invoker = ClangSharpInvoker(str(tmp_path / "BindingGenerator.csproj"))
        config = BindingGeneratorConfig()
        
        args = invoker._build_arguments("/project", config, False)
        
        assert "generate" in args
        assert "/project" in args

    def test_config_with_exclude_gems(self, tmp_path):
        """Test configuration with excluded gems."""
        invoker = ClangSharpInvoker(str(tmp_path / "BindingGenerator.csproj"))
        config = BindingGeneratorConfig()
        config.exclude_gems = ["TestGem", "DebugGem"]
        
        args = invoker._build_arguments("/project", config, False)
        
        assert "--exclude-gems" in args
        assert "TestGem" in args
        assert "DebugGem" in args

    def test_tool_path_as_directory(self, tmp_path):
        """Test when tool_path is a directory containing .csproj."""
        tool_dir = tmp_path / "BindingGenerator"
        tool_dir.mkdir()
        csproj = tool_dir / "BindingGenerator.csproj"
        csproj.write_text("", encoding="utf-8")
        
        invoker = ClangSharpInvoker(str(tool_dir))
        config = BindingGeneratorConfig()
        
        args = invoker._build_arguments("/project", config, False)
        
        assert "dotnet" in args
        assert str(csproj) in args

    @patch("subprocess.run")
    def test_generate_with_exception(self, mock_run, tmp_path):
        """Test handling of unexpected exceptions during generation."""
        mock_run.side_effect = Exception("Unexpected error")
        
        invoker = ClangSharpInvoker(str(tmp_path / "BindingGenerator.csproj"))
        config = BindingGeneratorConfig()
        
        result = invoker.generate_bindings("/project", config)
        
        assert not result.success
        assert "Unexpected error" in result.error_message
