#!/usr/bin/env python3
#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#

"""
O3DESharp Custom Export Script

Extends O3DE's default export script to add C# assembly build support.

Usage:
    o3de.py export-project \
        --export-script Gems/O3DESharp/ExportScripts/export_project_with_csharp.py \
        --project-path <path> \
        --output-path <path>
"""

import sys
import pathlib
import logging
from typing import Any

# Import O3DE's default export script
try:
    from o3de.ExportScripts import export_source_built_project as base_export
except ImportError:
    print("ERROR: Could not import O3DE export scripts", file=sys.stderr)
    print("       Make sure O3DE scripts are in your PYTHONPATH", file=sys.stderr)
    sys.exit(1)

# Import O3DESharp export utilities
try:
    # Add the O3DESharp ExportScripts directory to path
    o3desharp_scripts_dir = pathlib.Path(__file__).parent
    if str(o3desharp_scripts_dir) not in sys.path:
        sys.path.insert(0, str(o3desharp_scripts_dir))

    import o3desharp_export_utils
except ImportError as e:
    print(f"ERROR: Could not import O3DESharp export utilities: {e}", file=sys.stderr)
    sys.exit(1)


# Store the original export function
_original_export_standalone_project = base_export.export_standalone_project


def export_standalone_project_with_csharp(
    ctx,
    tools_build_path: pathlib.Path,
    asset_processor_platform_map: dict,
    launcher_build_path: pathlib.Path,
    archive_output_format: str,
    engine_centric: bool,
    allow_registry_overrides: bool,
    should_build_tools: bool,
    should_build_all_assets: bool,
    should_build_game_launcher: bool,
    fail_on_asset_errors: bool,
    engine_assets_only: bool,
    build_config: str,
    tool_config: str,
    launcher_types: list,
    seedlist_paths: list,
    seedfile_paths: list,
    level_names: list,
    game_project_file_patterns_to_copy: list,
    server_project_file_patterns_to_copy: list,
    project_file_patterns_to_copy: list,
    asset_bundling_path: pathlib.Path,
    max_bundle_size: int,
    using_installer_sdk: bool,
    logger: logging.Logger,
    monolithic_build: bool = False
) -> int:
    """
    Extended export function that builds C# assemblies before launcher build.

    This function wraps O3DE's default export_standalone_project to inject
    C# build and deployment logic at the appropriate point in the pipeline.

    Args:
        Same arguments as O3DE's export_standalone_project

    Returns:
        Exit code (0 for success, non-zero for failure)
    """
    logger.info("O3DESharp: Using custom export script with C# support")

    # Build C# assemblies BEFORE the game launcher build
    # This ensures user DLLs are in place when launcher_build_path is copied to export output
    if should_build_game_launcher and launcher_build_path:
        logger.info("O3DESharp: Building user C# assemblies before launcher build...")

        success = o3desharp_export_utils.build_user_csharp_assemblies(
            ctx=ctx,
            launcher_build_path=launcher_build_path,
            build_config=build_config,
            logger=logger
        )

        if not success:
            logger.error("O3DESharp: Failed to build user C# assemblies")
            logger.error("O3DESharp: Export aborted")
            return 1

        logger.info("O3DESharp: User C# assemblies built successfully")
    else:
        logger.info("O3DESharp: Skipping C# build (launcher build disabled)")

    # Call the original export function with all arguments
    logger.info("O3DESharp: Continuing with standard export process...")
    return _original_export_standalone_project(
        ctx=ctx,
        tools_build_path=tools_build_path,
        asset_processor_platform_map=asset_processor_platform_map,
        launcher_build_path=launcher_build_path,
        archive_output_format=archive_output_format,
        engine_centric=engine_centric,
        allow_registry_overrides=allow_registry_overrides,
        should_build_tools=should_build_tools,
        should_build_all_assets=should_build_all_assets,
        should_build_game_launcher=should_build_game_launcher,
        fail_on_asset_errors=fail_on_asset_errors,
        engine_assets_only=engine_assets_only,
        build_config=build_config,
        tool_config=tool_config,
        launcher_types=launcher_types,
        seedlist_paths=seedlist_paths,
        seedfile_paths=seedfile_paths,
        level_names=level_names,
        game_project_file_patterns_to_copy=game_project_file_patterns_to_copy,
        server_project_file_patterns_to_copy=server_project_file_patterns_to_copy,
        project_file_patterns_to_copy=project_file_patterns_to_copy,
        asset_bundling_path=asset_bundling_path,
        max_bundle_size=max_bundle_size,
        using_installer_sdk=using_installer_sdk,
        logger=logger,
        monolithic_build=monolithic_build
    )


# Replace the export function in the base module
base_export.export_standalone_project = export_standalone_project_with_csharp

# Re-export the argument parser so the CLI works the same
export_standalone_parse_args = base_export.export_standalone_parse_args


if __name__ == "__main__":
    # This allows the script to be run directly or via o3de.py export-project
    sys.exit(base_export.export_standalone(export_standalone_parse_args()))
