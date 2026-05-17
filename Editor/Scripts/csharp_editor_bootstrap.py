"""
Copyright (c) Contributors to the Open 3D Engine Project.
For complete copyright and license terms please see the LICENSE at the root of this distribution.

SPDX-License-Identifier: Apache-2.0 OR MIT

O3DESharp Editor Bootstrap - Registers C# scripting tools in the Editor menus
"""

import sys
import os
import re
from pathlib import Path

import azlmbr.bus as bus
import azlmbr.editor as editor
import azlmbr.legacy.general as general

# Ensure this script's directory is in sys.path for imports
_SCRIPT_DIR = Path(__file__).parent.resolve()
if str(_SCRIPT_DIR) not in sys.path:
    sys.path.insert(0, str(_SCRIPT_DIR))

# Try to import PySide if available
try:
    from PySide2 import QtWidgets
    HAS_PYSIDE = True
except ImportError:
    try:
        from PySide6 import QtWidgets
        HAS_PYSIDE = True
    except ImportError:
        HAS_PYSIDE = False


def _import_csharp_editor_tools():
    """Import csharp_editor_tools module, handling different import contexts."""
    try:
        # Try relative import first (when running as part of a package)
        from . import csharp_editor_tools
        return csharp_editor_tools
    except ImportError:
        pass
    
    # Fall back to direct import (when script dir is in sys.path)
    import csharp_editor_tools
    return csharp_editor_tools


def _import_csharp_project_manager():
    """Import csharp_project_manager module, handling different import contexts."""
    try:
        # Try relative import first (when running as part of a package)
        from . import csharp_project_manager
        return csharp_project_manager
    except ImportError:
        pass
    
    # Fall back to direct import (when script dir is in sys.path)
    import csharp_project_manager
    return csharp_project_manager


def open_csharp_project_manager():
    """Opens the C# Project Manager window"""
    if not HAS_PYSIDE:
        general.log("PySide2/PySide6 not available - cannot open C# Project Manager")
        return
    
    try:
        csharp_editor_tools = _import_csharp_editor_tools()
        
        # Get the main editor window as parent
        main_window = None
        for widget in QtWidgets.QApplication.topLevelWidgets():
            if isinstance(widget, QtWidgets.QMainWindow):
                main_window = widget
                break
        
        dialog = csharp_editor_tools.CSharpProjectManagerWindow(main_window)
        dialog.show()
        
    except ImportError as e:
        general.log(f"Failed to import C# editor tools: {e}")
    except Exception as e:
        general.log(f"Failed to open C# Project Manager: {e}")


def create_csharp_project():
    """Opens the Create C# Project dialog"""
    if not HAS_PYSIDE:
        general.log("PySide2/PySide6 not available - cannot create C# project")
        return
    
    try:
        csharp_editor_tools = _import_csharp_editor_tools()
        
        main_window = None
        for widget in QtWidgets.QApplication.topLevelWidgets():
            if isinstance(widget, QtWidgets.QMainWindow):
                main_window = widget
                break
        
        dialog = csharp_editor_tools.CreateProjectDialog(main_window)
        if dialog.exec_():
            project_path = dialog.get_project_path()
            if project_path:
                general.log(f"Created C# project at: {project_path}")
                
    except ImportError as e:
        general.log(f"Failed to import C# editor tools: {e}")
    except Exception as e:
        general.log(f"Failed to create C# project: {e}")


def create_csharp_script():
    """Opens the Create C# Script dialog"""
    if not HAS_PYSIDE:
        general.log("PySide2/PySide6 not available - cannot create C# script")
        return
    
    try:
        csharp_editor_tools = _import_csharp_editor_tools()
        
        main_window = None
        for widget in QtWidgets.QApplication.topLevelWidgets():
            if isinstance(widget, QtWidgets.QMainWindow):
                main_window = widget
                break
        
        dialog = csharp_editor_tools.CreateScriptDialog(main_window)
        if dialog.exec_():
            class_name = dialog.get_created_class_name()
            if class_name:
                general.log(f"Created C# script: {class_name}")
                
    except ImportError as e:
        general.log(f"Failed to import C# editor tools: {e}")
    except Exception as e:
        general.log(f"Failed to create C# script: {e}")


def browse_csharp_scripts():
    """Opens the C# Script Browser dialog"""
    if not HAS_PYSIDE:
        general.log("PySide2/PySide6 not available - cannot browse C# scripts")
        return
    
    try:
        csharp_editor_tools = _import_csharp_editor_tools()
        
        main_window = None
        for widget in QtWidgets.QApplication.topLevelWidgets():
            if isinstance(widget, QtWidgets.QMainWindow):
                main_window = widget
                break
        
        dialog = csharp_editor_tools.ScriptBrowserDialog(main_window)
        if dialog.exec_():
            class_name = dialog.get_selected_class()
            if class_name:
                general.log(f"Selected C# script: {class_name}")
                return class_name
                
    except ImportError as e:
        general.log(f"Failed to import C# editor tools: {e}")
    except Exception as e:
        general.log(f"Failed to browse C# scripts: {e}")
    
    return None


def pick_script_class(current_class: str = ""):
    """Opens the ScriptClassPickerDialog for entity-component usage.
    
    Args:
        current_class: The currently assigned class name (used to pre-select).
    
    Returns:
        The fully qualified class name chosen, empty string for "clear", or None
        if the dialog was cancelled.
    """
    if not HAS_PYSIDE:
        general.log("PySide2/PySide6 not available - cannot pick script class")
        return None
    
    try:
        csharp_editor_tools = _import_csharp_editor_tools()
        
        main_window = None
        for widget in QtWidgets.QApplication.topLevelWidgets():
            if isinstance(widget, QtWidgets.QMainWindow):
                main_window = widget
                break
        
        dialog = csharp_editor_tools.ScriptClassPickerDialog(
            current_class=current_class, parent=main_window
        )
        if dialog.exec_():
            selected = dialog.get_selected_class()
            if selected is not None:
                general.log(f"Picked C# script class: {selected}")
                return selected
    except ImportError as e:
        general.log(f"Failed to import C# editor tools: {e}")
    except Exception as e:
        general.log(f"Failed to pick script class: {e}")
    
    return None


def show_quick_actions():
    """Opens the command-palette-style Quick Action Prompt.
    
    Dispatches the chosen action to the appropriate function.
    """
    if not HAS_PYSIDE:
        general.log("PySide2/PySide6 not available - cannot show quick actions")
        return
    
    try:
        csharp_editor_tools = _import_csharp_editor_tools()
        
        main_window = None
        for widget in QtWidgets.QApplication.topLevelWidgets():
            if isinstance(widget, QtWidgets.QMainWindow):
                main_window = widget
                break
        
        dialog = csharp_editor_tools.QuickActionPrompt(main_window)
        if dialog.exec_():
            action_id = dialog.get_chosen_action()
            _dispatch_quick_action(action_id)
    except ImportError as e:
        general.log(f"Failed to import C# editor tools: {e}")
    except Exception as e:
        general.log(f"Failed to show quick actions: {e}")


def _dispatch_quick_action(action_id: str):
    """Route a Quick Action choice to the matching function."""
    dispatch = {
        "create_project":    create_csharp_project,
        "create_script":     create_csharp_script,
        "browse_scripts":    browse_csharp_scripts,
        "pick_class":        pick_script_class,
        "build_all":         build_csharp_projects,
        "open_manager":      open_csharp_project_manager,
        "generate_bindings": generate_bindings,
        "deploy_all":        _deploy_all_shortcut,
        "refresh_classes":   _refresh_class_cache,
    }
    fn = dispatch.get(action_id)
    if fn:
        fn()
    else:
        general.log(f"Unknown quick-action: {action_id}")


def _deploy_all_shortcut():
    """Deploy Coral + O3DE.Core runtime files via the project manager."""
    try:
        csharp_project_manager = _import_csharp_project_manager()
        manager = csharp_project_manager.get_project_manager()
        
        coral_result = manager.deploy_coral()
        core_result = manager.deploy_o3de_core()
        
        if coral_result["success"]:
            general.log(f"Coral deployed: {coral_result['message']}")
        else:
            general.log(f"Coral deploy failed: {coral_result['message']}")
        
        if core_result["success"]:
            general.log(f"O3DE.Core deployed: {core_result['message']}")
        else:
            general.log(f"O3DE.Core deploy failed: {core_result['message']}")
    except Exception as e:
        general.log(f"Deploy all failed: {e}")


def _refresh_class_cache():
    """Clear and rebuild the script class recent-usage cache."""
    try:
        csharp_editor_tools = _import_csharp_editor_tools()
        csharp_editor_tools._RecentClassesCache.instance().clear()
        general.log("Script class cache cleared")
    except Exception as e:
        general.log(f"Failed to refresh class cache: {e}")


_MSBUILD_ERROR_RE = re.compile(
    r"^(?P<file>.+?)\((?P<line>\d+),(?P<col>\d+)\):\s*"
    r"error(?:\s+(?P<code>[A-Z]+\d+))?:\s*(?P<msg>.+?)\s*$"
)
_MSBUILD_WARN_RE = re.compile(
    r"^(?P<file>.+?)\((?P<line>\d+),(?P<col>\d+)\):\s*"
    r"warning(?:\s+(?P<code>[A-Z]+\d+))?:\s*(?P<msg>.+?)\s*$"
)


def _surface_dotnet_build_output(project_label, build_output):
    """
    Parse `dotnet build` stdout/stderr for MSBuild-formatted error and warning
    lines and re-emit them through the O3DE editor log so the user sees compile
    failures alongside the rest of the editor's diagnostics rather than only
    inside the C# Project Manager dialog (which the editor console doesn't see).

    Lines that don't match either pattern are skipped to keep the output
    actionable.
    """
    if not build_output:
        return
    error_count = 0
    warning_count = 0
    for raw_line in build_output.splitlines():
        line = raw_line.strip()
        m = _MSBUILD_ERROR_RE.match(line)
        if m:
            error_count += 1
            code = m.group("code") or "error"
            general.log(
                f"[O3DESharp][ERROR][{project_label}] {m.group('file')}({m.group('line')},{m.group('col')}): {code}: {m.group('msg')}"
            )
            continue
        m = _MSBUILD_WARN_RE.match(line)
        if m:
            warning_count += 1
            code = m.group("code") or "warning"
            general.log(
                f"[O3DESharp][warn][{project_label}] {m.group('file')}({m.group('line')},{m.group('col')}): {code}: {m.group('msg')}"
            )
    if error_count or warning_count:
        general.log(
            f"[O3DESharp] {project_label}: parsed {error_count} error(s), {warning_count} warning(s) from dotnet build output"
        )


def build_csharp_projects():
    """Builds all C# projects in the current project"""
    try:
        csharp_project_manager = _import_csharp_project_manager()

        manager = csharp_project_manager.CSharpProjectManager()
        projects = manager.list_projects()

        if not projects:
            general.log("No C# projects found to build")
            return

        success_count = 0
        fail_count = 0

        for project_path in projects:
            label = str(project_path)
            general.log(f"Building: {project_path}")

            # build_project returns a dict; the previous `if manager.build_project(...)`
            # check evaluated dict truthiness (always True) and so silently
            # reported failed builds as successful.
            result = manager.build_project(project_path)
            ok = bool(result.get("success"))

            # Always surface compile output to the editor console - both on
            # failure (so the user can act on the error) and on success-with-
            # warnings.
            _surface_dotnet_build_output(label, result.get("build_output", ""))

            if ok:
                success_count += 1
                general.log(f"  Build succeeded: {result.get('output_path', '')}")
            else:
                fail_count += 1
                general.log(f"  Build FAILED: {result.get('message', 'unknown reason')}")

        general.log(f"Build complete: {success_count} succeeded, {fail_count} failed")

    except ImportError as e:
        general.log(f"Failed to import C# project manager: {e}")
    except Exception as e:
        general.log(f"Failed to build C# projects: {e}")


def warn_unmigrated_csharp_projects():
    """
    Surface a one-time warning per editor session for each .csproj missing
    the Phase 16b auto-deploy target. Called from initialize_ebus_handler
    so the user gets the heads-up at startup instead of finding out their
    IDE builds aren't auto-reloading through trial and error.

    Idempotent and silent when everything is already migrated.
    """
    try:
        csharp_project_manager = _import_csharp_project_manager()
        manager = csharp_project_manager.CSharpProjectManager()
        unmigrated = manager.find_unmigrated_csprojs()
        if not unmigrated:
            return
        general.log(
            f"[O3DESharp] {len(unmigrated)} C# project file(s) lack the auto-deploy "
            f"MSBuild target. IDE builds (Rider / VS) won't auto-reload until you run "
            f"Tools > C# Scripting > Migrate C# Project Files."
        )
        for csproj in unmigrated:
            general.log(f"  unmigrated: {csproj}")
    except Exception as e:  # noqa: BLE001 - non-fatal background check
        general.log(f"[O3DESharp] Unmigrated-csproj check failed (non-fatal): {e}")


def migrate_csharp_projects_to_deploy_target():
    """
    Add the Phase 16b auto-deploy MSBuild target to every user .csproj in
    the project that doesn't already have it. Called from
    Tools > C# Scripting > Migrate C# Project Files. Idempotent - re-running
    on an already-migrated project is a no-op.
    """
    try:
        csharp_project_manager = _import_csharp_project_manager()
        manager = csharp_project_manager.CSharpProjectManager()
        result = manager.migrate_csprojs_to_deploy_target()

        general.log(result.get("summary", "Migration finished"))
        for path in result.get("migrated", []):
            general.log(f"  migrated: {path}")
        for path in result.get("skipped", []):
            general.log(f"  already up-to-date: {path}")
        for entry in result.get("failed", []):
            general.log(f"  FAILED {entry.get('path')}: {entry.get('message')}")

        return result
    except ImportError as e:
        general.log(f"Failed to import C# project manager: {e}")
        return None
    except Exception as e:
        general.log(f"Failed to migrate C# projects: {e}")
        return None


def generate_bindings():
    """Generate C# bindings using the ClangSharp tool"""
    try:
        # Import generate_bindings module
        try:
            from . import generate_bindings as gen_bindings
        except ImportError:
            import generate_bindings as gen_bindings
        
        # Get current project path
        project_path = os.environ.get("O3DE_PROJECT_PATH")
        if not project_path:
            general.log("Error: O3DE_PROJECT_PATH environment variable not set")
            return
        
        general.log(f"Generating C# bindings for project: {project_path}")
        
        # Generate bindings
        result = gen_bindings.generate_bindings_for_project(project_path)
        
        if result.success:
            general.log(f"✓ Binding generation succeeded!")
            general.log(f"  Generated {result.classes_generated} classes")
            general.log(f"  Wrote {result.files_written} files")
            if result.processed_gems:
                general.log(f"  Processed gems: {', '.join(result.processed_gems[:5])}")
                if len(result.processed_gems) > 5:
                    general.log(f"    ... and {len(result.processed_gems) - 5} more")
        else:
            general.log(f"✗ Binding generation failed: {result.error_message}")
        
        # Show warnings
        for warning in result.warnings:
            general.log(f"  Warning: {warning}")
            
    except ImportError as e:
        general.log(f"Failed to import generate_bindings module: {e}")
    except Exception as e:
        general.log(f"Failed to generate bindings: {e}")
        import traceback
        general.log(traceback.format_exc())


def regenerate_bindings_force():
    """Force regenerate C# bindings (bypass incremental build)"""
    try:
        # Import generate_bindings module
        try:
            from . import generate_bindings as gen_bindings
        except ImportError:
            import generate_bindings as gen_bindings
        
        # Get current project path
        project_path = os.environ.get("O3DE_PROJECT_PATH")
        if not project_path:
            general.log("Error: O3DE_PROJECT_PATH environment variable not set")
            return
        
        general.log(f"Force regenerating C# bindings (bypassing cache)...")
        
        # Generate bindings with force flag
        result = gen_bindings.generate_bindings_for_project(
            project_path, 
            force_regenerate=True
        )
        
        if result.success:
            general.log(f"✓ Binding regeneration succeeded!")
            general.log(f"  Generated {result.classes_generated} classes")
            general.log(f"  Wrote {result.files_written} files")
        else:
            general.log(f"✗ Binding regeneration failed: {result.error_message}")
            
    except ImportError as e:
        general.log(f"Failed to import generate_bindings module: {e}")
    except Exception as e:
        general.log(f"Failed to regenerate bindings: {e}")
        import traceback
        general.log(traceback.format_exc())


def run_binding_tests():
    """Run binding generator tests (Python pytest + C# xUnit)"""
    try:
        import subprocess
        
        general.log("=" * 60)
        general.log("Running O3DESharp Binding Generator Tests")
        general.log("=" * 60)
        
        # Get gem root
        gem_root = Path(__file__).parent.parent.parent
        
        # Run Python tests
        general.log("\n[1/2] Running Python tests with pytest...")
        python_tests_dir = gem_root / "Editor" / "Tests"
        
        if python_tests_dir.exists():
            try:
                result = subprocess.run(
                    ["pytest", str(python_tests_dir), "-v", "--tb=short"],
                    capture_output=True,
                    text=True,
                    timeout=60,
                    cwd=str(gem_root)
                )
                general.log(result.stdout)
                if result.returncode == 0:
                    general.log("✓ Python tests PASSED")
                else:
                    general.log(f"✗ Python tests FAILED (exit code: {result.returncode})")
                    general.log(result.stderr)
            except FileNotFoundError:
                general.log("⚠ pytest not found - install with: pip install pytest")
            except Exception as e:
                general.log(f"✗ Python tests failed to run: {e}")
        else:
            general.log("⚠ Python tests not found")
        
        # Run C# tests
        general.log("\n[2/2] Running C# tests with dotnet test...")
        csharp_tests_dir = gem_root / "Code" / "Tools" / "BindingGenerator.Tests"
        
        if csharp_tests_dir.exists():
            try:
                result = subprocess.run(
                    ["dotnet", "test", str(csharp_tests_dir / "BindingGenerator.Tests.csproj"), "--verbosity", "normal"],
                    capture_output=True,
                    text=True,
                    timeout=120,
                    cwd=str(csharp_tests_dir)
                )
                general.log(result.stdout)
                if result.returncode == 0:
                    general.log("✓ C# tests PASSED")
                else:
                    general.log(f"✗ C# tests FAILED (exit code: {result.returncode})")
                    general.log(result.stderr)
            except FileNotFoundError:
                general.log("⚠ dotnet not found - install .NET 8.0 SDK")
            except Exception as e:
                general.log(f"✗ C# tests failed to run: {e}")
        else:
            general.log("⚠ C# tests not found")
        
        general.log("\n" + "=" * 60)
        general.log("Test run complete")
        general.log("=" * 60)
        
    except Exception as e:
        general.log(f"Failed to run binding tests: {e}")
        import traceback
        general.log(traceback.format_exc())


def register_menus():
    """
    Register C# scripting tools in the Editor menus.
    
    This function is called during Editor startup to add menu items.
    Registers the following menus:
    - Tools > C# Scripting > Project Manager
    - Tools > C# Scripting > Create Project
    - Tools > C# Scripting > Create Script
    - Tools > C# Scripting > Browse Scripts
    - Tools > C# Scripting > Pick Script Class
    - Tools > C# Scripting > Quick Actions
    - Tools > C# Scripting > Build Projects
    - Tools > C# Bindings > Generate Bindings
    - Tools > C# Bindings > Force Regenerate
    - Tools > C# Bindings > Run Tests
    """
    try:
        general.log("O3DESharp: Registering C# scripting tools in Editor menus...")
        general.log("O3DESharp: Menu items will be available under Tools > C# Scripting and Tools > C# Bindings")
        general.log("O3DESharp: Use Python console to call functions directly:")
        general.log("  import csharp_editor_bootstrap")
        general.log("  csharp_editor_bootstrap.open_csharp_project_manager()")
        general.log("  csharp_editor_bootstrap.show_quick_actions()")
        general.log("  csharp_editor_bootstrap.pick_script_class()")
        general.log("  csharp_editor_bootstrap.generate_bindings()")
        general.log("  csharp_editor_bootstrap.regenerate_bindings_force()")
        general.log("  csharp_editor_bootstrap.run_binding_tests()")
        
        # Note: Actual menu registration requires the ActionManager API
        # which may not be available in all Editor contexts.
        # For now, functions can be called directly from Python console.
        
    except Exception as e:
        # Menu registration may not be available in all Editor contexts
        general.log(f"O3DESharp: Menu registration note: {e}")


def initialize_ebus_handler():
    """
    Initialize and connect the CSharpEditorToolsBus Python handler.
    
    This enables direct C++ ↔ Python communication for script discovery,
    validation, and editor tool integration without file-based IPC.
    """
    try:
        csharp_editor_tools = _import_csharp_editor_tools()
        
        # Get the handler instance
        handler = csharp_editor_tools.get_ebus_handler()
        
        # The handler is created and will respond to EBus broadcasts from C++
        # The connection happens automatically via the behavior context
        general.log("O3DESharp: CSharpEditorToolsBus handler initialized")

        # Phase 16b-2: opportunistic check for csprojs that still need the
        # auto-deploy MSBuild target. Doesn't change behavior - just nudges
        # the user toward the Migrate menu so they don't silently lose
        # IDE-build auto-reload.
        try:
            warn_unmigrated_csharp_projects()
        except Exception:  # noqa: BLE001 - non-fatal background check
            pass

        return handler

    except Exception as e:
        general.log(f"O3DESharp: EBus handler initialization: {e}")
        return None


# Auto-register when this module is imported during Editor startup
if __name__ == "__main__":
    register_menus()
    initialize_ebus_handler()
else:
    # Also initialize when imported as a module
    try:
        initialize_ebus_handler()
    except:
        pass  # Will be initialized when editor is ready
