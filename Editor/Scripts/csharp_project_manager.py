#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#

"""
C# Project Manager for O3DESharp

Provides tools to create and manage C# scripting projects within O3DE.
"""

import os
import json
import re
import subprocess
from pathlib import Path
from typing import Optional, List, Dict, Any

import azlmbr.bus as bus
import azlmbr.editor as editor
import azlmbr.paths as paths


# Phase 16b helpers.

# Sentinel file used to communicate "a C# build is in progress" from Python
# back to the C++ Phase 16a file watcher. Lives in <ProjectPath>/user/ so it
# survives the editor process but stays out of source-control. AZ::IO::
# SystemFile::Exists is cheap (single stat) so the watcher polling it on each
# debounce expiry is fine.
_BUILD_IN_PROGRESS_FILENAME = ".csharp_build_in_progress"


def _build_in_progress_path() -> Optional[Path]:
    """
    Returns the sentinel-file path, or None if the project root can't be
    resolved (non-editor contexts where azlmbr.paths isn't usable).
    """
    try:
        root = Path(paths.projectroot)
    except Exception:  # noqa: BLE001 - happens in CLI / asset-builder contexts
        return None
    return root / "user" / _BUILD_IN_PROGRESS_FILENAME


def _set_build_in_progress(flag: bool) -> None:
    """
    Create or remove the sentinel file the Phase 16a file watcher polls.
    While the sentinel exists, the watcher reschedules every reload until
    the build finishes - so every transient DLL write during a dotnet build
    coalesces into one reload at the end.

    Best-effort: silently no-ops if the path can't be resolved or the
    filesystem write fails. Worst case the watcher fires one extra reload
    mid-build, which is harmless (debounce dedupes; reload is idempotent).
    """
    sentinel = _build_in_progress_path()
    if sentinel is None:
        return
    try:
        if flag:
            sentinel.parent.mkdir(parents=True, exist_ok=True)
            sentinel.write_text("", encoding="utf-8")
        else:
            if sentinel.exists():
                sentinel.unlink()
    except OSError:
        pass


# Marker the MSBuild deploy target carries so migrate_csproj_to_deploy_target
# can detect already-migrated projects and skip them. Keep in sync with the
# Target name in CSPROJ_TEMPLATE.
_DEPLOY_TARGET_MARKER = 'Name="DeployToBinScripts"'

# Block injected into existing csprojs by the migration helper. The form is
# duplicated (not shared) with CSPROJ_TEMPLATE because new projects can use
# a slightly different idiomatic layout (e.g. an explicit PropertyGroup for
# O3DEDeployPath); the migration only needs to add the bare minimum.
_DEPLOY_TARGET_BLOCK = r'''
  <PropertyGroup>
    <O3DEDeployPath Condition="'$(O3DEDeployPath)' == ''">$(MSBuildProjectDirectory)\..\..\..\..\Bin\Scripts</O3DEDeployPath>
  </PropertyGroup>

  <!-- Phase 16b auto-deploy: every Build copies the output to
       <ProjectPath>/Bin/Scripts/, which is where the Coral runtime loads user
       assemblies from and where the editor's file watcher (Phase 16a) auto-
       reload trigger is attached. Override $(O3DEDeployPath) above if your
       csproj is not at the canonical Gem/Source/CSharp/<Name>/ depth. -->
  <Target Name="DeployToBinScripts" AfterTargets="Build">
    <Message Text="O3DESharp: deploying $(AssemblyName).dll -&gt; $(O3DEDeployPath)" Importance="high"/>
    <MakeDir Directories="$(O3DEDeployPath)"/>
    <Copy SourceFiles="$(TargetPath)"
          DestinationFolder="$(O3DEDeployPath)"
          SkipUnchangedFiles="true"
          ContinueOnError="true"/>
    <Copy SourceFiles="$(TargetDir)$(AssemblyName).pdb"
          DestinationFolder="$(O3DEDeployPath)"
          SkipUnchangedFiles="true"
          ContinueOnError="true"
          Condition="Exists('$(TargetDir)$(AssemblyName).pdb')"/>
  </Target>

'''


def migrate_csproj_to_deploy_target(csproj_path: Path) -> Dict[str, Any]:
    """
    Add the Phase 16b auto-deploy target to an existing .csproj if it isn't
    already present. Returns a result dict with status / message / changed
    flag.

    The injection is a simple textual splice: find the closing </Project>
    tag and insert the deploy block just before it. We don't parse XML
    properly because user .csprojs often contain comments, custom MSBuild
    extensions, conditional ItemGroups, etc., and a roundtrip through an
    XML parser would reorder and reformat the file. A targeted regex insert
    preserves the user's formatting.
    """
    csproj_path = Path(csproj_path)
    if not csproj_path.exists():
        return {
            "success": False,
            "changed": False,
            "message": f"csproj not found: {csproj_path}",
        }

    try:
        content = csproj_path.read_text(encoding='utf-8')
    except OSError as e:
        return {
            "success": False,
            "changed": False,
            "message": f"Failed to read {csproj_path}: {e}",
        }

    if _DEPLOY_TARGET_MARKER in content:
        return {
            "success": True,
            "changed": False,
            "message": f"Already migrated: {csproj_path.name}",
        }

    # Splice the block in just before the closing </Project>. Use rsplit so
    # we hit the LAST </Project> in case the user embeds the literal text in
    # a comment.
    if '</Project>' not in content:
        return {
            "success": False,
            "changed": False,
            "message": f"Could not find </Project> tag in {csproj_path}",
        }

    before, sep, after = content.rpartition('</Project>')
    new_content = before + _DEPLOY_TARGET_BLOCK + sep + after

    # Back up the original alongside it before overwriting. Lets users
    # diff / revert if they want to keep their existing post-build hooks
    # instead.
    backup_path = csproj_path.with_suffix(csproj_path.suffix + '.pre-deploy-target.bak')
    try:
        if not backup_path.exists():
            backup_path.write_text(content, encoding='utf-8')
        csproj_path.write_text(new_content, encoding='utf-8')
    except OSError as e:
        return {
            "success": False,
            "changed": False,
            "message": f"Failed to write migrated csproj: {e}",
        }

    return {
        "success": True,
        "changed": True,
        "message": f"Migrated {csproj_path.name} (original kept as {backup_path.name})",
    }

# Templates for C# project files.
#
# TargetFramework MUST match O3DE.Core.csproj. Phase 1 bumped O3DE.Core to
# net9.0; user projects that stayed on net8.0 hit error CS1705 at build time
# because O3DE.Core.dll exports System.Runtime 9.0.0.0 but the user's project
# references the 8.0.0.0 ref pack. Keep this in lockstep with O3DE.Core.csproj.
#
# Phase 16b: the DeployToBinScripts target runs after every Build (regardless
# of caller: dotnet CLI, IDE, or the C# Project Manager) and copies the
# output into <ProjectPath>/Bin/Scripts/. That's the path the Coral runtime
# (O3DESharpSystemComponent::InitializeCoralHost) reads user assemblies from,
# AND the path the Phase 16a editor file watcher monitors for auto-reload.
# So a successful build anywhere triggers an editor reload automatically.
#
# O3DEDeployPath defaults to four levels up from the csproj (matches the
# canonical <ProjectPath>/Gem/Source/CSharp/<Name>/<Name>.csproj layout
# generated by csharp_editor_bootstrap.create_csharp_project). Users with a
# different layout can override the property:
#   <O3DEDeployPath>$(MSBuildProjectDirectory)\\..\\..\\Bin\\Scripts</O3DEDeployPath>
CSPROJ_TEMPLATE = r'''<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <OutputPath>bin/$(Configuration)</OutputPath>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
    <!-- Phase 16b deploy target uses this. Override per-project if your
         csproj is not at the default <ProjectPath>/Gem/Source/CSharp/<Name>/ depth. -->
    <O3DEDeployPath Condition="'$(O3DEDeployPath)' == ''">$(MSBuildProjectDirectory)\..\..\..\..\Bin\Scripts</O3DEDeployPath>
  </PropertyGroup>

  <ItemGroup>
    <Reference Include="O3DE.Core">
      <HintPath>{o3de_core_path}</HintPath>
    </Reference>
  </ItemGroup>

  <!-- Auto-deploy after every Build so IDE rebuilds (Rider/VS) and the C#
       Project Manager build flow both land their DLL where the Coral runtime
       picks it up. ContinueOnError prevents a locked Bin/Scripts/*.dll
       (engine running) from failing the IDE build entirely - it'll just warn. -->
  <Target Name="DeployToBinScripts" AfterTargets="Build">
    <Message Text="O3DESharp: deploying $(AssemblyName).dll -> $(O3DEDeployPath)" Importance="high"/>
    <MakeDir Directories="$(O3DEDeployPath)"/>
    <Copy SourceFiles="$(TargetPath)"
          DestinationFolder="$(O3DEDeployPath)"
          SkipUnchangedFiles="true"
          ContinueOnError="true"/>
    <Copy SourceFiles="$(TargetDir)$(AssemblyName).pdb"
          DestinationFolder="$(O3DEDeployPath)"
          SkipUnchangedFiles="true"
          ContinueOnError="true"
          Condition="Exists('$(TargetDir)$(AssemblyName).pdb')"/>
  </Target>

</Project>
'''

SCRIPT_COMPONENT_TEMPLATE = '''using O3DE;

namespace {namespace}
{{
    /// <summary>
    /// {description}
    /// </summary>
    public class {class_name} : ScriptComponent
    {{
        // Called when the component is activated
        public override void OnCreate()
        {{
            Debug.Log("{class_name} created on entity " + EntityId);
        }}

        // Called every frame
        public override void OnUpdate(float deltaTime)
        {{
            // Add your update logic here
        }}

        // Called when the component is deactivated
        public override void OnDestroy()
        {{
            Debug.Log("{class_name} destroyed");
        }}
    }}
}}
'''

EMPTY_CLASS_TEMPLATE = '''using O3DE;

namespace {namespace}
{{
    /// <summary>
    /// {description}
    /// </summary>
    public class {class_name}
    {{
        public {class_name}()
        {{
        }}
    }}
}}
'''

STATIC_CLASS_TEMPLATE = '''using O3DE;

namespace {namespace}
{{
    /// <summary>
    /// {description}
    /// </summary>
    public static class {class_name}
    {{
        // Add your static methods here
    }}
}}
'''

class CSharpProjectManager:
    """Manages C# projects for O3DE scripting."""
    
    # Settings file name for persisting configuration
    SETTINGS_FILE = "o3desharp_settings.json"
    
    # Script templates available for creation
    SCRIPT_TEMPLATES = {
        'ScriptComponent': {
            'template': SCRIPT_COMPONENT_TEMPLATE,
            'description': 'A component script that can be attached to entities',
            'base_class': 'ScriptComponent'
        },
        'EmptyClass': {
            'template': EMPTY_CLASS_TEMPLATE,
            'description': 'An empty C# class',
            'base_class': None
        },
        'StaticClass': {
            'template': STATIC_CLASS_TEMPLATE,
            'description': 'A static utility class',
            'base_class': None
        }
    }
    
    def __init__(self):
        self.project_path = self._get_project_path()
        self.gem_path = self._get_gem_path()
        self.scripts_path = self._get_scripts_path()
        self.is_installed = self._check_if_installed()
        self._settings = self._load_settings()
        # Path to Coral.Managed.dll - used for C# project references (NOT the O3DE.Core runtime API)
        self.coral_managed_path = self._get_coral_managed_path()
        self.user_assembly_path = self._get_user_assembly_path()
        # Path to O3DE.Core.dll - used for C# project references
        self.o3de_core_path = self._get_o3de_core_path()
        
        # In-memory cache for script discovery (file_path -> (mtime, parsed_data))
        self._script_cache: Dict[str, tuple] = {}
        
        # Cache for list_scripts results per project (project_path -> (timestamp, scripts))
        self._project_scripts_cache: Dict[str, tuple] = {}
        self._cache_ttl = 5.0  # Cache valid for 5 seconds
        
        # Automatically ensure runtime dependencies are deployed
        self._auto_deploy_runtime()
    
    def _auto_deploy_runtime(self):
        """
        Automatically deploy Coral and O3DE.Core if they are not already deployed,
        or if the deployed versions are older than the latest available build output.
        This runs silently during initialization to ensure the C# scripting system works.
        """
        try:
            self._auto_deploy_one(
                "Coral",
                self.get_coral_deploy_path() / "Coral.Managed.dll",
                self.find_coral_source_files,
                self.deploy_coral
            )
            self._auto_deploy_one(
                "O3DE.Core",
                self.get_o3de_core_deploy_path() / "O3DE.Core.dll",
                self.find_o3de_core_source_files,
                self.deploy_o3de_core
            )
        except Exception as e:
            # Don't fail initialization if auto-deploy fails
            print(f"O3DESharp: Auto-deployment warning: {e}")

    def _auto_deploy_one(self, label: str, deployed_dll: Path, find_fn, deploy_fn):
        """
        Deploy a component if it is missing or stale compared to the latest source.
        """
        source_files = find_fn()
        source_dll = source_files.get("dll")

        needs_deploy = False
        if not deployed_dll.exists():
            needs_deploy = True
        elif source_dll and source_dll.exists():
            # Redeploy if the source is newer than the currently deployed copy
            if source_dll.stat().st_mtime > deployed_dll.stat().st_mtime:
                needs_deploy = True

        if needs_deploy:
            result = deploy_fn()
            if result["success"]:
                print(f"O3DESharp: Auto-deployed {label} to {result.get('deploy_path', 'project')}")
            else:
                print(f"O3DESharp: Failed to auto-deploy {label}: {result.get('message', 'unknown error')}")
    
    def ensure_runtime_deployed(self) -> Dict[str, Any]:
        """
        Ensure both Coral and O3DE.Core are deployed to the project.
        
        This method checks if the required runtime files are present and
        deploys them if they are missing. Call this before operations that
        require the C# runtime (building, running scripts, etc.).
        
        Returns:
            Dict with 'success', 'coral_deployed', 'core_deployed', and 'message' keys
        """
        results = {
            "success": True,
            "coral_deployed": False,
            "core_deployed": False,
            "messages": []
        }
        
        # Check and deploy Coral
        coral_status = self.check_coral_deployment()
        if not coral_status["deployed"]:
            coral_result = self.deploy_coral()
            if coral_result["success"]:
                results["coral_deployed"] = True
                results["messages"].append(f"Deployed Coral: {coral_result['message']}")
            else:
                results["success"] = False
                results["messages"].append(f"Failed to deploy Coral: {coral_result['message']}")
        else:
            results["coral_deployed"] = True
            results["messages"].append("Coral already deployed")
        
        # Check and deploy O3DE.Core
        core_status = self.check_o3de_core_deployment()
        if not core_status["deployed"]:
            core_result = self.deploy_o3de_core()
            if core_result["success"]:
                results["core_deployed"] = True
                results["messages"].append(f"Deployed O3DE.Core: {core_result['message']}")
            else:
                results["success"] = False
                results["messages"].append(f"Failed to deploy O3DE.Core: {core_result['message']}")
        else:
            results["core_deployed"] = True
            results["messages"].append("O3DE.Core already deployed")
        
        results["message"] = "; ".join(results["messages"])
        return results
        
    def _get_project_path(self) -> Path:
        """Get the current O3DE project's source path."""
        return Path(paths.projectroot)
    
    def _get_gem_path(self) -> Path:
        """Get the project's Gem directory."""
        # The project's gem is typically at <project>/Gem
        gem_path = self.project_path / "Gem"
        if gem_path.exists():
            return gem_path
        # Fallback: some projects might use a different structure
        return self.project_path
    
    def _check_if_installed(self) -> bool:
        """Check if we're running from an installed engine."""
        engine_root = Path(paths.engroot)
        # In an installed engine, there's typically an 'install' marker or the structure is different
        # Check for common indicators of an installed engine
        return (engine_root / "engine.json").exists() and not (engine_root / "CMakeLists.txt").exists()
    
    def _get_scripts_path(self) -> Path:
        """Get the default scripts directory for new C# projects."""
        # Default location for new C# scripts is under the project's Gem
        scripts_path = self.gem_path / "Source" / "CSharp"
        return scripts_path
    
    def _get_settings_path(self) -> Path:
        """Get the path to the settings file."""
        return self.project_path / self.SETTINGS_FILE
    
    def _load_settings(self) -> Dict[str, Any]:
        """Load settings from the project's settings file."""
        settings_path = self._get_settings_path()
        if settings_path.exists():
            try:
                return json.loads(settings_path.read_text())
            except:
                pass
        return {}
    
    def _save_settings(self):
        """Save settings to the project's settings file."""
        settings_path = self._get_settings_path()
        try:
            settings_path.write_text(json.dumps(self._settings, indent=2))
        except Exception as e:
            print(f"Failed to save O3DESharp settings: {e}")
    
    def _get_coral_managed_path(self) -> str:
        """
        Get the path to Coral.Managed.dll (the managed host library).
        
        This is NOT the O3DE.Core API assembly - that's deployed separately.
        Coral.Managed.dll is referenced by user C# projects.
        
        Checks settings first, then auto-detects from common locations.
        """
        # Check if user has configured a custom path
        custom_path = self._settings.get("coral_managed_path")
        if custom_path:
            path = Path(custom_path)
            if path.exists():
                return str(path)
        
        engine_root = Path(paths.engroot)

        # Try multiple possible locations for the managed assembly. net9.0 is
        # listed first because O3DE.Core / Coral.Managed are built against
        # net9 (see O3DE.Core.csproj). net8.0 entries stay for any pinned
        # legacy build outputs sitting on disk from before the bump.
        possible_paths = [
            # CMake staging directory (created by O3DESharp.StageCoral target)
            engine_root / "Gems" / "O3DESharp" / "bin" / "Coral" / "Coral.Managed.dll",
            # Coral.Managed locations
            engine_root / "Gems" / "O3DESharp" / "External" / "Coral" / "Coral.Managed" / "bin" / "Release" / "net9.0" / "Coral.Managed.dll",
            engine_root / "Gems" / "O3DESharp" / "External" / "Coral" / "Coral.Managed" / "bin" / "Release" / "net8.0" / "Coral.Managed.dll",
            engine_root / "Gems" / "O3DESharp" / "bin" / "Coral.Managed.dll",
            # O3DE.Core locations
            engine_root / "Gems" / "O3DESharp" / "Assets" / "Scripts" / "O3DE.Core" / "bin" / "Release" / "net9.0" / "O3DE.Core.dll",
            engine_root / "Gems" / "O3DESharp" / "Assets" / "Scripts" / "O3DE.Core" / "bin" / "Release" / "net8.0" / "O3DE.Core.dll",
            # Installed engine paths
            engine_root / "Gems" / "O3DESharp" / "bin" / "Release" / "Coral.Managed.dll",
            engine_root / "bin" / "Release" / "Coral.Managed.dll",
        ]
        
        for path in possible_paths:
            if path.exists():
                return str(path)
        
        # Return the default Coral.Managed path even if it doesn't exist yet
        return str(possible_paths[0])
    
    def get_coral_managed_path(self) -> str:
        """Get the current Coral.Managed.dll path (for C# project references)."""
        return self.coral_managed_path
    
    def set_coral_managed_path(self, path: str) -> Dict[str, Any]:
        """
        Set a custom path for Coral.Managed.dll.
        
        This is the managed host library that C# projects reference.
        NOT to be confused with O3DE.Core.dll which is deployed separately.
        
        Args:
            path: Path to Coral.Managed.dll
            
        Returns:
            Dict with 'success' and 'message' keys
        """
        path_obj = Path(path)
        
        if not path_obj.exists():
            return {
                "success": False,
                "message": f"File does not exist: {path}"
            }
        
        if not path_obj.suffix.lower() == ".dll":
            return {
                "success": False,
                "message": "File must be a .dll assembly"
            }
        
        self._settings["coral_managed_path"] = str(path_obj.resolve())
        self._save_settings()
        self.coral_managed_path = str(path_obj.resolve())
        
        # Update the settings registry for C++ code
        self._update_settings_registry()
        
        return {
            "success": True,
            "message": f"Coral.Managed path set to: {path}"
        }
    
    def clear_coral_managed_path(self):
        """Clear the custom Coral.Managed path and revert to auto-detection."""
        if "coral_managed_path" in self._settings:
            del self._settings["coral_managed_path"]
            self._save_settings()
        self.coral_managed_path = self._get_coral_managed_path()
        self._update_settings_registry()
    
    def _get_user_assembly_path(self) -> str:
        """
        Get the path to the user's game scripts assembly.
        
        Checks settings first, then returns the default path.
        """
        # Check if user has configured a custom path
        custom_path = self._settings.get("user_assembly_path")
        if custom_path:
            return custom_path
        
        # Default path
        return str(self.project_path / "Bin" / "Scripts" / "GameScripts.dll")
    
    def _get_o3de_core_path(self) -> str:
        """
        Get the path to O3DE.Core.dll for project references.
        
        Returns the deployed location in the project's Bin/Scripts folder.
        """
        return str(self.project_path / "Bin" / "Scripts" / "O3DE.Core.dll")

    def get_user_assembly_path(self) -> str:
        """Get the current user assembly path."""
        return self.user_assembly_path
    
    def get_user_assembly_name(self) -> str:
        """Get just the assembly name (without path)."""
        return Path(self.user_assembly_path).stem
    
    def set_user_assembly_path(self, path: str) -> Dict[str, Any]:
        """
        Set a custom path for the user's game scripts assembly.
        
        Args:
            path: Full path to the user's assembly DLL
            
        Returns:
            Dict with 'success' and 'message' keys
        """
        path_obj = Path(path)
        
        if not path_obj.suffix.lower() == ".dll":
            return {
                "success": False,
                "message": "Path must end with .dll"
            }
        
        self._settings["user_assembly_path"] = str(path_obj)
        self._save_settings()
        self.user_assembly_path = str(path_obj)
        
        # Also update the settings registry for the C++ code to read
        self._update_settings_registry()
        
        return {
            "success": True,
            "message": f"User assembly path set to: {path}"
        }
    
    def set_user_assembly_name(self, name: str) -> Dict[str, Any]:
        """
        Set the user assembly by name (uses default Scripts directory).
        
        Args:
            name: Assembly name (e.g., "MyGame" will become "MyGame.dll")
            
        Returns:
            Dict with 'success' and 'message' keys
        """
        # Remove .dll extension if provided
        if name.lower().endswith(".dll"):
            name = name[:-4]
        
        # Build the full path
        assembly_path = self.project_path / "Bin" / "Scripts" / f"{name}.dll"
        
        return self.set_user_assembly_path(str(assembly_path))
    
    def clear_user_assembly_path(self):
        """Clear the custom user assembly path and revert to default."""
        if "user_assembly_path" in self._settings:
            del self._settings["user_assembly_path"]
            self._save_settings()
        self.user_assembly_path = self._get_user_assembly_path()
        self._update_settings_registry()
    
    def _update_settings_registry(self):
        """
        Update the O3DE settings registry with current settings.
        
        This creates/updates a .setreg file that the C++ code reads.
        """
        registry_path = self.project_path / "Registry" / "o3desharp.setreg"
        
        try:
            registry_path.parent.mkdir(parents=True, exist_ok=True)
            
            # Build the settings structure
            settings = {
                "O3DE": {
                    "O3DESharp": {}
                }
            }
            
            # Add user assembly path if customized
            if "user_assembly_path" in self._settings:
                settings["O3DE"]["O3DESharp"]["UserAssemblyPath"] = self._settings["user_assembly_path"]
            
            # Add Coral directory if customized
            if "coral_directory" in self._settings:
                settings["O3DE"]["O3DESharp"]["CoralDirectory"] = self._settings["coral_directory"]
            
            # NOTE: CoreApiAssemblyPath is NOT written here.
            # O3DE.Core.dll path is computed by C++ code based on project path.
            # coral_managed_path is for Coral.Managed.dll (C# project references only).
            
            # Only write if there are settings to save
            if settings["O3DE"]["O3DESharp"]:
                registry_path.write_text(json.dumps(settings, indent=4))
            elif registry_path.exists():
                # Remove the file if no custom settings
                registry_path.unlink()
                
        except Exception as e:
            print(f"Failed to update settings registry: {e}")
    
    def get_coral_deploy_path(self) -> Path:
        """Get the path where Coral files should be deployed."""
        return self.project_path / "Bin" / "Scripts" / "Coral"
    
    @staticmethod
    def _find_latest_file(filename: str, search_dirs: list, rglob_roots: list = None) -> 'Path | None':
        """
        Scan *search_dirs* (direct check) and *rglob_roots* (recursive search)
        for *filename* and return the path with the newest modification time,
        or None if the file could not be found anywhere.
        """
        best_path: 'Path | None' = None
        best_mtime: float = 0.0

        # Direct locations first (fast)
        for d in search_dirs:
            candidate = d / filename
            if candidate.is_file():
                mt = candidate.stat().st_mtime
                if mt > best_mtime:
                    best_mtime = mt
                    best_path = candidate

        # Recursive search in broader roots (slower, but catches CMake _deps/build trees)
        for root in (rglob_roots or []):
            if not root.is_dir():
                continue
            try:
                for candidate in root.rglob(filename):
                    # Skip obj/intermediate directories to avoid ref-assembly copies
                    parts_lower = [p.lower() for p in candidate.parts]
                    if 'obj' in parts_lower or 'ref' in parts_lower or 'refint' in parts_lower:
                        continue
                    mt = candidate.stat().st_mtime
                    if mt > best_mtime:
                        best_mtime = mt
                        best_path = candidate
            except OSError:
                continue

        return best_path

    def _get_assembly_search_dirs(self):
        """
        Return (direct_dirs, rglob_roots) tuples for both Coral and O3DE.Core
        searches.  These cover the gem staging folder, dotnet build outputs,
        CMake build output, install tree, and the executable folder.
        """
        engine_root = Path(paths.engroot)
        gem_root = engine_root / "Gems" / "O3DESharp"
        exe_folder = Path(paths.executableFolder)
        # The CMake build workspace is typically the parent of the executable folder
        # e.g. F:/o3de/workspace/bin/profile -> F:/o3de/workspace
        build_workspace = exe_folder.parent.parent if exe_folder.exists() else None

        direct_dirs = [
            # Gem staging (committed / CMake copy)
            gem_root / "bin" / "Coral",
            gem_root / "bin" / "O3DE.Core",
            gem_root / "bin",
            # dotnet build outputs (all config x TFM combos)
            gem_root / "Assets" / "Scripts" / "O3DE.Core" / "bin" / "Debug" / "net9.0",
            gem_root / "Assets" / "Scripts" / "O3DE.Core" / "bin" / "Release" / "net9.0",
            gem_root / "Assets" / "Scripts" / "O3DE.Core" / "bin" / "Debug" / "net8.0",
            gem_root / "Assets" / "Scripts" / "O3DE.Core" / "bin" / "Release" / "net8.0",
            # Install tree copies
            engine_root / "install" / "Gems" / "O3DESharp" / "Assets" / "Scripts" / "O3DE.Core" / "bin" / "Debug" / "net9.0",
            engine_root / "install" / "Gems" / "O3DESharp" / "Assets" / "Scripts" / "O3DE.Core" / "bin" / "Release" / "net9.0",
            engine_root / "install" / "Gems" / "O3DESharp" / "Assets" / "Scripts" / "O3DE.Core" / "bin" / "Debug" / "net8.0",
            engine_root / "install" / "Gems" / "O3DESharp" / "Assets" / "Scripts" / "O3DE.Core" / "bin" / "Release" / "net8.0",
        ]

        # User-configured Coral.Managed path directory
        if self.coral_managed_path:
            managed_dir = Path(self.coral_managed_path).parent
            if managed_dir not in direct_dirs:
                direct_dirs.insert(0, managed_dir)

        rglob_roots = []
        # CMake build workspace (contains _deps/coral-build, Build/profile, etc.)
        if build_workspace and build_workspace.is_dir():
            rglob_roots.append(build_workspace / "Build")
            rglob_roots.append(build_workspace / "_deps")
        # Fallback: engine-level build / out directories
        for d in [engine_root / "build", engine_root / "out"]:
            if d.is_dir() and d not in rglob_roots:
                rglob_roots.append(d)

        return direct_dirs, rglob_roots

    def find_coral_source_files(self) -> Dict[str, Path]:
        """
        Find the **newest** Coral.Managed build output files across all known
        build-output and staging locations.

        Returns:
            Dict mapping file type to Path, or empty dict if not found.
        """
        direct_dirs, rglob_roots = self._get_assembly_search_dirs()

        dll_path = self._find_latest_file("Coral.Managed.dll", direct_dirs, rglob_roots)
        if dll_path is None:
            return {}

        # Companion files live next to the DLL
        result: Dict[str, Path] = {"dll": dll_path}
        dll_dir = dll_path.parent
        for key, name in [("runtimeconfig", "Coral.Managed.runtimeconfig.json"),
                          ("deps", "Coral.Managed.deps.json")]:
            companion = dll_dir / name
            if companion.is_file():
                result[key] = companion
            else:
                # Companion might be newer elsewhere; search broadly
                found = self._find_latest_file(name, direct_dirs, rglob_roots)
                if found:
                    result[key] = found

        return result
    
    def deploy_coral(self, source_path: str = None) -> Dict[str, Any]:
        """
        Deploy Coral.Managed files to the project's Bin/Scripts/Coral directory.
        
        This copies Coral.Managed.dll, Coral.Managed.runtimeconfig.json, and
        Coral.Managed.deps.json to the project's runtime directory so the
        C# scripting system can initialize.
        
        Args:
            source_path: Optional path to directory containing Coral.Managed files.
                         If not provided, will auto-detect from build output.
        
        Returns:
            Dict with 'success', 'message', and 'deployed_files' keys
        """
        import shutil
        
        deploy_path = self.get_coral_deploy_path()
        
        # Find source files
        if source_path:
            source_dir = Path(source_path)
            if not source_dir.exists():
                return {
                    "success": False,
                    "message": f"Source path does not exist: {source_path}",
                    "deployed_files": []
                }
            
            # Build file list from provided path
            source_files = {}
            for key, filename in [("dll", "Coral.Managed.dll"), 
                                   ("runtimeconfig", "Coral.Managed.runtimeconfig.json"),
                                   ("deps", "Coral.Managed.deps.json")]:
                file_path = source_dir / filename
                if file_path.exists():
                    source_files[key] = file_path
        else:
            source_files = self.find_coral_source_files()
        
        if not source_files:
            return {
                "success": False,
                "message": "Could not find Coral.Managed build output files. "
                          "Please build the engine with O3DESharp Gem enabled, "
                          "or specify the path to Coral.Managed files manually.",
                "deployed_files": []
            }
        
        # Ensure at least the DLL is found
        if "dll" not in source_files:
            return {
                "success": False,
                "message": "Coral.Managed.dll not found in source location.",
                "deployed_files": []
            }
        
        # Create deploy directory
        try:
            deploy_path.mkdir(parents=True, exist_ok=True)
        except Exception as e:
            return {
                "success": False,
                "message": f"Failed to create deploy directory: {e}",
                "deployed_files": []
            }
        
        # Copy files
        deployed_files = []
        missing_files = []
        
        for key, src_path in source_files.items():
            dest_path = deploy_path / src_path.name
            try:
                shutil.copy2(src_path, dest_path)
                deployed_files.append(str(dest_path))
            except Exception as e:
                return {
                    "success": False,
                    "message": f"Failed to copy {src_path.name}: {e}",
                    "deployed_files": deployed_files
                }
        
        # Check for missing optional files
        expected_files = ["Coral.Managed.dll", "Coral.Managed.runtimeconfig.json", "Coral.Managed.deps.json"]
        for filename in expected_files:
            if not (deploy_path / filename).exists():
                missing_files.append(filename)
        
        message = f"Successfully deployed {len(deployed_files)} file(s) to {deploy_path}"
        if missing_files:
            message += f"\nWarning: Missing files: {', '.join(missing_files)}"
        
        return {
            "success": True,
            "message": message,
            "deployed_files": deployed_files,
            "deploy_path": str(deploy_path)
        }
    
    def get_o3de_core_deploy_path(self) -> Path:
        """Get the path where O3DE.Core.dll should be deployed."""
        return self.project_path / "Bin" / "Scripts"
    
    def find_o3de_core_source_files(self) -> Dict[str, Path]:
        """
        Find the **newest** O3DE.Core build output files across all known
        build-output and staging locations.

        Returns:
            Dict mapping file type to Path, or empty dict if not found.
        """
        direct_dirs, rglob_roots = self._get_assembly_search_dirs()

        dll_path = self._find_latest_file("O3DE.Core.dll", direct_dirs, rglob_roots)
        if dll_path is None:
            return {}

        result: Dict[str, Path] = {"dll": dll_path}
        dll_dir = dll_path.parent
        for key, name in [("deps", "O3DE.Core.deps.json")]:
            companion = dll_dir / name
            if companion.is_file():
                result[key] = companion
            else:
                found = self._find_latest_file(name, direct_dirs, rglob_roots)
                if found:
                    result[key] = found

        return result
    
    def deploy_o3de_core(self, source_path: str = None) -> Dict[str, Any]:
        """
        Deploy O3DE.Core.dll to the project's Bin/Scripts directory.
        
        This copies O3DE.Core.dll (the C# scripting API) to the project's runtime 
        directory so the C# scripting system can load it.
        
        Args:
            source_path: Optional path to directory containing O3DE.Core files.
                         If not provided, will auto-detect from build output.
        
        Returns:
            Dict with 'success', 'message', and 'deployed_files' keys
        """
        import shutil
        
        deploy_path = self.get_o3de_core_deploy_path()
        
        # Find source files
        if source_path:
            source_dir = Path(source_path)
            if not source_dir.exists():
                return {
                    "success": False,
                    "message": f"Source path does not exist: {source_path}",
                    "deployed_files": []
                }
            
            # Build file list from provided path
            source_files = {}
            for key, filename in [("dll", "O3DE.Core.dll"), 
                                   ("deps", "O3DE.Core.deps.json")]:
                file_path = source_dir / filename
                if file_path.exists():
                    source_files[key] = file_path
        else:
            source_files = self.find_o3de_core_source_files()
        
        if not source_files:
            return {
                "success": False,
                "message": "Could not find O3DE.Core build output files. "
                          "Please build the O3DE.Core C# project first:\n"
                          "  cd <engine>/Gems/O3DESharp/Assets/Scripts/O3DE.Core\n"
                          "  dotnet build -c Release",
                "deployed_files": []
            }
        
        # Ensure at least the DLL is found
        if "dll" not in source_files:
            return {
                "success": False,
                "message": "O3DE.Core.dll not found in source location.",
                "deployed_files": []
            }
        
        # Create deploy directory
        try:
            deploy_path.mkdir(parents=True, exist_ok=True)
        except Exception as e:
            return {
                "success": False,
                "message": f"Failed to create deploy directory: {e}",
                "deployed_files": []
            }
        
        # Copy files
        deployed_files = []
        missing_files = []
        
        for key, src_path in source_files.items():
            dest_path = deploy_path / src_path.name
            try:
                shutil.copy2(src_path, dest_path)
                deployed_files.append(str(dest_path))
            except Exception as e:
                return {
                    "success": False,
                    "message": f"Failed to copy {src_path.name}: {e}",
                    "deployed_files": deployed_files
                }
        
        # Check for missing optional files
        expected_files = ["O3DE.Core.dll", "O3DE.Core.deps.json"]
        for filename in expected_files:
            if not (deploy_path / filename).exists():
                missing_files.append(filename)
        
        message = f"Successfully deployed {len(deployed_files)} file(s) to {deploy_path}"
        if missing_files:
            message += f"\nWarning: Missing files: {', '.join(missing_files)}"
        
        return {
            "success": True,
            "message": message,
            "deployed_files": deployed_files,
            "deploy_path": str(deploy_path)
        }
    
    def check_o3de_core_deployment(self) -> Dict[str, Any]:
        """
        Check if O3DE.Core is properly deployed to the project.
        
        Returns:
            Dict with 'deployed', 'path', and 'files' keys
        """
        deploy_path = self.get_o3de_core_deploy_path()
        
        required_files = [
            "O3DE.Core.dll"
        ]
        
        optional_files = [
            "O3DE.Core.deps.json"
        ]
        
        found_required = []
        missing_required = []
        found_optional = []
        
        for filename in required_files:
            if (deploy_path / filename).exists():
                found_required.append(filename)
            else:
                missing_required.append(filename)
        
        for filename in optional_files:
            if (deploy_path / filename).exists():
                found_optional.append(filename)
        
        is_deployed = len(missing_required) == 0
        
        return {
            "deployed": is_deployed,
            "path": str(deploy_path),
            "found_files": found_required + found_optional,
            "missing_files": missing_required,
            "message": "O3DE.Core is properly deployed" if is_deployed 
                      else f"Missing required files: {', '.join(missing_required)}"
        }

    def check_coral_deployment(self) -> Dict[str, Any]:
        """
        Check if Coral.Managed is properly deployed to the project.
        
        Returns:
            Dict with 'deployed', 'path', and 'files' keys
        """
        deploy_path = self.get_coral_deploy_path()
        
        required_files = [
            "Coral.Managed.dll",
            "Coral.Managed.runtimeconfig.json"
        ]
        
        optional_files = [
            "Coral.Managed.deps.json"
        ]
        
        found_required = []
        missing_required = []
        found_optional = []
        
        for filename in required_files:
            if (deploy_path / filename).exists():
                found_required.append(filename)
            else:
                missing_required.append(filename)
        
        for filename in optional_files:
            if (deploy_path / filename).exists():
                found_optional.append(filename)
        
        is_deployed = len(missing_required) == 0
        
        return {
            "deployed": is_deployed,
            "path": str(deploy_path),
            "found_files": found_required + found_optional,
            "missing_files": missing_required,
            "message": "Coral is properly deployed" if is_deployed 
                      else f"Missing required files: {', '.join(missing_required)}"
        }
    
    def create_project(self, 
                       project_name: str, 
                       namespace: str = None,
                       output_path: Path = None) -> Dict[str, Any]:
        """
        Create a new C# project for O3DE scripting.
        
        Args:
            project_name: Name of the project (e.g., "MyGame")
            namespace: Root namespace (defaults to project_name)
            output_path: Where to create the project (defaults to Assets/Scripts)
            
        Returns:
            Dict with 'success', 'message', and 'project_path' keys
        """
        # Ensure runtime dependencies are deployed first
        deploy_result = self.ensure_runtime_deployed()
        if not deploy_result["success"]:
            return {
                "success": False,
                "message": f"Failed to deploy runtime dependencies: {deploy_result['message']}",
                "project_path": None
            }
        
        if namespace is None:
            namespace = project_name
            
        if output_path is None:
            output_path = self.scripts_path
            
        project_dir = output_path / project_name
        
        try:
            # Create project directory
            project_dir.mkdir(parents=True, exist_ok=True)
            
            # Create .csproj file
            csproj_content = CSPROJ_TEMPLATE.format(
                o3de_core_path=self.o3de_core_path
            )
            csproj_path = project_dir / f"{project_name}.csproj"
            csproj_path.write_text(csproj_content)
            
            # Create a default script
            self.create_script(
                project_dir,
                "GameScript",
                namespace,
                "ScriptComponent",
                "Default game script component"
            )
            
            # Create project metadata
            metadata = {
                "name": project_name,
                "namespace": namespace,
                "version": "1.0.0",
                "scripts": ["GameScript.cs"]
            }
            metadata_path = project_dir / "project.json"
            metadata_path.write_text(json.dumps(metadata, indent=2))
            
            return {
                "success": True,
                "message": f"Created C# project '{project_name}' at {project_dir}",
                "project_path": str(project_dir),
                "csproj_path": str(csproj_path)
            }
            
        except Exception as e:
            return {
                "success": False,
                "message": f"Failed to create project: {str(e)}",
                "project_path": None
            }
    
    def create_script(self,
                      project_path: Path,
                      class_name: str,
                      namespace: str,
                      template_type: str = "ScriptComponent",
                      description: str = "") -> Dict[str, Any]:
        """
        Create a new C# script file.
        
        Args:
            project_path: Path to the C# project directory
            class_name: Name of the class to create
            namespace: Namespace for the class
            template_type: Type of template ('ScriptComponent', 'EmptyClass', 'StaticClass')
            description: Description for the class documentation
            
        Returns:
            Dict with 'success', 'message', and 'file_path' keys
        """
        if template_type not in self.SCRIPT_TEMPLATES:
            return {
                "success": False,
                "message": f"Unknown template type: {template_type}",
                "file_path": None
            }
        
        template_info = self.SCRIPT_TEMPLATES[template_type]
        
        try:
            project_path = Path(project_path)
            script_path = project_path / f"{class_name}.cs"
            
            if script_path.exists():
                return {
                    "success": False,
                    "message": f"Script already exists: {script_path}",
                    "file_path": None
                }
            
            content = template_info['template'].format(
                namespace=namespace,
                class_name=class_name,
                description=description or f"{class_name} class"
            )
            
            script_path.write_text(content)
            
            # Update project metadata
            self._update_project_metadata(project_path, class_name)
            
            return {
                "success": True,
                "message": f"Created script '{class_name}.cs'",
                "file_path": str(script_path)
            }
            
        except Exception as e:
            return {
                "success": False,
                "message": f"Failed to create script: {str(e)}",
                "file_path": None
            }
    
    def _update_project_metadata(self, project_path: Path, class_name: str):
        """Update project.json with new script."""
        metadata_path = project_path / "project.json"
        if metadata_path.exists():
            metadata = json.loads(metadata_path.read_text())
            if "scripts" not in metadata:
                metadata["scripts"] = []
            script_file = f"{class_name}.cs"
            if script_file not in metadata["scripts"]:
                metadata["scripts"].append(script_file)
            metadata_path.write_text(json.dumps(metadata, indent=2))
    
    def build_project(self, project_path: Path, configuration: str = "Release") -> Dict[str, Any]:
        """
        Build a C# project using dotnet CLI.
        
        Args:
            project_path: Path to the project directory
            configuration: Build configuration ('Debug' or 'Release')
            
        Returns:
            Dict with 'success', 'message', and 'output_path' keys
        """
        # Ensure runtime dependencies are deployed first
        deploy_result = self.ensure_runtime_deployed()
        if not deploy_result["success"]:
            return {
                "success": False,
                "message": f"Failed to deploy runtime dependencies: {deploy_result['message']}",
                "output_path": None
            }
        
        project_path = Path(project_path)
        
        # Find .csproj file
        csproj_files = list(project_path.glob("*.csproj"))
        if not csproj_files:
            return {
                "success": False,
                "message": "No .csproj file found in project directory",
                "output_path": None
            }
        
        csproj_path = csproj_files[0]

        try:
            # Phase 16b: set the BuildInProgress flag so the editor's file watcher
            # (Phase 16a) coalesces every intermediate write during this build into
            # a single reload at the end. The watcher polls this flag on each
            # debounce expiry and reschedules itself while it's set. Best-effort -
            # the settings registry might not exist in non-editor contexts.
            _set_build_in_progress(True)
            try:
                result = subprocess.run(
                    ["dotnet", "build", str(csproj_path), "-c", configuration],
                    capture_output=True,
                    text=True,
                    cwd=str(project_path)
                )
            finally:
                _set_build_in_progress(False)

            if result.returncode == 0:
                output_path = project_path / "bin" / configuration
                dll_name = csproj_path.stem + ".dll"
                dll_path = output_path / dll_name

                # Deploy the built assembly to <ProjectPath>/Bin/Scripts/ so
                # the Coral runtime's UserAssemblyVisitor can find it (the
                # visitor prepends <ProjectPath>/Bin/Scripts/ to each
                # configured AssemblyName, with that fixed path coming from
                # O3DESharpSystemComponent::InitializeCoralHost). Without this
                # copy, the .csproj's bin/Release output never reaches the
                # runtime and CSharpScriptComponent::CreateScriptInstance logs
                # "Script class not found".
                deployed_path = None
                if dll_path.exists():
                    deploy_dir = self.project_path / "Bin" / "Scripts"
                    deploy_dir.mkdir(parents=True, exist_ok=True)
                    deployed_path = deploy_dir / dll_name
                    try:
                        import shutil
                        shutil.copy2(str(dll_path), str(deployed_path))
                        # Also bring along the .pdb if present so the Coral
                        # log surfaces line numbers in stack traces.
                        pdb_path = dll_path.with_suffix(".pdb")
                        if pdb_path.exists():
                            shutil.copy2(str(pdb_path), str(deploy_dir / pdb_path.name))
                    except OSError as deploy_err:
                        return {
                            "success": True,
                            "message": (
                                f"Build succeeded but deploy to {deploy_dir} failed: {deploy_err}. "
                                f"Copy {dll_path} there manually so Coral can load it."
                            ),
                            "output_path": str(dll_path),
                            "build_output": result.stdout,
                        }

                return {
                    "success": True,
                    "message": "Build succeeded",
                    "output_path": str(deployed_path) if deployed_path else (
                        str(dll_path) if dll_path.exists() else str(output_path)
                    ),
                    "build_output": result.stdout
                }
            else:
                return {
                    "success": False,
                    "message": f"Build failed",
                    "output_path": None,
                    "build_output": result.stdout + "\n" + result.stderr
                }
                
        except FileNotFoundError:
            return {
                "success": False,
                "message": "dotnet CLI not found. Please install .NET 8.0 SDK.",
                "output_path": None
            }
        except Exception as e:
            return {
                "success": False,
                "message": f"Build error: {str(e)}",
                "output_path": None
            }
    
    def find_unmigrated_csprojs(self) -> List[Path]:
        """
        Walk the project for .csproj files that don't yet have the Phase 16b
        deploy target. Returns a list of Paths to surface in the editor log
        on startup so users discover the Migrate command without having to
        notice that auto-reload isn't kicking in for their IDE builds.

        Search root and skip rules mirror migrate_csprojs_to_deploy_target.
        """
        unmigrated: List[Path] = []
        search_locations = [self.gem_path]
        if self.scripts_path != self.gem_path and self.scripts_path.exists():
            search_locations.append(self.scripts_path)

        seen: set = set()
        for search_root in search_locations:
            if not search_root.exists():
                continue
            for csproj in search_root.rglob("*.csproj"):
                if "bin" in csproj.parts or "obj" in csproj.parts:
                    continue
                if str(csproj) in seen:
                    continue
                seen.add(str(csproj))
                try:
                    content = csproj.read_text(encoding='utf-8')
                except OSError:
                    continue
                if _DEPLOY_TARGET_MARKER not in content:
                    unmigrated.append(csproj)
        return unmigrated

    def migrate_csprojs_to_deploy_target(self) -> Dict[str, Any]:
        """
        Walk the project tree, find every user .csproj, and inject the Phase
        16b auto-deploy target into any that don't already have it. Used by
        Tools > C# Scripting > Migrate C# Project Files.

        Returns a dict summarising which files were migrated vs skipped vs
        failed. Honored by the editor menu, which surfaces a single Qt
        message box rather than spamming the log.
        """
        migrated: List[str] = []
        skipped: List[str] = []
        failed: List[Dict[str, str]] = []

        # Search both standard locations - same set list_projects uses.
        search_locations = [self.gem_path]
        if self.scripts_path != self.gem_path and self.scripts_path.exists():
            search_locations.append(self.scripts_path)

        seen: set = set()
        for search_root in search_locations:
            if not search_root.exists():
                continue
            for csproj in search_root.rglob("*.csproj"):
                # Skip build outputs and dedupe across the two roots.
                if "bin" in csproj.parts or "obj" in csproj.parts:
                    continue
                if str(csproj) in seen:
                    continue
                seen.add(str(csproj))

                result = migrate_csproj_to_deploy_target(csproj)
                if not result["success"]:
                    failed.append({"path": str(csproj), "message": result["message"]})
                elif result["changed"]:
                    migrated.append(str(csproj))
                else:
                    skipped.append(str(csproj))

        return {
            "success": len(failed) == 0,
            "migrated": migrated,
            "skipped": skipped,
            "failed": failed,
            "summary": (
                f"Migrated {len(migrated)} csproj(s), "
                f"skipped {len(skipped)} already-migrated, "
                f"{len(failed)} failed"
            ),
        }

    def list_projects(self) -> List[Dict[str, Any]]:
        """
        List all C# projects by searching the Gem directory recursively.

        Searches for .csproj files within the project's Gem directory structure.
        """
        projects = []
        found_paths = set()  # Avoid duplicates
        
        # Search locations: project's Gem directory and the default scripts path
        search_locations = [self.gem_path]
        if self.scripts_path != self.gem_path and self.scripts_path.exists():
            search_locations.append(self.scripts_path)
        
        for search_root in search_locations:
            if not search_root.exists():
                continue
                
            # Recursively find all .csproj files
            for csproj_file in search_root.rglob("*.csproj"):
                project_dir = csproj_file.parent
                
                # Skip if we've already found this project
                if str(project_dir) in found_paths:
                    continue
                found_paths.add(str(project_dir))
                
                # Skip build output directories
                if "bin" in project_dir.parts or "obj" in project_dir.parts:
                    continue
                
                # Load project metadata if available
                metadata_path = project_dir / "project.json"
                metadata = {}
                if metadata_path.exists():
                    try:
                        metadata = json.loads(metadata_path.read_text())
                    except:
                        pass
                
                projects.append({
                    "name": csproj_file.stem,
                    "path": str(project_dir),
                    "csproj": str(csproj_file),
                    "namespace": metadata.get("namespace", csproj_file.stem),
                    "scripts": metadata.get("scripts", [])
                })
        
        return projects
    
    def list_scripts(self, project_path: Path) -> List[Dict[str, Any]]:
        """
        List all C# scripts in a project with intelligent caching.
        
        Uses in-memory cache based on file modification times to avoid
        re-parsing files that haven't changed.
        """
        import time
        project_path = Path(project_path)
        project_key = str(project_path)
        
        # Check project-level cache
        current_time = time.time()
        if project_key in self._project_scripts_cache:
            cache_time, cached_scripts = self._project_scripts_cache[project_key]
            if (current_time - cache_time) < self._cache_ttl:
                # Check if any files have been modified
                cache_valid = True
                for cs_file in project_path.glob("*.cs"):
                    file_key = str(cs_file)
                    if file_key in self._script_cache:
                        cached_mtime, _ = self._script_cache[file_key]
                        try:
                            current_mtime = cs_file.stat().st_mtime
                            if current_mtime != cached_mtime:
                                cache_valid = False
                                break
                        except:
                            cache_valid = False
                            break
                    else:
                        # New file found
                        cache_valid = False
                        break
                
                if cache_valid:
                    return cached_scripts
        
        # Cache miss or invalid - rebuild
        scripts = []
        for cs_file in project_path.glob("*.cs"):
            file_key = str(cs_file)
            try:
                current_mtime = cs_file.stat().st_mtime
            except:
                continue
            
            # Check file-level cache
            if file_key in self._script_cache:
                cached_mtime, cached_data = self._script_cache[file_key]
                if cached_mtime == current_mtime:
                    # File unchanged, use cached data
                    scripts.append(cached_data)
                    continue
            
            # Parse file (not in cache or modified)
            try:
                content = cs_file.read_text(encoding='utf-8', errors='ignore')
                class_name = cs_file.stem
                namespace = self._extract_namespace(content)
                base_class = self._extract_base_class(content, class_name)
                
                script_data = {
                    "file_name": cs_file.name,
                    "class_name": class_name,
                    "full_name": f"{namespace}.{class_name}" if namespace else class_name,
                    "namespace": namespace,
                    "base_class": base_class,
                    "is_script_component": base_class == "ScriptComponent",
                    "file_path": str(cs_file),  # Changed from 'path' to 'file_path' for consistency
                    "path": str(cs_file)  # Keep for backward compatibility
                }
                
                # Update file-level cache
                self._script_cache[file_key] = (current_mtime, script_data)
                scripts.append(script_data)
                
            except Exception as e:
                print(f"[O3DESharp] Error parsing {cs_file.name}: {e}")
                continue
        
        # Update project-level cache
        self._project_scripts_cache[project_key] = (current_time, scripts)
        
        return scripts
    
    def invalidate_cache(self, project_path: Path = None):
        """
        Invalidate the script discovery cache.
        
        Args:
            project_path: Optional specific project to invalidate. If None, clears all caches.
        """
        if project_path is None:
            self._script_cache.clear()
            self._project_scripts_cache.clear()
            print("[O3DESharp] Cleared all script caches")
        else:
            project_key = str(Path(project_path))
            if project_key in self._project_scripts_cache:
                del self._project_scripts_cache[project_key]
            
            # Also clear file-level cache for this project
            project_path = Path(project_path)
            for cs_file in project_path.glob("*.cs"):
                file_key = str(cs_file)
                if file_key in self._script_cache:
                    del self._script_cache[file_key]
            
            print(f"[O3DESharp] Cleared cache for project: {project_path}")
    
    def _extract_namespace(self, content: str) -> Optional[str]:
        """Extract namespace from C# file content."""
        import re
        match = re.search(r'namespace\s+([\w.]+)', content)
        return match.group(1) if match else None
    
    def _extract_base_class(self, content: str, class_name: str) -> Optional[str]:
        """Extract base class from C# file content."""
        import re
        pattern = rf'class\s+{class_name}\s*:\s*(\w+)'
        match = re.search(pattern, content)
        return match.group(1) if match else None
    
    def get_available_script_classes(self) -> List[str]:
        """
        Get all available C# script component classes across all projects.
        
        Scans all C# projects and returns a list of fully qualified class names
        (namespace.classname) for classes that inherit from ScriptComponent.
        
        Returns:
            List of fully qualified class names (e.g., ["MyGame.PlayerController", "MyGame.EnemyAI"])
        """
        script_classes = []
        
        try:
            projects = self.list_projects()
            
            for project in projects:
                project_path = Path(project["path"])
                scripts = self.list_scripts(project_path)
                
                for script in scripts:
                    # Only include classes that inherit from ScriptComponent
                    if script.get("is_script_component", False):
                        full_name = script.get("full_name", "")
                        if full_name:
                            script_classes.append(full_name)
            
            # Sort alphabetically for better UX
            script_classes.sort()
            
        except Exception as e:
            print(f"Error scanning for script classes: {e}")
        
        return script_classes
    
    def get_script_class_info(self, full_class_name: str) -> Optional[Dict[str, Any]]:
        """
        Get detailed information about a specific script class.
        
        Args:
            full_class_name: Fully qualified class name (e.g., "MyGame.PlayerController")
        
        Returns:
            Dict with class info including path, namespace, base_class, etc.
            Returns None if class not found.
        """
        try:
            projects = self.list_projects()
            
            for project in projects:
                project_path = Path(project["path"])
                scripts = self.list_scripts(project_path)
                
                for script in scripts:
                    if script.get("full_name") == full_class_name:
                        # Add project info to the script data
                        script["project_name"] = project["name"]
                        script["project_path"] = project["path"]
                        return script
        except Exception as e:
            print(f"Error looking up script class: {e}")
        
        return None


def get_project_manager() -> CSharpProjectManager:
    """Get the singleton project manager instance."""
    return CSharpProjectManager()
