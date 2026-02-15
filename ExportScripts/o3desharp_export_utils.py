#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#

"""
O3DESharp Export Utilities

Provides functions for building and deploying C# assemblies during O3DE project exports.
"""

import json
import logging
import pathlib
import subprocess
import shutil
from typing import List, Dict, Any, Optional


def check_dotnet_sdk(logger: logging.Logger) -> bool:
    """
    Check if .NET SDK is installed and accessible.

    Args:
        logger: Logger instance for output

    Returns:
        True if .NET SDK is available, False otherwise
    """
    try:
        result = subprocess.run(
            ["dotnet", "--version"],
            check=True,
            capture_output=True,
            text=True,
            timeout=10
        )
        logger.info(f"O3DESharp: Found .NET SDK version {result.stdout.strip()}")
        return True
    except subprocess.CalledProcessError as e:
        logger.error("O3DESharp: .NET SDK command failed")
        logger.error(f"O3DESharp: {e.stderr}")
        return False
    except FileNotFoundError:
        logger.error("O3DESharp: .NET SDK not found")
        logger.error("O3DESharp: Install .NET SDK from https://dotnet.microsoft.com/download")
        logger.error("O3DESharp: Restart your terminal after installation")
        return False
    except Exception as e:
        logger.error(f"O3DESharp: Failed to check .NET SDK: {e}")
        return False


def load_settings_registry(project_path: pathlib.Path, logger: logging.Logger) -> Dict[str, Any]:
    """
    Load O3DESharp settings from project's Settings Registry.

    Args:
        project_path: Path to the project root
        logger: Logger instance for output

    Returns:
        Dictionary with O3DESharp settings, or empty dict if not found
    """
    # Try multiple possible locations for settings registry
    possible_paths = [
        project_path / "Registry" / "o3desharp.setreg",
        project_path / "Registry" / "O3DESharp.setreg",
        project_path / "user" / "Registry" / "o3desharp.setreg"
    ]

    for setreg_path in possible_paths:
        if setreg_path.exists():
            try:
                logger.info(f"O3DESharp: Loading settings from {setreg_path}")
                setreg_data = json.loads(setreg_path.read_text(encoding='utf-8'))
                o3desharp_config = setreg_data.get("O3DE", {}).get("O3DESharp", {})
                return o3desharp_config
            except json.JSONDecodeError as e:
                logger.warning(f"O3DESharp: Failed to parse {setreg_path}: {e}")
            except Exception as e:
                logger.warning(f"O3DESharp: Error reading {setreg_path}: {e}")

    logger.info("O3DESharp: No settings registry found")
    return {}


def discover_user_projects(project_path: pathlib.Path, logger: logging.Logger) -> List[Dict[str, str]]:
    """
    Auto-discover user C# projects in Assets/Scripts directory.

    Args:
        project_path: Path to the project root
        logger: Logger instance for output

    Returns:
        List of project dictionaries with 'ProjectPath' and 'AssemblyName' keys
    """
    scripts_dir = project_path / "Assets" / "Scripts"
    if not scripts_dir.exists():
        logger.info("O3DESharp: Assets/Scripts directory not found")
        return []

    # Exclusion list - projects to skip during auto-discovery
    exclusions = ["O3DE.Core", "Examples", "BindingGenerator", "Example"]

    discovered = []
    for csproj in scripts_dir.rglob("*.csproj"):
        # Skip excluded directories
        if any(excl in csproj.parts for excl in exclusions):
            logger.debug(f"O3DESharp: Skipping excluded project {csproj.name}")
            continue

        relative_path = csproj.relative_to(project_path)
        assembly_name = csproj.stem + ".dll"

        discovered.append({
            "ProjectPath": str(relative_path).replace("\\", "/"),
            "AssemblyName": assembly_name
        })
        logger.info(f"O3DESharp: Auto-discovered user project: {relative_path}")

    return discovered


def build_csharp_project(
    project_path: pathlib.Path,
    csproj_relative_path: str,
    build_config: str,
    logger: logging.Logger
) -> Optional[pathlib.Path]:
    """
    Build a C# project using dotnet build.

    Args:
        project_path: Path to the project root
        csproj_relative_path: Relative path to .csproj file
        build_config: Build configuration (Debug/Release/Profile)
        logger: Logger instance for output

    Returns:
        Path to the build output directory if successful, None otherwise
    """
    csproj_path = project_path / csproj_relative_path

    if not csproj_path.exists():
        logger.error(f"O3DESharp: Project file not found: {csproj_path}")
        return None

    # Map O3DE build configs to .NET configs
    # Profile -> Release (no direct equivalent in .NET)
    dotnet_config = "Release" if build_config.lower() in ["release", "profile"] else "Debug"

    logger.info(f"O3DESharp: Building {csproj_path.name} ({dotnet_config})...")

    try:
        result = subprocess.run(
            ["dotnet", "build", str(csproj_path), "-c", dotnet_config],
            check=True,
            capture_output=True,
            text=True,
            timeout=300  # 5 minute timeout
        )

        # Log build output for debugging
        if result.stdout:
            for line in result.stdout.splitlines():
                if "warning" in line.lower() or "error" in line.lower():
                    logger.warning(f"  {line}")

        logger.info(f"O3DESharp: Successfully built {csproj_path.name}")

        # Return the expected output directory
        project_dir = csproj_path.parent
        output_dir = project_dir / "bin" / dotnet_config
        return output_dir

    except subprocess.CalledProcessError as e:
        logger.error(f"O3DESharp: Failed to build {csproj_path}")
        logger.error(f"O3DESharp: Exit code: {e.returncode}")

        # Log full error output
        if e.stderr:
            logger.error("O3DESharp: Build errors:")
            for line in e.stderr.splitlines():
                logger.error(f"  {line}")
        if e.stdout:
            logger.error("O3DESharp: Build output:")
            for line in e.stdout.splitlines():
                logger.error(f"  {line}")

        return None

    except subprocess.TimeoutExpired:
        logger.error(f"O3DESharp: Build timed out after 5 minutes: {csproj_path}")
        return None

    except Exception as e:
        logger.error(f"O3DESharp: Unexpected error building {csproj_path}: {e}")
        return None


def deploy_assembly(
    output_dir: pathlib.Path,
    assembly_name: str,
    deploy_path: pathlib.Path,
    logger: logging.Logger
) -> bool:
    """
    Deploy a built C# assembly and its dependencies to the target directory.

    Args:
        output_dir: Directory containing built assembly (bin/Config/)
        assembly_name: Name of the assembly DLL
        deploy_path: Target deployment directory
        logger: Logger instance for output

    Returns:
        True if deployment successful, False otherwise
    """
    # .NET projects can target multiple frameworks (net8.0, net9.0, etc.)
    # Search for the assembly in common framework directories
    framework_patterns = ["net9.0", "net8.0", "net7.0", "net6.0", "netstandard2.1"]

    found = False
    for framework in framework_patterns:
        framework_dir = output_dir / framework
        assembly_path = framework_dir / assembly_name

        if assembly_path.exists():
            # Found the assembly, deploy it
            deploy_path.mkdir(parents=True, exist_ok=True)

            try:
                # Copy the main DLL
                target_dll = deploy_path / assembly_name
                shutil.copy2(assembly_path, target_dll)
                logger.info(f"O3DESharp: Deployed {assembly_name} to {deploy_path}")

                # Copy .deps.json if present (dependency manifest)
                deps_file = assembly_path.with_suffix(".deps.json")
                if deps_file.exists():
                    shutil.copy2(deps_file, deploy_path / deps_file.name)
                    logger.debug(f"O3DESharp: Deployed {deps_file.name}")

                # Copy .pdb if present (debug symbols)
                pdb_file = assembly_path.with_suffix(".pdb")
                if pdb_file.exists():
                    shutil.copy2(pdb_file, deploy_path / pdb_file.name)
                    logger.debug(f"O3DESharp: Deployed {pdb_file.name}")

                found = True
                break

            except Exception as e:
                logger.error(f"O3DESharp: Failed to copy {assembly_name}: {e}")
                return False

    if not found:
        logger.warning(f"O3DESharp: Could not find {assembly_name} in {output_dir}")
        logger.warning(f"O3DESharp: Searched frameworks: {', '.join(framework_patterns)}")
        return False

    return True


def build_user_csharp_assemblies(
    ctx,
    launcher_build_path: pathlib.Path,
    build_config: str,
    logger: logging.Logger
) -> bool:
    """
    Build user C# assemblies and deploy them to the launcher build directory.

    This is the main entry point called by the export script.

    Args:
        ctx: O3DE export context (has project_path attribute)
        launcher_build_path: Path to launcher build directory
        build_config: Build configuration (debug/profile/release)
        logger: Logger instance for output

    Returns:
        True if successful (or no user assemblies), False on error
    """
    logger.info("=" * 80)
    logger.info("O3DESharp: Building and deploying user C# assemblies")
    logger.info("=" * 80)

    project_path = pathlib.Path(ctx.project_path)

    # Check .NET SDK first
    if not check_dotnet_sdk(logger):
        return False

    # Load settings registry
    config = load_settings_registry(project_path, logger)
    user_projects = config.get("UserAssemblies", [])

    # Fallback to auto-discovery if no explicit configuration
    if not user_projects:
        logger.info("O3DESharp: No UserAssemblies in settings registry, auto-discovering...")
        user_projects = discover_user_projects(project_path, logger)

    # No user projects is valid - just deploy core runtime
    if not user_projects:
        logger.info("O3DESharp: No user C# projects found")
        logger.info("O3DESharp: Only core runtime (Coral, O3DE.Core) will be deployed")
        return True

    logger.info(f"O3DESharp: Found {len(user_projects)} user project(s) to build")

    # Deployment target directory
    deploy_path = pathlib.Path(launcher_build_path) / "bin" / build_config / "Bin" / "Scripts"
    logger.info(f"O3DESharp: Deployment target: {deploy_path}")

    # Build and deploy each project
    success_count = 0
    for project in user_projects:
        project_relative_path = project["ProjectPath"]
        assembly_name = project.get("AssemblyName", pathlib.Path(project_relative_path).stem + ".dll")

        # Build the project
        output_dir = build_csharp_project(
            project_path,
            project_relative_path,
            build_config,
            logger
        )

        if output_dir is None:
            logger.error(f"O3DESharp: Build failed for {project_relative_path}")
            return False  # Fail fast on build errors

        # Deploy the built assembly
        if deploy_assembly(output_dir, assembly_name, deploy_path, logger):
            success_count += 1
        else:
            logger.warning(f"O3DESharp: Deployment failed for {assembly_name}")
            # Continue with other projects (deployment failures are warnings, not errors)

    logger.info("=" * 80)
    logger.info(f"O3DESharp: Successfully built and deployed {success_count}/{len(user_projects)} user assemblies")
    logger.info("=" * 80)

    return True
