#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
"""
O3DESharp C# Binding Generation Script - ClangSharp Orchestrator

This script orchestrates the ClangSharp-based binding generator tool.
It replaces the deprecated Python-based BehaviorContext generator.

The binding generation workflow:
1. Invoke ClangSharp tool to parse C++ headers
2. Generate C# wrapper classes organized by gem
3. Generate C++ Coral registration code
4. Generate solution/project files

Usage:
    # Generate bindings for a project
    python generate_bindings.py --project /path/to/project

    # Generate bindings for specific gems
    python generate_bindings.py --project /path/to/project --gems PhysX Atom

    # Force regeneration (bypass incremental build)
    python generate_bindings.py --project /path/to/project --force-regenerate

    # Use from O3DE Editor Python console
    import generate_bindings
    generate_bindings.generate_bindings_for_project("/path/to/project")
"""

import argparse
import logging
import os
import subprocess
import sys
from collections import defaultdict
from pathlib import Path
from typing import Dict, List, Optional

# Add this directory to the path for local imports
script_dir = Path(__file__).parent
if str(script_dir) not in sys.path:
    sys.path.insert(0, str(script_dir))

# Auto-detect engine root from this script's location:
#   <engine>/Gems/O3DESharp/Editor/Scripts/generate_bindings.py
_SCRIPT_ENGINE_ROOT: Optional[Path] = None
_candidate = script_dir.parent.parent.parent.parent  # 4 levels up
if (_candidate / "engine.json").exists():
    _SCRIPT_ENGINE_ROOT = _candidate

from csharp_binding_generator import (
    BindingGeneratorConfig,
    BindingGeneratorResult,
    ClangSharpInvoker,
    CSharpBindingGenerator,
    ReflectionData,
    load_reflection_data_from_json,
)
from gem_dependency_resolver import GemDependencyResolver, GemResolutionResult

# Set up logging
logger = logging.getLogger("O3DESharp.GenerateBindings")


# ============================================================
# BindingGenerationOrchestrator Class
# ============================================================


class BindingGenerationOrchestrator:
    """
    High-level orchestrator for C# binding generation.
    
    Provides a convenient interface for configuring and running binding generation
    from both the CLI and the Editor UI.
    """
    
    def __init__(self, engine_path: Optional[Path] = None):
        self.config = BindingGeneratorConfig()
        self.reflection_data: Optional[ReflectionData] = None
        self.gem_resolver: Optional[GemDependencyResolver] = None
        self._generated_files: dict = {}
        self._generator: Optional[CSharpBindingGenerator] = None
        self._project_path: Optional[str] = None
        self._engine_path: Optional[Path] = engine_path or _SCRIPT_ENGINE_ROOT
    
    def configure(
        self,
        output_directory: str = None,
        root_namespace: str = "O3DE",
        generate_core: bool = True,
        generate_gems: bool = True,
        separate_gem_directories: bool = True,
        generate_per_gem_projects: bool = True,
        include_gems: List[str] = None,
        exclude_gems: List[str] = None,
        require_export_attribute: bool = False,
        target_framework: str = "net9.0",
        incremental_build: bool = True,
    ):
        """
        Configure the binding generator.
        
        Args:
            output_directory: Directory to write generated files
            root_namespace: Root namespace for generated code (default: O3DE)
            generate_core: Whether to generate core O3DE bindings
            generate_gems: Whether to generate gem bindings
            separate_gem_directories: Create separate directories per gem
            generate_per_gem_projects: Generate .csproj per gem
            include_gems: Only include these gems (None = all)
            exclude_gems: Exclude these gems
            require_export_attribute: Only export marked declarations
            target_framework: Target .NET framework
            incremental_build: Use incremental build caching
        """
        if output_directory:
            self.config.output_directory = output_directory
        self.config.root_namespace = root_namespace
        self.config.generate_core = generate_core
        self.config.generate_gems = generate_gems
        self.config.separate_gem_directories = separate_gem_directories
        self.config.generate_per_gem_projects = generate_per_gem_projects
        self.config.include_gems = include_gems or []
        self.config.exclude_gems = exclude_gems or []
        self.config.require_export_attribute = require_export_attribute
        self.config.target_framework = target_framework
        self.config.incremental_build = incremental_build
    
    def load_reflection_data(self, json_path: str) -> bool:
        """
        Load reflection data from a JSON file.
        
        Args:
            json_path: Path to reflection_data.json
            
        Returns:
            True if loaded successfully, False otherwise
        """
        try:
            self.reflection_data = load_reflection_data_from_json(json_path)
            if self.reflection_data:
                logger.info(
                    f"Loaded reflection data: {len(self.reflection_data.classes)} classes, "
                    f"{len(self.reflection_data.ebuses)} EBuses"
                )
                return True
            else:
                logger.error("Failed to parse reflection data")
                return False
        except Exception as e:
            logger.error(f"Failed to load reflection data: {e}")
            return False
    
    def discover_gems_from_project(self, project_path: str) -> GemResolutionResult:
        """
        Discover gems in the project.
        
        Args:
            project_path: Path to the O3DE project root
            
        Returns:
            GemResolutionResult with discovery results
        """
        self._project_path = project_path
        self.gem_resolver = GemDependencyResolver(engine_path_hint=self._engine_path)
        result = self.gem_resolver.discover_gems_from_project(project_path)
        
        if result.success:
            logger.info(f"Discovered {len(result.active_gem_names)} active gems")
        else:
            logger.error(f"Gem discovery failed: {result.error_message}")
        
        return result
    
    def generate(self) -> BindingGeneratorResult:
        """
        Generate C# bindings based on loaded reflection data.
        
        Returns:
            BindingGeneratorResult with generation statistics
        """
        if not self.reflection_data:
            return BindingGeneratorResult(
                success=False,
                error_message="No reflection data loaded. Call load_reflection_data() first."
            )
        
        try:
            # Create generator
            self._generator = CSharpBindingGenerator(self.config)
            
            # Generate from reflection data
            self._generated_files = self._generator.generate_from_reflection_data(
                self.reflection_data,
                gem_resolver=self.gem_resolver
            )
            
            # Count results
            classes_count = len(self.reflection_data.classes)
            ebuses_count = len(self.reflection_data.ebuses)
            files_count = len(self._generated_files)
            
            # Get processed gems
            processed_gems = []
            if self.gem_resolver:
                processed_gems = list(self.gem_resolver.get_active_gem_names())
            
            return BindingGeneratorResult(
                success=True,
                classes_generated=classes_count,
                ebuses_generated=ebuses_count,
                files_written=files_count,
                processed_gems=processed_gems
            )
            
        except Exception as e:
            logger.exception("Error during binding generation")
            return BindingGeneratorResult(
                success=False,
                error_message=str(e)
            )
    
    def write_files(self) -> int:
        """
        Write generated files to disk.
        
        Returns:
            Number of files written
        """
        if not self._generated_files:
            logger.warning("No files to write")
            return 0
        
        output_dir = Path(self.config.output_directory)
        output_dir.mkdir(parents=True, exist_ok=True)
        
        files_written = 0
        for rel_path, content in self._generated_files.items():
            file_path = output_dir / rel_path
            file_path.parent.mkdir(parents=True, exist_ok=True)
            
            try:
                file_path.write_text(content, encoding='utf-8')
                files_written += 1
                logger.debug(f"Wrote: {file_path}")
            except Exception as e:
                logger.error(f"Failed to write {file_path}: {e}")
        
        logger.info(f"Wrote {files_written} files to {output_dir}")
        return files_written

    def generate_per_gem_projects(self) -> Dict[str, Path]:
        """
        Generate a .csproj file for each gem's generated bindings.

        Returns:
            Dictionary mapping gem name to its .csproj path.
        """
        if not self._generated_files:
            logger.warning("No generated files — skipping .csproj generation")
            return {}

        output_dir = Path(self.config.output_directory)

        # Group generated files by their top-level gem directory
        gem_files: Dict[str, List[str]] = defaultdict(list)
        for rel_path in self._generated_files:
            parts = Path(rel_path).parts
            gem_name = parts[0] if parts else "Core"
            gem_files[gem_name].append(rel_path)

        csproj_paths: Dict[str, Path] = {}

        for gem_name, files in gem_files.items():
            gem_dir = output_dir / gem_name
            gem_dir.mkdir(parents=True, exist_ok=True)

            assembly_name = f"O3DE.Bindings.{gem_name}"
            namespace = f"{self.config.root_namespace}.{gem_name}"
            framework = getattr(self.config, "target_framework", "net9.0")

            # Resolve reference to O3DE.Core managed runtime
            o3de_core_hint = ""
            o3de_core_dll = self._find_o3de_core_dll()
            if o3de_core_dll:
                o3de_core_hint = (
                    "  <ItemGroup>\n"
                    f'    <Reference Include="O3DE.Core">\n'
                    f'      <HintPath>{o3de_core_dll}</HintPath>\n'
                    "    </Reference>\n"
                    "  </ItemGroup>\n"
                )

            csproj_content = (
                '<Project Sdk="Microsoft.NET.Sdk">\n'
                "\n"
                "  <PropertyGroup>\n"
                f"    <TargetFramework>{framework}</TargetFramework>\n"
                f"    <AssemblyName>{assembly_name}</AssemblyName>\n"
                f"    <RootNamespace>{namespace}</RootNamespace>\n"
                "    <Nullable>enable</Nullable>\n"
                "    <ImplicitUsings>enable</ImplicitUsings>\n"
                "    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>\n"
                "    <EnableDefaultCompileItems>true</EnableDefaultCompileItems>\n"
                "    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>\n"
                f"    <!-- Auto-generated by O3DESharp binding generator -->\n"
                "  </PropertyGroup>\n"
                "\n"
                f"{o3de_core_hint}"
                "\n"
                "</Project>\n"
            )

            csproj_path = gem_dir / f"{assembly_name}.csproj"
            try:
                csproj_path.write_text(csproj_content, encoding="utf-8")
                csproj_paths[gem_name] = csproj_path
                logger.info(f"Generated project: {csproj_path}")
            except Exception as e:
                logger.error(f"Failed to write {csproj_path}: {e}")

        return csproj_paths

    def build_binding_dlls(self, csproj_paths: Dict[str, Path] = None) -> Dict[str, bool]:
        """
        Build the generated .csproj files into DLLs using ``dotnet build``.

        Args:
            csproj_paths: Dict of gem name → .csproj path.
                          If None, calls generate_per_gem_projects() first.

        Returns:
            Dictionary mapping gem name to build success (True/False).
        """
        if csproj_paths is None:
            csproj_paths = self.generate_per_gem_projects()

        if not csproj_paths:
            logger.warning("No .csproj files to build")
            return {}

        results: Dict[str, bool] = {}
        for gem_name, csproj_path in csproj_paths.items():
            logger.info(f"Building {gem_name} from {csproj_path} ...")
            try:
                proc = subprocess.run(
                    ["dotnet", "build", str(csproj_path), "-c", "Release", "--nologo"],
                    capture_output=True,
                    text=True,
                    timeout=120,
                )
                if proc.returncode == 0:
                    logger.info(f"  {gem_name}: BUILD SUCCEEDED")
                    results[gem_name] = True
                else:
                    logger.error(f"  {gem_name}: BUILD FAILED\n{proc.stdout}\n{proc.stderr}")
                    results[gem_name] = False
            except FileNotFoundError:
                logger.error(
                    "dotnet CLI not found — cannot compile bindings. "
                    "Install the .NET SDK from https://dotnet.microsoft.com/download"
                )
                results[gem_name] = False
                break
            except subprocess.TimeoutExpired:
                logger.error(f"  {gem_name}: build timed out after 120 s")
                results[gem_name] = False

        succeeded = sum(1 for v in results.values() if v)
        logger.info(f"Build complete: {succeeded}/{len(results)} gems succeeded")
        return results

    def _find_o3de_core_dll(self) -> Optional[str]:
        """Try to locate the O3DE.Core.dll managed runtime assembly."""
        # Check common locations relative to the gem
        search_roots: List[Path] = []

        gem_root = Path(__file__).resolve().parent.parent.parent  # .../O3DESharp
        search_roots.append(gem_root / "bin" / "O3DE.Core")
        search_roots.append(gem_root / "bin" / "Coral")

        if self._project_path:
            proj = Path(self._project_path)
            for cfg in ("profile", "debug", "release"):
                search_roots.append(proj / "bin" / cfg / "Gems" / "O3DESharp")
                search_roots.append(proj / "Bin" / cfg)

        for root in search_roots:
            candidate = root / "O3DE.Core.dll"
            if candidate.exists():
                return str(candidate)
            # Also try rglob one level
            if root.exists():
                for hit in root.glob("**/O3DE.Core.dll"):
                    return str(hit)

        return None
    
    def get_generated_files(self) -> dict:
        """Get the dictionary of generated file paths to content."""
        return self._generated_files


# ============================================================
# Convenience Functions for __init__.py exports
# ============================================================


def generate_all_bindings(
    project_path: str,
    output_directory: str = None,
    force_regenerate: bool = False,
    engine_path: Optional[str] = None,
) -> BindingGeneratorResult:
    """
    Generate all C# bindings for a project.

    Args:
        project_path: Path to the O3DE project root
        output_directory: Optional output directory
        force_regenerate: Bypass incremental build cache
        engine_path: Optional explicit engine root path

    Returns:
        BindingGeneratorResult with generation statistics
    """
    config = BindingGeneratorConfig()
    if output_directory:
        config.output_directory = output_directory
    else:
        config.output_directory = str(Path(project_path) / "Generated" / "CSharp")
    config.incremental_build = not force_regenerate

    return generate_bindings_for_project(project_path, config, force_regenerate,
                                         engine_path=engine_path)


def generate_gem_bindings(
    project_path: str,
    gem_names: List[str],
    output_directory: str = None,
    force_regenerate: bool = False
) -> BindingGeneratorResult:
    """
    Generate C# bindings for specific gems.
    
    Args:
        project_path: Path to the O3DE project root
        gem_names: List of gem names to generate bindings for
        output_directory: Optional output directory
        force_regenerate: Bypass incremental build cache
        
    Returns:
        BindingGeneratorResult with generation statistics
    """
    config = BindingGeneratorConfig()
    config.include_gems = gem_names
    if output_directory:
        config.output_directory = output_directory
    else:
        config.output_directory = str(Path(project_path) / "Generated" / "CSharp")
    config.incremental_build = not force_regenerate
    
    return generate_bindings_for_project(project_path, config, force_regenerate)


def generate_core_bindings(
    project_path: str,
    output_directory: str = None,
    force_regenerate: bool = False
) -> BindingGeneratorResult:
    """
    Generate core O3DE bindings (no gems).
    
    Args:
        project_path: Path to the O3DE project root
        output_directory: Optional output directory
        force_regenerate: Bypass incremental build cache
        
    Returns:
        BindingGeneratorResult with generation statistics
    """
    config = BindingGeneratorConfig()
    config.generate_core = True
    config.generate_gems = False
    if output_directory:
        config.output_directory = output_directory
    else:
        config.output_directory = str(Path(project_path) / "Generated" / "CSharp")
    config.incremental_build = not force_regenerate
    
    return generate_bindings_for_project(project_path, config, force_regenerate)


# ============================================================
# Main Orchestration Functions
# ============================================================


def generate_bindings_for_project(
    project_path: str,
    config: Optional[BindingGeneratorConfig] = None,
    force_regenerate: bool = False,
    engine_path: Optional[str] = None,
) -> BindingGeneratorResult:
    """
    Generate C# bindings for an O3DE project using the ClangSharp tool.

    This is the main function to call from the O3DE Editor or scripts.

    Args:
        project_path: Path to the O3DE project root
        config: Binding generation configuration (uses defaults if None)
        force_regenerate: If True, bypass incremental build cache
        engine_path: Optional explicit engine root path

    Returns:
        BindingGeneratorResult with generation statistics
    """
    logger.info(f"Generating C# bindings for project: {project_path}")

    # Create config with defaults if not provided
    if config is None:
        config = BindingGeneratorConfig()
        config.output_directory = str(Path(project_path) / "Generated" / "CSharp")
        config.incremental_build = not force_regenerate

    # Resolve engine path: explicit > auto-detected from script location
    resolved_engine = engine_path or (str(_SCRIPT_ENGINE_ROOT) if _SCRIPT_ENGINE_ROOT else None)

    # Create ClangSharp invoker with engine path
    invoker = ClangSharpInvoker(engine_path=resolved_engine)
    
    # Check if tool is available
    available, message = invoker.check_tool_available()
    if not available:
        logger.error(f"ClangSharp tool not available: {message}")
        return BindingGeneratorResult(
            success=False,
            error_message=f"ClangSharp tool not available: {message}"
        )
    
    logger.info(f"Using ClangSharp tool: {message}")
    
    # Generate bindings
    result = invoker.generate_bindings(
        project_path=project_path,
        config=config,
        force_regenerate=force_regenerate
    )
    
    if result.success:
        logger.info(
            f"Successfully generated bindings: "
            f"{result.classes_generated} classes, {result.files_written} files"
        )
        if result.processed_gems:
            logger.info(f"Processed gems: {', '.join(result.processed_gems)}")
    else:
        logger.error(f"Binding generation failed: {result.error_message}")
    
    # Print warnings if any
    for warning in result.warnings:
        logger.warning(warning)
    
    return result


def generate_bindings_for_gems(
    project_path: str,
    gem_names: List[str],
    force_regenerate: bool = False,
) -> BindingGeneratorResult:
    """
    Generate C# bindings for specific gems only.
    
    Args:
        project_path: Path to the O3DE project root
        gem_names: List of gem names to generate bindings for
        force_regenerate: If True, bypass incremental build cache
        
    Returns:
        BindingGeneratorResult with generation statistics
    """
    logger.info(f"Generating C# bindings for gems: {', '.join(gem_names)}")
    
    # Create config for specific gems
    config = BindingGeneratorConfig()
    config.output_directory = str(Path(project_path) / "Generated" / "CSharp")
    config.include_gems = gem_names
    config.incremental_build = not force_regenerate
    
    return generate_bindings_for_project(
        project_path=project_path,
        config=config,
        force_regenerate=force_regenerate
    )


def check_bindings_need_regeneration(project_path: str) -> bool:
    """
    Check if bindings need to be regenerated based on file timestamps.
    
    This performs a simple check comparing the gem headers against generated files.
    The ClangSharp tool has its own internal hash-based caching that is more accurate.
    
    Args:
        project_path: Path to the O3DE project root
        
    Returns:
        True if bindings need regeneration, False otherwise
    """
    project_path = Path(project_path)
    generated_dir = project_path / "Generated" / "CSharp"
    
    # If generated directory doesn't exist, need to generate
    if not generated_dir.exists():
        logger.info("Generated bindings directory not found - regeneration needed")
        return True
    
    # Check for binding_config.json changes
    config_file = project_path / "Gems" / "O3DESharp" / "binding_config.json"
    if config_file.exists():
        config_mtime = config_file.stat().st_mtime
        
        # Check if any generated file is older than config
        for gen_file in generated_dir.rglob("*.cs"):
            if gen_file.stat().st_mtime < config_mtime:
                logger.info("Binding configuration changed - regeneration needed")
                return True
    
    # For more accurate checking, the ClangSharp tool uses file hash caching
    # So we'll default to letting the tool decide via incremental build
    logger.info("Using ClangSharp incremental build for change detection")
    return False


def list_available_gems(project_path: str, engine_path: Optional[Path] = None) -> List[str]:
    """
    List all available gems in a project.
    
    Args:
        project_path: Path to the project root
        engine_path: Optional explicit engine root
        
    Returns:
        List of gem names
    """
    resolver = GemDependencyResolver(engine_path_hint=engine_path or _SCRIPT_ENGINE_ROOT)
    result = resolver.discover_gems_from_project(project_path)
    if result.success:
        return sorted(result.active_gem_names)
    else:
        logger.error(f"Failed to discover gems: {result.error_message}")
        return []


def get_gem_dependencies(project_path: str, gem_name: str, engine_path: Optional[Path] = None) -> List[str]:
    """
    Get dependencies for a specific gem.
    
    Args:
        project_path: Path to the project root
        gem_name: Name of the gem
        engine_path: Optional explicit engine root
        
    Returns:
        List of dependency gem names
    """
    resolver = GemDependencyResolver(engine_path_hint=engine_path or _SCRIPT_ENGINE_ROOT)
    result = resolver.discover_gems_from_project(project_path)
    if result.success:
        return resolver.get_gem_dependencies(gem_name, include_transitive=True)
    else:
        logger.error(f"Failed to discover gems: {result.error_message}")
        return []


def find_binding_generator_tool() -> Optional[Path]:
    """
    Find the ClangSharp binding generator tool.
    
    Searches in common locations relative to this script and the CMake build directory.
    
    Returns:
        Path to the tool if found, None otherwise
    """
    script_dir = Path(__file__).parent
    gem_root = script_dir.parent.parent
    
    # Search locations
    possible_paths = [
        gem_root / "build" / "bin" / "BindingGenerator",
        gem_root / "Code" / "Tools" / "BindingGenerator",
        Path.cwd() / "build" / "bin" / "BindingGenerator",
        Path(os.environ.get("O3DESHARP_BINDING_GENERATOR_PATH", "")),
    ]
    
    for path in possible_paths:
        if path and path.exists():
            logger.info(f"Found ClangSharp tool at: {path}")
            return path
    
    # Check source project
    source_project = gem_root / "Code" / "Tools" / "BindingGenerator" / "BindingGenerator.csproj"
    if source_project.exists():
        logger.info(f"Using ClangSharp tool source project: {source_project}")
        return source_project
    
    logger.warning("ClangSharp binding generator tool not found")
    return None


# ============================================================
# CLI Interface
# ============================================================


def create_argument_parser() -> argparse.ArgumentParser:
    """Create the command-line argument parser."""
    parser = argparse.ArgumentParser(
        description="Generate C# bindings from O3DE C++ headers using ClangSharp",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
Examples:
  # Generate all bindings for a project
  python generate_bindings.py --project /path/to/project

  # Generate bindings for specific gems
  python generate_bindings.py --project /path/to/project --gems PhysX Atom

  # Force regeneration (bypass incremental build)
  python generate_bindings.py --project /path/to/project --force-regenerate

  # Specify the engine root explicitly
  python generate_bindings.py --project /path/to/project --engine /path/to/o3de

  # Require export attribute (only export marked declarations)
  python generate_bindings.py --project /path/to/project --require-attribute

  # List available gems in a project
  python generate_bindings.py --project /path/to/project --list-gems
        """,
    )

    parser.add_argument(
        "--project",
        "-p",
        required=True,
        help="Path to O3DE project root",
    )
    parser.add_argument(
        "--engine",
        "-e",
        help=(
            "Path to O3DE engine root. Auto-detected from this script's location, "
            "~/.o3de/o3de_manifest.json, or O3DE_ENGINE_PATH env var if not specified."
        ),
    )
    parser.add_argument(
        "--output",
        "-o",
        help="Output directory for generated files (default: <project>/Generated/CSharp)",
    )
    parser.add_argument(
        "--namespace",
        "-n",
        default="O3DE",
        help="Root C# namespace (default: O3DE)",
    )
    parser.add_argument(
        "--gems",
        "-g",
        nargs="+",
        help="Generate bindings for specific gems only",
    )
    parser.add_argument(
        "--exclude-gems",
        nargs="+",
        help="Exclude specific gems from generation",
    )
    parser.add_argument(
        "--require-attribute",
        action="store_true",
        help="Only export declarations marked with O3DE_EXPORT_CSHARP attribute",
    )
    parser.add_argument(
        "--force-regenerate",
        "-f",
        action="store_true",
        help="Force regeneration (bypass incremental build cache)",
    )
    parser.add_argument(
        "--no-incremental",
        action="store_true",
        help="Disable incremental build (same as --force-regenerate)",
    )
    parser.add_argument(
        "--target-framework",
        default="net9.0",
        help="Target .NET framework (default: net9.0)",
    )
    parser.add_argument(
        "--list-gems",
        action="store_true",
        help="List available gems and exit",
    )
    parser.add_argument(
        "--show-dependencies",
        metavar="GEM",
        help="Show dependencies for a gem and exit",
    )
    parser.add_argument(
        "--check-tool",
        action="store_true",
        help="Check if ClangSharp tool is available and exit",
    )
    parser.add_argument(
        "--build-dlls",
        action="store_true",
        help="After generation, compile per-gem .csproj files into DLLs via 'dotnet build'",
    )
    parser.add_argument(
        "--verbose",
        "-v",
        action="store_true",
        help="Enable verbose output",
    )

    return parser


def main() -> int:
    """Main entry point for command-line usage."""
    parser = create_argument_parser()
    args = parser.parse_args()

    # Configure logging
    log_level = logging.DEBUG if args.verbose else logging.INFO
    logging.basicConfig(
        level=log_level,
        format="%(asctime)s [%(levelname)s] %(name)s: %(message)s",
        datefmt="%H:%M:%S",
    )

    # Handle info-only commands
    if args.check_tool:
        invoker = ClangSharpInvoker()
        available, message = invoker.check_tool_available()
        if available:
            print(f"✓ ClangSharp tool is available: {message}")
            return 0
        else:
            print(f"✗ ClangSharp tool not available: {message}")
            return 1

    if args.list_gems:
        engine = Path(args.engine) if args.engine else _SCRIPT_ENGINE_ROOT
        gems = list_available_gems(args.project, engine_path=engine)
        if not gems:
            logger.error("No gems found or gem discovery failed")
            return 1
        
        print(f"\nDiscovered {len(gems)} gems in project:")
        for gem_name in gems:
            print(f"  {gem_name}")
        return 0

    if args.show_dependencies:
        engine = Path(args.engine) if args.engine else _SCRIPT_ENGINE_ROOT
        deps = get_gem_dependencies(args.project, args.show_dependencies, engine_path=engine)
        if not deps:
            print(f"No dependencies found for {args.show_dependencies}")
            return 0
        
        print(f"\nDependencies for {args.show_dependencies}:")
        for dep in deps:
            print(f"  {dep}")
        return 0

    # Validate project path
    project_path = Path(args.project)
    if not project_path.exists():
        logger.error(f"Project path does not exist: {project_path}")
        return 1

    # Create configuration
    config = BindingGeneratorConfig()
    config.root_namespace = args.namespace
    config.target_framework = args.target_framework
    config.require_export_attribute = args.require_attribute
    config.incremental_build = not (args.force_regenerate or args.no_incremental)
    config.verbose = args.verbose
    
    if args.output:
        config.output_directory = args.output
    else:
        config.output_directory = str(project_path / "Generated" / "CSharp")
    
    if args.gems:
        config.include_gems = args.gems
    
    if args.exclude_gems:
        config.exclude_gems = args.exclude_gems

    # Generate bindings
    engine = args.engine or (str(_SCRIPT_ENGINE_ROOT) if _SCRIPT_ENGINE_ROOT else None)
    result = generate_bindings_for_project(
        project_path=str(project_path),
        config=config,
        force_regenerate=args.force_regenerate or args.no_incremental,
        engine_path=engine,
    )

    if not result.success:
        logger.error(f"Generation failed: {result.error_message}")
        return 1

    # Build DLLs if requested
    build_results = {}
    if args.build_dlls:
        gen_dir = Path(config.output_directory)
        cs_files = list(gen_dir.rglob("*.g.cs")) if gen_dir.exists() else []
        if result.files_written == 0 and result.classes_generated == 0 and cs_files:
            logger.warning(
                f"Generator produced 0 new files but {len(cs_files)} old .g.cs files "
                f"exist on disk in {gen_dir}. Rebuilding them with the current codebase. "
                f"Use --force-regenerate to re-generate from scratch."
            )
        if not cs_files:
            logger.warning("No .g.cs files found — skipping DLL build.")
        else:
            logger.info("Building generated bindings into DLLs ...")
            engine = Path(args.engine) if args.engine else _SCRIPT_ENGINE_ROOT
            orchestrator = BindingGenerationOrchestrator(engine_path=engine)
            orchestrator.config = config
            orchestrator._project_path = str(project_path)
            for cs_file in cs_files:
                rel = cs_file.relative_to(gen_dir).as_posix()
                orchestrator._generated_files[rel] = cs_file.read_text(encoding="utf-8")
            csproj_paths = orchestrator.generate_per_gem_projects()
            build_results = orchestrator.build_binding_dlls(csproj_paths)

    # Print summary
    print("\n" + "=" * 70)
    print("C# Binding Generation Complete")
    print("=" * 70)
    print(f"  Classes generated:  {result.classes_generated}")
    print(f"  Files written:      {result.files_written}")
    print(f"  Output directory:   {config.output_directory}")

    if result.processed_gems:
        print(f"  Gems processed:     {len(result.processed_gems)}")
        for gem in result.processed_gems[:10]:
            print(f"    - {gem}")
        if len(result.processed_gems) > 10:
            print(f"    ... and {len(result.processed_gems) - 10} more")

    if build_results:
        succeeded = sum(1 for v in build_results.values() if v)
        failed = len(build_results) - succeeded
        print(f"\n  DLL builds:  {succeeded} succeeded, {failed} failed")
        for gem_name, ok in build_results.items():
            status = "OK" if ok else "FAILED"
            print(f"    [{status}] {gem_name}")

    if result.warnings:
        print(f"\n  Warnings ({len(result.warnings)}):")
        for warning in result.warnings[:5]:
            print(f"  {warning}")
        if len(result.warnings) > 5:
            print(f"  ... and {len(result.warnings) - 5} more warnings")

    print("=" * 70)

    return 0


if __name__ == "__main__":
    sys.exit(main())
