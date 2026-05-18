#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
"""
C# Binding Generator - ClangSharp Tool Wrapper

This module wraps the C# / libclang-based binding generator at
``Code/Tools/BindingGenerator``. It provides:

- :class:`BindingGeneratorConfig` - knobs that map onto the tool's CLI flags.
- :class:`BindingGeneratorResult` - the structured result the tool produces.
- :class:`GemBindingInfo` - per-gem output info.
- :class:`ClangSharpInvoker` - constructs the ``dotnet run -- generate ...``
  command line, executes it, and parses the tool's stdout into a
  :class:`BindingGeneratorResult`.

The previous Python BehaviorContext generator (``CSharpBindingGenerator``,
``BindingGenerationOrchestrator``, ``ReflectionData`` and supporting reflected
data classes, ``TypeMapper``, ``load_reflection_data_from_json``) has been
removed - the C# tool reads C++ headers directly and no longer needs the
reflection_data.json intermediate. If you need to resurrect any of that
flow, the pre-removal version lives in git history at the commit prior to
"Phase 12 migrate editor button..."

Canonical usage::

    invoker = ClangSharpInvoker()
    config = BindingGeneratorConfig()
    config.output_directory = "Generated/CSharp"
    config.include_gems = ["PhysX", "Atom"]
    result = invoker.generate_bindings(project_path="/path/to/project", config=config)
    if result.success:
        print(f"Generated {result.classes_generated} classes")
"""

import logging
import subprocess
from dataclasses import dataclass, field
from pathlib import Path
from typing import Callable, List, Optional, Set, Tuple


logger = logging.getLogger("O3DESharp.BindingGenerator")


# ============================================================
# Data Classes
# ============================================================


@dataclass
class BindingGeneratorConfig:
    """Configuration for C# binding generation."""

    # Output configuration
    output_directory: str = "Generated/CSharp"
    root_namespace: str = "O3DE"
    target_framework: str = "net9.0"

    # Generation options
    require_export_attribute: bool = False
    incremental_build: bool = True
    verbose: bool = False
    generate_core: bool = True
    generate_gems: bool = True
    separate_gem_directories: bool = True
    generate_per_gem_projects: bool = True

    # Gem filtering
    include_gems: List[str] = field(default_factory=list)
    exclude_gems: List[str] = field(default_factory=list)

    # Core types to always include
    core_types: Set[str] = field(
        default_factory=lambda: {
            "Vector3",
            "Vector2",
            "Vector4",
            "Quaternion",
            "Transform",
            "Color",
            "EntityId",
            "Entity",
            "Matrix",
        }
    )

    # File header
    file_header: str = ""


@dataclass
class GemBindingInfo:
    """Information about generated bindings for a single gem."""

    gem_name: str
    output_path: str
    classes_generated: int = 0
    files_generated: int = 0
    generated_files: List[str] = field(default_factory=list)


@dataclass
class BindingGeneratorResult:
    """Result of a binding generation invocation."""

    success: bool = False
    error_message: str = ""

    # Statistics
    classes_generated: int = 0
    ebuses_generated: int = 0
    files_written: int = 0
    processed_gems: List[str] = field(default_factory=list)

    # Generated files
    generated_files: List[str] = field(default_factory=list)

    # Warnings parsed out of tool output
    warnings: List[str] = field(default_factory=list)


# ============================================================
# ClangSharpInvoker
# ============================================================


class ClangSharpInvoker:
    """
    Invokes the ClangSharp-based binding generator tool.

    This class constructs command-line arguments and executes the
    C# ClangSharp tool that performs static analysis of C++ headers.
    """

    def __init__(self, tool_path: Optional[str] = None, engine_path: Optional[str] = None):
        """
        Initialize the ClangSharp invoker.

        Args:
            tool_path: Path to the BindingGenerator executable/project.
                      If None, will search in common locations.
            engine_path: Optional explicit engine root path to pass to the tool.
        """
        self.logger = logging.getLogger("O3DESharp.ClangSharpInvoker")
        self.tool_path = tool_path or self._find_tool()
        self.engine_path = engine_path

    def _find_tool(self) -> Optional[str]:
        """Find the ClangSharp binding generator tool.

        The tool ships as a .csproj that is invoked through `dotnet run`. We
        search several roots because an installed engine (cmake --install)
        often does NOT copy Code/Tools/BindingGenerator/ into the install
        tree, so the loaded gem_root (which points at the install location)
        won't have the csproj even though the developer has it under the
        source tree.

        Search order:
          1. <loaded_gem_root>/Code/Tools/BindingGenerator/...
             - normal case when running from a fully-populated source clone.
          2. <engine_source>/Gems/O3DESharp/Code/Tools/BindingGenerator/...
             - hop back from the install tree to the source clone via the
               azlmbr engroot setting. Lets the editor find the tool even
               when it's been deleted from the install location.
          3. build/bin/BindingGenerator under either gem_root or cwd.
        """
        script_dir = Path(__file__).parent
        gem_root = script_dir.parent.parent

        # === Pass 1: prebuilt DLL ==================================
        # Prefer the prebuilt DLL over the csproj. Why: invoking via
        # `dotnet run --project <csproj>` re-runs MSBuild's restore +
        # build pipeline on every invocation, which takes 30-180s of
        # silent startup time before the tool emits a single line of
        # output. Users watching the log see no progress and assume the
        # generator is hung. `dotnet <path-to-dll>` skips the rebuild
        # entirely and starts the tool in under a second, so first-line
        # output appears immediately.
        #
        # The CMake target `O3DESharp.BindingGenerator` builds the DLL
        # to <build>/bin/BindingGenerator/O3DESharp.BindingGenerator.dll
        # via ExternalProject_Add - so on any developer machine where
        # the engine has been configured + built once, the DLL is
        # there. We just check the common build-tree locations.
        dll_candidates: List[Path] = [
            gem_root / "build" / "bin" / "BindingGenerator" / "O3DESharp.BindingGenerator.dll",
            Path.cwd() / "build" / "bin" / "BindingGenerator" / "O3DESharp.BindingGenerator.dll",
        ]

        # Engine-relative build dir (when running inside the editor).
        try:
            import azlmbr.paths as _paths
            engroot = Path(_paths.engroot)
            dll_candidates.extend([
                engroot / "Workspace" / "bin" / "BindingGenerator" / "O3DESharp.BindingGenerator.dll",
                engroot / "build" / "bin" / "BindingGenerator" / "O3DESharp.BindingGenerator.dll",
            ])
        except Exception:
            pass

        for dll in dll_candidates:
            if dll.is_file():
                self.logger.info(f"Using prebuilt ClangSharp tool DLL: {dll}")
                return str(dll)

        # === Pass 2: csproj (fallback - triggers dotnet run rebuild) =
        # Only reached when no prebuilt DLL is found. Costs the
        # 30-180s rebuild penalty but at least the tool runs.
        candidates: List[Path] = []
        candidates.append(
            gem_root / "Code" / "Tools" / "BindingGenerator" /
            "O3DESharp.BindingGenerator" / "O3DESharp.BindingGenerator.csproj"
        )

        # Try the engine source clone via azlmbr.paths if available. This is
        # only resolvable inside an Editor Python context, so guard the
        # import.
        try:
            import azlmbr.paths as _paths
            engroot = Path(_paths.engroot)
            candidates.append(
                engroot / "Gems" / "O3DESharp" / "Code" / "Tools" /
                "BindingGenerator" / "O3DESharp.BindingGenerator" /
                "O3DESharp.BindingGenerator.csproj"
            )
        except Exception:
            pass  # Not running inside the editor; skip the azlmbr lookup.

        for csproj in candidates:
            if csproj.exists():
                self.logger.info(
                    f"Using ClangSharp tool source project (will trigger dotnet build): {csproj}"
                )
                return str(csproj)

        # Check in CMake binary directory (last-resort)
        possible_paths = [
            gem_root / "build" / "bin" / "BindingGenerator",
            Path.cwd() / "build" / "bin" / "BindingGenerator",
        ]

        for path in possible_paths:
            if path.exists():
                # If it's a directory, look for a .csproj inside
                if path.is_dir():
                    for csproj in path.rglob("*.csproj"):
                        self.logger.info(f"Found ClangSharp tool project: {csproj}")
                        return str(csproj)
                else:
                    self.logger.info(f"Found ClangSharp tool at: {path}")
                    return str(path)

        self.logger.warning(
            "ClangSharp binding generator tool not found in common locations. "
            f"Searched DLLs: {[str(d) for d in dll_candidates]} ; "
            f"csprojs: {[str(c) for c in candidates]}"
        )
        return None

    def generate_bindings(
        self,
        project_path: str,
        config: Optional[BindingGeneratorConfig] = None,
        force_regenerate: bool = False,
        output_callback: Optional[Callable[[str], None]] = None,
    ) -> BindingGeneratorResult:
        """
        Generate C# bindings using the ClangSharp tool.

        Args:
            project_path: Path to the O3DE project
            config: Binding generation configuration
            force_regenerate: If True, bypass incremental build cache
            output_callback: Optional callable invoked with each line of
                stdout/stderr as the tool emits it. When provided, the
                tool runs in streaming mode (subprocess.Popen + line-by-
                line read) instead of capture_output=True buffering -
                lets callers display live progress in a UI without the
                "completely silent for 60s, then a wall of text" UX.
                The callback is invoked from THIS thread, so the caller
                is responsible for thread-marshaling if the UI lives
                elsewhere (the editor UI uses a QThread + signal for
                exactly this case).

        Returns:
            BindingGeneratorResult with generation statistics
        """
        if not self.tool_path:
            return BindingGeneratorResult(
                success=False,
                error_message="ClangSharp binding generator tool not found. "
                              "Ensure Code/Tools/BindingGenerator is built."
            )

        config = config or BindingGeneratorConfig()

        # Build command-line arguments
        args = self._build_arguments(project_path, config, force_regenerate)

        # Execute the tool
        self.logger.info("Executing ClangSharp binding generator...")
        self.logger.debug(f"Command: {' '.join(args)}")

        cwd = (
            Path(self.tool_path).parent if Path(self.tool_path).is_file()
            else self.tool_path
        )

        # Streaming path: line-by-line read via Popen. Used when a
        # caller (the editor's Binding tab) wants live progress
        # rendered in its log view as the tool runs, rather than a
        # 60-second blank screen followed by a wall of buffered text.
        if output_callback is not None:
            return self._generate_streaming(args, cwd, config, output_callback)

        # Legacy capture path: buffered run, single result. Kept for
        # callers that don't care about progress UX (CLI scripts, tests,
        # places where the caller is already on a worker thread).
        try:
            result = subprocess.run(
                args,
                capture_output=True,
                text=True,
                cwd=cwd,
                timeout=1800,  # 30 minute timeout for full project generation
            )

            # Parse output
            if result.returncode == 0:
                return self._parse_success_output(result.stdout, config)
            else:
                return BindingGeneratorResult(
                    success=False,
                    error_message=f"ClangSharp tool failed with exit code {result.returncode}\n"
                                  f"STDOUT: {result.stdout}\n"
                                  f"STDERR: {result.stderr}"
                )

        except subprocess.TimeoutExpired:
            return BindingGeneratorResult(
                success=False,
                error_message="ClangSharp tool timed out after 30 minutes"
            )
        except Exception as e:
            return BindingGeneratorResult(
                success=False,
                error_message=f"Failed to execute ClangSharp tool: {str(e)}"
            )

    def _generate_streaming(
        self,
        args: List[str],
        cwd,
        config: "BindingGeneratorConfig",
        output_callback: Callable[[str], None],
    ) -> "BindingGeneratorResult":
        """
        Run the generator with line-by-line stdout streaming.

        Merges stderr into stdout so order is preserved (matches what a
        user sees on a terminal). Each line is dispatched to
        output_callback as it arrives, AND accumulated for the final
        _parse_success_output pass so we still get classes/EBuses/files
        counts on success.

        Uses Popen with bufsize=1 + universal_newlines=True so the OS
        pipe doesn't sit on lines until the buffer fills - that's the
        whole point of streaming.
        """
        accumulated_stdout: List[str] = []
        try:
            proc = subprocess.Popen(
                args,
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,  # merge into stdout for order
                text=True,
                bufsize=1,
                cwd=cwd,
            )
        except Exception as e:
            return BindingGeneratorResult(
                success=False,
                error_message=f"Failed to launch ClangSharp tool: {str(e)}"
            )

        try:
            # readline blocks per-line; we yield each to the callback
            # so the UI can render it. None signals EOF.
            assert proc.stdout is not None
            for line in proc.stdout:
                # Trim trailing newline so the callback can format as it
                # likes. The accumulator keeps the raw line so the
                # success-parser sees the same output it would have
                # gotten from capture_output=True.
                accumulated_stdout.append(line)
                try:
                    output_callback(line.rstrip("\r\n"))
                except Exception:
                    # Callback errors must not kill the generator run.
                    pass
        finally:
            # Drain + close. Bound the wait at 30 minutes total (the
            # same cap the legacy capture path enforces via timeout=).
            try:
                rc = proc.wait(timeout=1800)
            except subprocess.TimeoutExpired:
                proc.kill()
                return BindingGeneratorResult(
                    success=False,
                    error_message="ClangSharp tool timed out after 30 minutes"
                )

        full_stdout = "".join(accumulated_stdout)
        if rc == 0:
            return self._parse_success_output(full_stdout, config)
        return BindingGeneratorResult(
            success=False,
            error_message=(
                f"ClangSharp tool failed with exit code {rc}\n"
                f"OUTPUT: {full_stdout}"
            ),
        )

    def _build_arguments(
        self,
        project_path: str,
        config: BindingGeneratorConfig,
        force_regenerate: bool,
    ) -> List[str]:
        """Build command-line arguments for the ClangSharp tool."""
        tool_path = Path(self.tool_path)

        # Base command - prefer running the prebuilt DLL with `dotnet <dll>`
        # because that skips the dotnet-run restore + build pipeline entirely
        # (30-180s of silent startup → < 1s). Fall through to `dotnet run`
        # only if all we have is a csproj.
        if tool_path.suffix == ".dll":
            # Prebuilt DLL path - fastest startup, no MSBuild involvement
            args = ["dotnet", str(tool_path)]
        elif tool_path.suffix == ".csproj":
            # Csproj fallback - dotnet run with --no-build first attempt
            # would be ideal but requires a prior build that we can't
            # guarantee here. Plain dotnet run is what we have.
            args = ["dotnet", "run", "--project", str(tool_path), "--"]
        elif tool_path.is_dir():
            # Directory - look for a DLL first, then fall back to csproj
            dll = tool_path / "O3DESharp.BindingGenerator.dll"
            csproj = tool_path / "O3DESharp.BindingGenerator.csproj"
            if dll.exists():
                args = ["dotnet", str(dll)]
            elif csproj.exists():
                args = ["dotnet", "run", "--project", str(csproj), "--"]
            else:
                args = ["dotnet", "run", "--"]
        else:
            # Assume it's an executable
            args = [str(tool_path)]

        # Add generate command
        args.append("generate")

        # Add project path
        args.extend(["--project", str(project_path)])

        # Add engine path if available
        if self.engine_path:
            args.extend(["--engine", str(self.engine_path)])

        # Add configuration options
        if config.require_export_attribute:
            args.append("--require-attribute")

        if config.incremental_build and not force_regenerate:
            args.append("--incremental")

        if force_regenerate:
            args.append("--force")

        if config.verbose:
            args.append("--verbose")

        if config.include_gems:
            args.extend(["--gems", ",".join(config.include_gems)])

        if config.output_directory:
            args.extend(["--output", config.output_directory])

        return args

    def _parse_success_output(
        self,
        stdout: str,
        config: BindingGeneratorConfig
    ) -> BindingGeneratorResult:
        """Parse the ClangSharp tool output for statistics."""
        result = BindingGeneratorResult(success=True)

        # Parse output for statistics (simple regex patterns)
        import re

        # Look for patterns like "Generated 42 classes" or "Generated 42 classes in 15 files"
        classes_match = re.search(r"Generated (\d+) classes", stdout)
        if classes_match:
            result.classes_generated = int(classes_match.group(1))

        # Look for "in X files" (part of "Generated N classes in M files") or "Wrote 15 files"
        files_match = re.search(r"in (\d+) files", stdout)
        if not files_match:
            files_match = re.search(r"Wrote (\d+) files", stdout)
        if files_match:
            result.files_written = int(files_match.group(1))

        # Look for "Processed gems: Gem1, Gem2"
        gems_match = re.search(r"Processed gems?: (.+)", stdout)
        if gems_match:
            gems_str = gems_match.group(1).strip()
            result.processed_gems = [g.strip() for g in gems_str.split(",")]

        # Look for warnings
        for line in stdout.split("\n"):
            if "warning:" in line.lower() or "warn:" in line.lower():
                result.warnings.append(line.strip())

        self.logger.info(f"Generated {result.classes_generated} classes in {result.files_written} files")
        if result.processed_gems:
            self.logger.info(f"Processed gems: {', '.join(result.processed_gems)}")

        return result

    def check_tool_available(self) -> Tuple[bool, str]:
        """
        Check if the ClangSharp tool is available.

        Returns:
            Tuple of (available, message)
        """
        if not self.tool_path:
            return False, "ClangSharp binding generator tool not found"

        tool_path = Path(self.tool_path)

        # Check if it's a source project (.csproj file or directory containing one)
        if tool_path.suffix == ".csproj" and tool_path.exists():
            csproj_path = tool_path
        elif tool_path.is_dir():
            # Look for any .csproj in the directory tree
            csproj_path = None
            for candidate_name in [
                "O3DESharp.BindingGenerator.csproj",
                "BindingGenerator.csproj",
            ]:
                found = list(tool_path.rglob(candidate_name))
                if found:
                    csproj_path = found[0]
                    break
        else:
            csproj_path = None

        if csproj_path and csproj_path.exists():
            # Check if dotnet is available
            try:
                result = subprocess.run(
                    ["dotnet", "--version"],
                    capture_output=True,
                    text=True,
                    timeout=5
                )
                if result.returncode == 0:
                    dotnet_version = result.stdout.strip()
                    return True, f"ClangSharp tool source available, dotnet version: {dotnet_version}"
                else:
                    return False, "dotnet CLI not found. Install .NET 9.0 SDK or later."
            except Exception as e:
                return False, f"dotnet CLI check failed: {str(e)}"

        # Check if it's a built executable
        if tool_path.is_file() and tool_path.exists():
            return True, f"ClangSharp tool executable found at {tool_path}"

        return False, "ClangSharp tool path exists but is not executable or project"
