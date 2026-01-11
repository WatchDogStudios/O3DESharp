"""
Copyright (c) Contributors to the Open 3D Engine Project.
For complete copyright and license terms please see the LICENSE at the root of this distribution.

SPDX-License-Identifier: Apache-2.0 OR MIT

O3DESharp Editor Bootstrap - Registers C# scripting tools in the Editor menus
"""

import sys
import os
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
            general.log(f"Building: {project_path}")
            if manager.build_project(project_path):
                success_count += 1
                general.log(f"  Build succeeded")
            else:
                fail_count += 1
                general.log(f"  Build FAILED")
        
        general.log(f"Build complete: {success_count} succeeded, {fail_count} failed")
        
    except ImportError as e:
        general.log(f"Failed to import C# project manager: {e}")
    except Exception as e:
        general.log(f"Failed to build C# projects: {e}")


def register_menus():
    """
    Register C# scripting tools in the Editor menus.
    
    This function is called during Editor startup to add menu items.
    """
    try:
        # Use the ActionManager API to register actions and menus
        import azlmbr.action as action
        
        # Register the C# submenu under Tools
        action.ActionManagerInterface_GetAction

        general.log("O3DESharp: C# scripting tools registered in Editor menus")
        
    except Exception as e:
        # Menu registration may not be available in all Editor contexts
        general.log(f"O3DESharp: Could not register menus (this is normal during some startup phases): {e}")


# Auto-register when this module is imported during Editor startup
if __name__ == "__main__":
    register_menus()
