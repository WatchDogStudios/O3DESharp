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
import subprocess
from pathlib import Path
from typing import Optional, List, Dict, Any

import azlmbr.bus as bus
import azlmbr.editor as editor
import azlmbr.paths as paths

# Templates for C# project files
CSPROJ_TEMPLATE = '''<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <OutputPath>bin/$(Configuration)</OutputPath>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
  </PropertyGroup>

  <ItemGroup>
    <Reference Include="O3DE.Core">
      <HintPath>{o3de_core_path}</HintPath>
    </Reference>
  </ItemGroup>

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
        self.managed_assembly_path = self._get_managed_assembly_path()
        self.user_assembly_path = self._get_user_assembly_path()
        # Alias for backward compatibility
        self.o3de_core_path = self.managed_assembly_path
        
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
    
    def _get_managed_assembly_path(self) -> str:
        """
        Get the path to the managed assembly (Coral.Managed/O3DE.Core.dll).
        
        Checks settings first, then auto-detects from common locations.
        """
        # Check if user has configured a custom path
        custom_path = self._settings.get("managed_assembly_path")
        if custom_path:
            path = Path(custom_path)
            if path.exists():
                return str(path)
        
        engine_root = Path(paths.engroot)
        
        # Try multiple possible locations for the managed assembly
        possible_paths = [
            # CMake staging directory (created by O3DESharp.StageCoral target)
            engine_root / "Gems" / "O3DESharp" / "bin" / "Coral" / "Coral.Managed.dll",
            # Coral.Managed locations
            engine_root / "Gems" / "O3DESharp" / "External" / "Coral" / "Coral.Managed" / "bin" / "Release" / "net8.0" / "Coral.Managed.dll",
            engine_root / "Gems" / "O3DESharp" / "bin" / "Coral.Managed.dll",
            # O3DE.Core locations (legacy)
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
    
    def get_managed_assembly_path(self) -> str:
        """Get the current managed assembly path."""
        return self.managed_assembly_path
    
    def set_managed_assembly_path(self, path: str) -> Dict[str, Any]:
        """
        Set a custom path for the managed assembly.
        
        Args:
            path: Path to Coral.Managed.dll or O3DE.Core.dll
            
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
        
        self._settings["managed_assembly_path"] = str(path_obj.resolve())
        self._save_settings()
        self.managed_assembly_path = str(path_obj.resolve())
        self.o3de_core_path = self.managed_assembly_path
        
        # Update the settings registry for C++ code
        self._update_settings_registry()
        
        return {
            "success": True,
            "message": f"Managed assembly path set to: {path}"
        }
    
    def clear_managed_assembly_path(self):
        """Clear the custom managed assembly path and revert to auto-detection."""
        if "managed_assembly_path" in self._settings:
            del self._settings["managed_assembly_path"]
            self._save_settings()
        self.managed_assembly_path = self._get_managed_assembly_path()
        self.o3de_core_path = self.managed_assembly_path
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
            
            # Add Core API path if customized
            if "managed_assembly_path" in self._settings:
                settings["O3DE"]["O3DESharp"]["CoreApiAssemblyPath"] = self._settings["managed_assembly_path"]
            
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
    
    def find_coral_source_files(self) -> Dict[str, Path]:
        """
        Find Coral.Managed build output files from various possible locations.
        
        Returns:
            Dict mapping file type to Path, or empty dict if not found
        """
        engine_root = Path(paths.engroot)
        
        # Possible locations for Coral.Managed build output
        # Priority: CMake staging dir > User-configured path > Build output > Source location
        search_roots = [
            # CMake staging directory (created by O3DESharp.StageCoral target)
            engine_root / "Gems" / "O3DESharp" / "bin" / "Coral",
            # CMake build output directory (FetchContent places it in _deps)
            engine_root / "build",
            engine_root / "out",
            # Direct gem location
            engine_root / "Gems" / "O3DESharp" / "External" / "Coral",
            engine_root / "Gems" / "O3DESharp" / "bin",
            # User-configured path directory
        ]
        
        # If user has configured a managed assembly path, check its directory too
        if self.managed_assembly_path:
            managed_dir = Path(self.managed_assembly_path).parent
            search_roots.insert(0, managed_dir)
        
        # Files we need to find
        required_files = {
            "dll": "Coral.Managed.dll",
            "runtimeconfig": "Coral.Managed.runtimeconfig.json",
            "deps": "Coral.Managed.deps.json"
        }
        
        found_files = {}
        
        for root in search_roots:
            if not root.exists():
                continue
            
            # First check if files are directly in this directory
            all_found_direct = True
            for key, filename in required_files.items():
                file_path = root / filename
                if file_path.exists():
                    found_files[key] = file_path
                else:
                    all_found_direct = False
            
            if all_found_direct and "dll" in found_files:
                return found_files
            found_files.clear()
            
            # Search recursively for Coral.Managed.dll
            for dll_path in root.rglob("Coral.Managed.dll"):
                dll_dir = dll_path.parent
                
                # Check if all required files are in the same directory
                all_found = True
                for key, filename in required_files.items():
                    file_path = dll_dir / filename
                    if file_path.exists():
                        found_files[key] = file_path
                    else:
                        all_found = False
                        break
                
                if all_found:
                    return found_files
                found_files.clear()
        
        return found_files
    
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
        Find O3DE.Core build output files from various possible locations.
        
        Returns:
            Dict mapping file type to Path, or empty dict if not found
        """
        engine_root = Path(paths.engroot)
        
        # Possible locations for O3DE.Core build output
        # Priority: CMake staging dir > Release build > Debug build
        search_locations = [
            # CMake staging directory (created by O3DESharp.StageO3DECore target)
            engine_root / "Gems" / "O3DESharp" / "bin" / "O3DE.Core",
            # Direct C# build output - Release
            engine_root / "Gems" / "O3DESharp" / "Assets" / "Scripts" / "O3DE.Core" / "bin" / "Release" / "net8.0",
            # Direct C# build output - Debug  
            engine_root / "Gems" / "O3DESharp" / "Assets" / "Scripts" / "O3DE.Core" / "bin" / "Debug" / "net8.0",
            # Alternative net9.0 targets
            engine_root / "Gems" / "O3DESharp" / "Assets" / "Scripts" / "O3DE.Core" / "bin" / "Release" / "net9.0",
            engine_root / "Gems" / "O3DESharp" / "Assets" / "Scripts" / "O3DE.Core" / "bin" / "Debug" / "net9.0",
        ]
        
        # Files we need to find
        required_files = {
            "dll": "O3DE.Core.dll",
            "deps": "O3DE.Core.deps.json"
        }
        
        found_files = {}
        
        for search_root in search_locations:
            if not search_root.exists():
                continue
            
            # Check for files directly in this location
            all_found = True
            for key, filename in required_files.items():
                file_path = search_root / filename
                if file_path.exists():
                    found_files[key] = file_path
                else:
                    all_found = False
                    break
            
            if all_found:
                return found_files
            found_files.clear()
        
        return found_files
    
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
            result = subprocess.run(
                ["dotnet", "build", str(csproj_path), "-c", configuration],
                capture_output=True,
                text=True,
                cwd=str(project_path)
            )
            
            if result.returncode == 0:
                output_path = project_path / "bin" / configuration
                dll_name = csproj_path.stem + ".dll"
                dll_path = output_path / dll_name
                
                return {
                    "success": True,
                    "message": f"Build succeeded",
                    "output_path": str(dll_path) if dll_path.exists() else str(output_path),
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
        
        return projects
    
    def list_scripts(self, project_path: Path) -> List[Dict[str, Any]]:
        """List all C# scripts in a project."""
        scripts = []
        project_path = Path(project_path)
        
        for cs_file in project_path.glob("*.cs"):
            # Parse the file to extract class info
            content = cs_file.read_text()
            class_name = cs_file.stem
            namespace = self._extract_namespace(content)
            base_class = self._extract_base_class(content, class_name)
            
            scripts.append({
                "file_name": cs_file.name,
                "class_name": class_name,
                "full_name": f"{namespace}.{class_name}" if namespace else class_name,
                "namespace": namespace,
                "base_class": base_class,
                "is_script_component": base_class == "ScriptComponent",
                "path": str(cs_file)
            })
        
        return scripts
    
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


def get_project_manager() -> CSharpProjectManager:
    """Get the singleton project manager instance."""
    return CSharpProjectManager()
