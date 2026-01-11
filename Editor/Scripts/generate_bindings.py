#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
"""
O3DESharp C# Binding Generation Script

This is the main entry point for generating C# bindings from O3DE's BehaviorContext.
It can be run from the command line or invoked from the O3DE Editor.

The binding generation workflow:
1. Export reflection data from C++ BehaviorContext to JSON (via Editor or standalone tool)
2. Discover gems from the project
3. Generate C# wrapper classes organized by gem
4. Generate solution/project files

Usage:
    # Generate bindings for a project
    python generate_bindings.py --project /path/to/project --reflection-data reflection.json

    # Generate bindings for specific gems
    python generate_bindings.py --project /path/to/project --reflection-data reflection.json --gems PhysX Atom

    # Generate core bindings only
    python generate_bindings.py --reflection-data reflection.json --core-only

    # Use from O3DE Editor Python console
    import generate_bindings
    generate_bindings.generate_all_bindings("/path/to/output")
"""

import argparse
import json
import logging
import os
import sys
from pathlib import Path
from typing import List, Optional

# Add this directory to the path for local imports
script_dir = Path(__file__).parent
if str(script_dir) not in sys.path:
    sys.path.insert(0, str(script_dir))

from csharp_binding_generator import (
    BindingGeneratorConfig,
    BindingGeneratorResult,
    CSharpBindingGenerator,
    ReflectionData,
    load_reflection_data_from_json,
)
from gem_dependency_resolver import GemDependencyResolver, GemResolutionResult

# Set up logging
logger = logging.getLogger("O3DESharp.GenerateBindings")


class BindingGenerationOrchestrator:
    """
    Orchestrates the complete binding generation workflow.

    This class coordinates between the reflection data, gem resolver,
    and C# generator to produce organized bindings.
    """

    def __init__(self):
        self.config = BindingGeneratorConfig()
        self.gem_resolver = GemDependencyResolver()
        self.generator = CSharpBindingGenerator()
        self.reflection_data: Optional[ReflectionData] = None

    def configure(
        self,
        output_directory: str = "Generated/CSharp",
        root_namespace: str = "O3DE.Generated",
        generate_core: bool = True,
        generate_gems: bool = True,
        separate_gem_directories: bool = True,
        generate_per_gem_projects: bool = False,
        include_gems: Optional[List[str]] = None,
        exclude_gems: Optional[List[str]] = None,
        target_framework: str = "net8.0",
    ) -> None:
        """
        Configure the binding generation.

        Args:
            output_directory: Root output directory for generated files
            root_namespace: Root C# namespace for generated code
            generate_core: Whether to generate core O3DE bindings
            generate_gems: Whether to generate per-gem bindings
            separate_gem_directories: Create separate directories per gem
            generate_per_gem_projects: Generate separate .csproj per gem
            include_gems: List of gems to include (None = all active)
            exclude_gems: List of gems to exclude
            target_framework: Target .NET framework version
        """
        self.config.output_directory = output_directory
        self.config.root_namespace = root_namespace
        self.config.generate_core_bindings = generate_core
        self.config.generate_gem_bindings = generate_gems
        self.config.separate_gem_directories = separate_gem_directories
        self.config.generate_per_gem_projects = generate_per_gem_projects
        self.config.include_gems = include_gems or []
        self.config.exclude_gems = exclude_gems or []
        self.config.target_framework = target_framework

    def load_reflection_data(self, json_path: str) -> bool:
        """
        Load reflection data from a JSON file.

        Args:
            json_path: Path to the reflection data JSON file

        Returns:
            True if successful
        """
        try:
            self.reflection_data = load_reflection_data_from_json(json_path)
            logger.info(
                f"Loaded reflection data: {len(self.reflection_data.classes)} classes, "
                f"{len(self.reflection_data.ebuses)} EBuses"
            )
            return True
        except Exception as e:
            logger.error(f"Failed to load reflection data: {e}")
            return False

    def discover_gems_from_project(self, project_path: str) -> GemResolutionResult:
        """
        Discover gems from an O3DE project.

        Args:
            project_path: Path to the project root

        Returns:
            GemResolutionResult with discovered gems
        """
        result = self.gem_resolver.discover_gems_from_project(project_path)
        if result.success:
            logger.info(f"Discovered {len(result.active_gem_names)} active gems")
        else:
            logger.error(f"Failed to discover gems: {result.error_message}")
        return result

    def discover_gems_from_engine(self, engine_path: str) -> GemResolutionResult:
        """
        Discover gems from an O3DE engine installation.

        Args:
            engine_path: Path to the engine root

        Returns:
            GemResolutionResult with discovered gems
        """
        result = self.gem_resolver.discover_gems_from_engine(engine_path)
        if result.success:
            logger.info(f"Discovered {len(result.active_gem_names)} gems from engine")
        else:
            logger.error(f"Failed to discover gems: {result.error_message}")
        return result

    def generate(self) -> BindingGeneratorResult:
        """
        Generate bindings using the current configuration and loaded data.

        Returns:
            BindingGeneratorResult with statistics
        """
        if self.reflection_data is None:
            logger.error("No reflection data loaded. Call load_reflection_data first.")
            return BindingGeneratorResult(
                success=False, error_message="No reflection data loaded"
            )

        # Update generator config
        self.generator.config = self.config

        # Generate bindings
        gem_resolver = self.gem_resolver if self.config.generate_gem_bindings else None
        result = self.generator.generate_from_reflection_data(
            self.reflection_data, gem_resolver
        )

        if result.success:
            logger.info(
                f"Generated {result.classes_generated} classes, "
                f"{result.ebuses_generated} EBuses"
            )
        else:
            logger.error(f"Generation failed: {result.error_message}")

        return result

    def generate_for_gem(self, gem_name: str) -> BindingGeneratorResult:
        """
        Generate bindings for a specific gem and its dependencies.

        Args:
            gem_name: Name of the gem to generate bindings for

        Returns:
            BindingGeneratorResult with statistics
        """
        if self.reflection_data is None:
            logger.error("No reflection data loaded. Call load_reflection_data first.")
            return BindingGeneratorResult(
                success=False, error_message="No reflection data loaded"
            )

        self.generator.config = self.config
        result = self.generator.generate_for_gem(
            self.reflection_data, self.gem_resolver, gem_name
        )

        if result.success:
            logger.info(
                f"Generated bindings for {gem_name}: "
                f"{result.classes_generated} classes, {result.ebuses_generated} EBuses"
            )
        else:
            logger.error(f"Generation failed: {result.error_message}")

        return result

    def write_files(self) -> int:
        """
        Write all generated files to disk.

        Returns:
            Number of files written
        """
        return self.generator.write_files()

    def get_generated_files(self) -> List[str]:
        """Get list of generated file paths."""
        return [f.relative_path for f in self.generator.get_generated_files()]


# ============================================================
# Convenience Functions for Editor Integration
# ============================================================


def generate_all_bindings(
    output_directory: str,
    reflection_data_path: Optional[str] = None,
    project_path: Optional[str] = None,
) -> BindingGeneratorResult:
    """
    Generate all C# bindings (core + gems).

    This is the main function to call from the O3DE Editor or scripts.

    Args:
        output_directory: Where to write generated files
        reflection_data_path: Path to reflection data JSON (optional if using live reflection)
        project_path: Path to the project for gem discovery (optional)

    Returns:
        BindingGeneratorResult with statistics
    """
    orchestrator = BindingGenerationOrchestrator()
    orchestrator.configure(output_directory=output_directory)

    # Load reflection data
    if reflection_data_path:
        if not orchestrator.load_reflection_data(reflection_data_path):
            return BindingGeneratorResult(
                success=False, error_message="Failed to load reflection data"
            )
    else:
        # Try to get reflection data from the editor
        reflection_data = _get_live_reflection_data()
        if reflection_data:
            orchestrator.reflection_data = reflection_data
        else:
            return BindingGeneratorResult(
                success=False,
                error_message="No reflection data available. Provide a JSON path or run in Editor.",
            )

    # Discover gems if project path provided
    if project_path:
        gem_result = orchestrator.discover_gems_from_project(project_path)
        if not gem_result.success:
            logger.warning(f"Gem discovery failed: {gem_result.error_message}")

    # Generate and write
    result = orchestrator.generate()
    if result.success:
        files_written = orchestrator.write_files()
        result.files_written = files_written
        logger.info(f"Wrote {files_written} files to {output_directory}")

    return result


def generate_gem_bindings(
    gem_name: str,
    output_directory: str,
    reflection_data_path: Optional[str] = None,
    project_path: Optional[str] = None,
) -> BindingGeneratorResult:
    """
    Generate C# bindings for a specific gem.

    Args:
        gem_name: Name of the gem to generate bindings for
        output_directory: Where to write generated files
        reflection_data_path: Path to reflection data JSON
        project_path: Path to the project for gem discovery

    Returns:
        BindingGeneratorResult with statistics
    """
    orchestrator = BindingGenerationOrchestrator()
    orchestrator.configure(
        output_directory=output_directory,
        generate_core=False,
        generate_gems=True,
    )

    # Load reflection data
    if reflection_data_path:
        if not orchestrator.load_reflection_data(reflection_data_path):
            return BindingGeneratorResult(
                success=False, error_message="Failed to load reflection data"
            )
    else:
        reflection_data = _get_live_reflection_data()
        if reflection_data:
            orchestrator.reflection_data = reflection_data
        else:
            return BindingGeneratorResult(
                success=False, error_message="No reflection data available"
            )

    # Discover gems
    if project_path:
        gem_result = orchestrator.discover_gems_from_project(project_path)
        if not gem_result.success:
            return BindingGeneratorResult(
                success=False,
                error_message=f"Gem discovery failed: {gem_result.error_message}",
            )
    else:
        return BindingGeneratorResult(
            success=False,
            error_message="Project path required for gem-specific generation",
        )

    # Generate for specific gem
    result = orchestrator.generate_for_gem(gem_name)
    if result.success:
        files_written = orchestrator.write_files()
        result.files_written = files_written

    return result


def generate_core_bindings(
    output_directory: str,
    reflection_data_path: Optional[str] = None,
) -> BindingGeneratorResult:
    """
    Generate only core O3DE bindings (no gems).

    Args:
        output_directory: Where to write generated files
        reflection_data_path: Path to reflection data JSON

    Returns:
        BindingGeneratorResult with statistics
    """
    orchestrator = BindingGenerationOrchestrator()
    orchestrator.configure(
        output_directory=output_directory,
        generate_core=True,
        generate_gems=False,
    )

    # Load reflection data
    if reflection_data_path:
        if not orchestrator.load_reflection_data(reflection_data_path):
            return BindingGeneratorResult(
                success=False, error_message="Failed to load reflection data"
            )
    else:
        reflection_data = _get_live_reflection_data()
        if reflection_data:
            orchestrator.reflection_data = reflection_data
        else:
            return BindingGeneratorResult(
                success=False, error_message="No reflection data available"
            )

    # Generate and write
    result = orchestrator.generate()
    if result.success:
        files_written = orchestrator.write_files()
        result.files_written = files_written

    return result


def _get_live_reflection_data() -> Optional[ReflectionData]:
    """
    Try to get live reflection data from the O3DE Editor.

    This requires the O3DESharp gem to be loaded and exposes
    the reflection data via azlmbr.

    Returns:
        ReflectionData if available, None otherwise
    """
    try:
        # Try to import O3DE's Python bindings
        import azlmbr.bus as bus
        import azlmbr.o3desharp as o3desharp

        # Request reflection data from the O3DESharp system component
        json_data = o3desharp.get_reflection_data_json()
        if json_data:
            import json

            from csharp_binding_generator import (
                _parse_class_from_json,
                _parse_ebus_from_json,
            )

            data = json.loads(json_data)
            reflection_data = ReflectionData()

            for class_data in data.get("classes", []):
                cls = _parse_class_from_json(class_data)
                reflection_data.classes[cls.name] = cls

            for ebus_data in data.get("ebuses", []):
                ebus = _parse_ebus_from_json(ebus_data)
                reflection_data.ebuses[ebus.name] = ebus

            return reflection_data

    except ImportError:
        logger.debug("O3DE Python bindings not available (not running in Editor)")
    except Exception as e:
        logger.warning(f"Failed to get live reflection data: {e}")

    return None


def list_available_gems(project_path: str) -> List[str]:
    """
    List all available gems in a project.

    Args:
        project_path: Path to the project root

    Returns:
        List of gem names
    """
    resolver = GemDependencyResolver()
    result = resolver.discover_gems_from_project(project_path)
    if result.success:
        return result.active_gem_names
    return []


def get_gem_dependencies(project_path: str, gem_name: str) -> List[str]:
    """
    Get dependencies for a specific gem.

    Args:
        project_path: Path to the project root
        gem_name: Name of the gem

    Returns:
        List of dependency gem names
    """
    resolver = GemDependencyResolver()
    result = resolver.discover_gems_from_project(project_path)
    if result.success:
        return resolver.get_gem_dependencies(gem_name, include_transitive=True)
    return []


# ============================================================
# CLI Interface
# ============================================================


def create_argument_parser() -> argparse.ArgumentParser:
    """Create the command-line argument parser."""
    parser = argparse.ArgumentParser(
        description="Generate C# bindings from O3DE BehaviorContext reflection data",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
Examples:
  # Generate all bindings for a project
  python generate_bindings.py -p /path/to/project -r reflection.json -o Generated

  # Generate bindings for specific gems
  python generate_bindings.py -p /path/to/project -r reflection.json --gems PhysX Atom

  # Generate core bindings only (no gems)
  python generate_bindings.py -r reflection.json --core-only

  # Generate with separate .csproj per gem
  python generate_bindings.py -p /path/to/project -r reflection.json --per-gem-projects

  # List available gems in a project
  python generate_bindings.py -p /path/to/project --list-gems
        """,
    )

    parser.add_argument(
        "--reflection-data",
        "-r",
        help="Path to reflection data JSON file (exported from C++)",
    )
    parser.add_argument(
        "--project",
        "-p",
        help="Path to O3DE project root (for gem discovery)",
    )
    parser.add_argument(
        "--engine",
        "-e",
        help="Path to O3DE engine root (alternative to --project)",
    )
    parser.add_argument(
        "--output",
        "-o",
        default="Generated/CSharp",
        help="Output directory for generated files (default: Generated/CSharp)",
    )
    parser.add_argument(
        "--namespace",
        "-n",
        default="O3DE.Generated",
        help="Root C# namespace (default: O3DE.Generated)",
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
        "--core-only",
        action="store_true",
        help="Generate only core bindings (no gems)",
    )
    parser.add_argument(
        "--gems-only",
        action="store_true",
        help="Generate only gem bindings (no core)",
    )
    parser.add_argument(
        "--per-gem-projects",
        action="store_true",
        help="Generate separate .csproj for each gem",
    )
    parser.add_argument(
        "--combined-assembly",
        action="store_true",
        default=True,
        help="Generate a combined assembly (default)",
    )
    parser.add_argument(
        "--target-framework",
        default="net8.0",
        help="Target .NET framework (default: net8.0)",
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
        "--verbose",
        "-v",
        action="store_true",
        help="Enable verbose output",
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="Generate but don't write files",
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
    if args.list_gems:
        if not args.project and not args.engine:
            logger.error("--project or --engine required with --list-gems")
            return 1

        path = args.project or args.engine
        resolver = GemDependencyResolver()

        if args.project:
            result = resolver.discover_gems_from_project(path)
        else:
            result = resolver.discover_gems_from_engine(path)

        if not result.success:
            logger.error(f"Failed to discover gems: {result.error_message}")
            return 1

        print(f"\nDiscovered {len(result.active_gem_names)} gems:")
        for gem_name in sorted(result.active_gem_names):
            gem = resolver.get_gem(gem_name)
            deps = gem.dependencies if gem else []
            dep_str = f" -> {', '.join(deps)}" if deps else ""
            print(f"  {gem_name}{dep_str}")

        return 0

    if args.show_dependencies:
        if not args.project and not args.engine:
            logger.error("--project or --engine required with --show-dependencies")
            return 1

        path = args.project or args.engine
        resolver = GemDependencyResolver()

        if args.project:
            result = resolver.discover_gems_from_project(path)
        else:
            result = resolver.discover_gems_from_engine(path)

        if not result.success:
            logger.error(f"Failed to discover gems: {result.error_message}")
            return 1

        gem_name = args.show_dependencies
        deps = resolver.get_gem_dependencies(gem_name, include_transitive=True)

        print(f"\nDependencies for {gem_name}:")
        for dep in deps:
            print(f"  {dep}")

        dependents = resolver.get_gem_dependents(gem_name, include_transitive=True)
        if dependents:
            print(f"\nGems that depend on {gem_name}:")
            for dep in dependents:
                print(f"  {dep}")

        return 0

    # Validate required arguments for generation
    if not args.reflection_data:
        logger.error("--reflection-data is required for binding generation")
        parser.print_help()
        return 1

    # Create and configure orchestrator
    orchestrator = BindingGenerationOrchestrator()
    orchestrator.configure(
        output_directory=args.output,
        root_namespace=args.namespace,
        generate_core=not args.gems_only,
        generate_gems=not args.core_only,
        generate_per_gem_projects=args.per_gem_projects,
        include_gems=args.gems,
        exclude_gems=args.exclude_gems,
        target_framework=args.target_framework,
    )

    # Disable file writing for dry run
    if args.dry_run:
        orchestrator.config.write_to_disk = False

    # Load reflection data
    if not orchestrator.load_reflection_data(args.reflection_data):
        return 1

    # Discover gems if needed
    if not args.core_only:
        if args.project:
            gem_result = orchestrator.discover_gems_from_project(args.project)
            if not gem_result.success:
                logger.warning(f"Gem discovery failed: {gem_result.error_message}")
        elif args.engine:
            gem_result = orchestrator.discover_gems_from_engine(args.engine)
            if not gem_result.success:
                logger.warning(f"Gem discovery failed: {gem_result.error_message}")
        elif not args.core_only:
            logger.warning(
                "No --project or --engine specified, gem bindings will be limited"
            )

    # Generate bindings
    if args.gems and len(args.gems) == 1:
        # Generate for single specific gem
        result = orchestrator.generate_for_gem(args.gems[0])
    else:
        # Generate all (filtered by config)
        result = orchestrator.generate()

    if not result.success:
        logger.error(f"Generation failed: {result.error_message}")
        return 1

    # Write files (unless dry run)
    if not args.dry_run:
        files_written = orchestrator.write_files()
        logger.info(f"Wrote {files_written} files to {args.output}")
    else:
        logger.info(
            f"Dry run: would write {len(orchestrator.get_generated_files())} files"
        )
        for path in orchestrator.get_generated_files():
            logger.info(f"  {path}")

    # Print summary
    print("\n" + "=" * 60)
    print("C# Binding Generation Complete")
    print("=" * 60)
    print(f"  Classes generated:  {result.classes_generated}")
    print(f"  EBuses generated:   {result.ebuses_generated}")
    print(f"  Files generated:    {len(result.generated_files)}")

    if result.processed_gems:
        print(f"  Gems processed:     {len(result.processed_gems)}")
        for gem in result.processed_gems[:10]:
            print(f"    - {gem}")
        if len(result.processed_gems) > 10:
            print(f"    ... and {len(result.processed_gems) - 10} more")

    if result.warnings:
        print("\nWarnings:")
        for warning in result.warnings:
            print(f"  ! {warning}")

    print("=" * 60)

    return 0


if __name__ == "__main__":
    sys.exit(main())
