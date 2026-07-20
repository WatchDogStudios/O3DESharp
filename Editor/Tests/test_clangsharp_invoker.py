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
        # autospec=True is required so the patched method still receives
        # `self` - a bare patch.object(Path, "exists") replaces the method
        # with a plain Mock, which is not a descriptor and therefore never
        # binds `self` on instance access.
        with patch.object(Path, "exists", autospec=True) as mock_exists, \
             patch.object(Path, "is_file", autospec=True, return_value=False):
            # _find_tool's Pass 1 (prebuilt DLL) checks is_file(), forced
            # False above so Pass 2 (source .csproj under
            # Code/Tools/BindingGenerator) is the one under test here.
            # Compare path parts rather than a raw substring so this
            # isn't sensitive to os.sep (\ on Windows, / elsewhere).
            def exists_side_effect(path_self):
                return "BindingGenerator" in path_self.parts

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

        # _build_arguments resolves the dotnet executable via
        # resolve_dotnet(), which returns a full path when a dotnet
        # install is discoverable on the host (as it usually is in CI/dev
        # machines). Pin it to the bare "dotnet" literal so this test's
        # assertions stay meaningful and machine-independent.
        with patch("csharp_binding_generator.resolve_dotnet", return_value="dotnet"):
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

        # The C# CLI's --gems option takes a single comma-separated value
        # (matches `--gems PhysX,Atom` in the README), not repeated flags.
        assert "--gems" in args
        assert "PhysX,Atom" in args

    def test_build_arguments_force_regenerate(self, tmp_path):
        """Test argument construction with force regenerate."""
        invoker = ClangSharpInvoker(str(tmp_path / "BindingGenerator.csproj"))
        config = BindingGeneratorConfig()
        
        args = invoker._build_arguments("/path/to/project", config, True)

        # force_regenerate maps to --force (bypass the tool's own
        # incremental cache), and --incremental is correspondingly omitted
        # rather than negated with a --no-incremental flag.
        assert "--force" in args
        assert "--incremental" not in args

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
        # ClangSharpInvoker(None) only stays tool-less if _find_tool() also
        # fails to locate anything - `tool_path or self._find_tool()` falls
        # through to a real filesystem search otherwise. This repo actually
        # ships Code/Tools/BindingGenerator/.../O3DESharp.BindingGenerator.csproj,
        # so without mocking _find_tool this test would pick up the real
        # tool and shell out to a real (and failing) dotnet invocation.
        with patch.object(ClangSharpInvoker, "_find_tool", return_value=None):
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

        # check_tool_available() only treats a .csproj tool_path as a real
        # source project if it actually exists on disk (a deliberate guard
        # against reporting a stale/deleted path as "available"), so the
        # file must be created rather than just referencing a tmp_path.
        csproj = tmp_path / "BindingGenerator.csproj"
        csproj.write_text("", encoding="utf-8")

        invoker = ClangSharpInvoker(str(csproj))
        available, message = invoker.check_tool_available()

        assert available
        assert "8.0.100" in message

    @patch("subprocess.run")
    def test_check_tool_available_no_dotnet(self, mock_run, tmp_path):
        """Test checking tool availability when dotnet is not installed."""
        mock_run.return_value = Mock(returncode=1, stdout="", stderr="")

        csproj = tmp_path / "BindingGenerator.csproj"
        csproj.write_text("", encoding="utf-8")

        invoker = ClangSharpInvoker(str(csproj))
        available, message = invoker.check_tool_available()

        assert not available
        assert "not found" in message

    def test_check_tool_available_not_found(self):
        """Test checking tool availability when tool doesn't exist."""
        # See test_generate_bindings_tool_not_found: ClangSharpInvoker(None)
        # only stays tool-less if the real _find_tool() search is also
        # mocked out, since the repo's actual BindingGenerator.csproj would
        # otherwise be found for real.
        with patch.object(ClangSharpInvoker, "_find_tool", return_value=None):
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
        """Test configuration with excluded gems.

        The C# CLI (Code/Tools/BindingGenerator/O3DESharp.BindingGenerator/
        Program.cs) has no --exclude-gems option - gem/header exclusion is
        expressed via binding_config.json's per-gem ExcludePatterns, not a
        CLI flag. `BindingGeneratorConfig.exclude_gems` is therefore
        intentionally not translated into a command-line argument here.
        """
        invoker = ClangSharpInvoker(str(tmp_path / "BindingGenerator.csproj"))
        config = BindingGeneratorConfig()
        config.exclude_gems = ["TestGem", "DebugGem"]

        args = invoker._build_arguments("/project", config, False)

        assert "--exclude-gems" not in args

    def test_tool_path_as_directory(self, tmp_path):
        """Test when tool_path is a directory containing .csproj."""
        tool_dir = tmp_path / "BindingGenerator"
        tool_dir.mkdir()
        # _build_arguments' directory-lookup branch checks for this exact
        # filename (matching the real project at
        # Code/Tools/BindingGenerator/O3DESharp.BindingGenerator/
        # O3DESharp.BindingGenerator.csproj), not an arbitrary *.csproj.
        csproj = tool_dir / "O3DESharp.BindingGenerator.csproj"
        csproj.write_text("", encoding="utf-8")
        
        invoker = ClangSharpInvoker(str(tool_dir))
        config = BindingGeneratorConfig()

        # See test_build_arguments_basic: pin resolve_dotnet() to the bare
        # "dotnet" literal so the assertion is machine-independent.
        with patch("csharp_binding_generator.resolve_dotnet", return_value="dotnet"):
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
