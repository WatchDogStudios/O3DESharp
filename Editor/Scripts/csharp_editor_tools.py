#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#

"""
C# Script Editor Tools for O3DE

Provides Qt-based dialogs for creating and managing C# scripts.
"""

import sys
from pathlib import Path
from typing import Optional, List, Callable

# Ensure this script's directory is in sys.path for imports
_SCRIPT_DIR = Path(__file__).parent.resolve()
if str(_SCRIPT_DIR) not in sys.path:
    sys.path.insert(0, str(_SCRIPT_DIR))

from PySide2 import QtWidgets, QtCore, QtGui
from PySide2.QtWidgets import (
    QApplication, QDialog, QVBoxLayout, QHBoxLayout, QLabel, QLineEdit, 
    QPushButton, QComboBox, QTextEdit, QGroupBox, QFormLayout,
    QFileDialog, QMessageBox, QListWidget, QListWidgetItem,
    QTabWidget, QWidget, QSplitter, QTreeWidget, QTreeWidgetItem,
    QToolButton, QMenu, QAction, QProgressDialog
)
from PySide2.QtCore import Qt, Signal, QThread
from PySide2.QtGui import QFont, QIcon

import azlmbr.bus as bus
import azlmbr.editor as editor

# Import with fallback for different contexts
try:
    from .csharp_project_manager import CSharpProjectManager, get_project_manager
except ImportError:
    from csharp_project_manager import CSharpProjectManager, get_project_manager


class CreateProjectDialog(QDialog):
    """Dialog for creating a new C# project."""
    
    project_created = Signal(str)  # Emits project path on success
    
    def __init__(self, parent=None):
        super().__init__(parent)
        self.project_manager = get_project_manager()
        self.setup_ui()
        
    def setup_ui(self):
        self.setWindowTitle("Create C# Project")
        self.setMinimumWidth(450)
        self.setModal(True)
        
        layout = QVBoxLayout(self)
        
        # Project Info Group
        info_group = QGroupBox("Project Information")
        info_layout = QFormLayout(info_group)
        
        self.project_name_edit = QLineEdit()
        self.project_name_edit.setPlaceholderText("MyGame")
        self.project_name_edit.textChanged.connect(self._on_name_changed)
        info_layout.addRow("Project Name:", self.project_name_edit)
        
        self.namespace_edit = QLineEdit()
        self.namespace_edit.setPlaceholderText("(Same as project name)")
        info_layout.addRow("Namespace:", self.namespace_edit)
        
        # Location
        location_layout = QHBoxLayout()
        self.location_edit = QLineEdit()
        self.location_edit.setText(str(self.project_manager.scripts_path))
        self.location_edit.setReadOnly(True)
        location_layout.addWidget(self.location_edit)
        
        browse_btn = QPushButton("Browse...")
        browse_btn.clicked.connect(self._browse_location)
        location_layout.addWidget(browse_btn)
        info_layout.addRow("Location:", location_layout)
        
        layout.addWidget(info_group)
        
        # Preview Group
        preview_group = QGroupBox("Preview")
        preview_layout = QVBoxLayout(preview_group)
        
        self.preview_label = QLabel()
        self.preview_label.setWordWrap(True)
        self.preview_label.setStyleSheet("color: #888; font-style: italic;")
        preview_layout.addWidget(self.preview_label)
        
        layout.addWidget(preview_group)
        
        # Buttons
        button_layout = QHBoxLayout()
        button_layout.addStretch()
        
        cancel_btn = QPushButton("Cancel")
        cancel_btn.clicked.connect(self.reject)
        button_layout.addWidget(cancel_btn)
        
        self.create_btn = QPushButton("Create Project")
        self.create_btn.setEnabled(False)
        self.create_btn.clicked.connect(self._create_project)
        self.create_btn.setDefault(True)
        button_layout.addWidget(self.create_btn)
        
        layout.addLayout(button_layout)
        
        self._update_preview()
        
    def _on_name_changed(self, text):
        self.create_btn.setEnabled(bool(text.strip()))
        self._update_preview()
        
    def _update_preview(self):
        name = self.project_name_edit.text().strip() or "MyGame"
        namespace = self.namespace_edit.text().strip() or name
        location = self.location_edit.text()
        
        preview = f"""
<b>Project will be created at:</b><br>
{location}/{name}/<br><br>
<b>Files created:</b><br>
• {name}.csproj<br>
• GameScript.cs<br>
• project.json<br><br>
<b>Default class:</b> {namespace}.GameScript
"""
        self.preview_label.setText(preview)
        
    def _browse_location(self):
        path = QFileDialog.getExistingDirectory(
            self, "Select Location", self.location_edit.text()
        )
        if path:
            self.location_edit.setText(path)
            self._update_preview()
            
    def _create_project(self):
        name = self.project_name_edit.text().strip()
        namespace = self.namespace_edit.text().strip() or name
        location = Path(self.location_edit.text())
        
        result = self.project_manager.create_project(name, namespace, location)
        
        if result["success"]:
            QMessageBox.information(
                self, "Success", 
                f"Project '{name}' created successfully!\n\n{result['project_path']}"
            )
            self.project_created.emit(result["project_path"])
            self.accept()
        else:
            QMessageBox.critical(self, "Error", result["message"])


class CreateScriptDialog(QDialog):
    """Dialog for creating a new C# script."""
    
    script_created = Signal(str)  # Emits script path on success
    
    def __init__(self, project_path: str = None, parent=None):
        super().__init__(parent)
        self.project_manager = get_project_manager()
        self.project_path = project_path
        self.setup_ui()
        
    def setup_ui(self):
        self.setWindowTitle("Create C# Script")
        self.setMinimumWidth(500)
        self.setModal(True)
        
        layout = QVBoxLayout(self)
        
        # Project Selection
        project_group = QGroupBox("Project")
        project_layout = QHBoxLayout(project_group)
        
        self.project_combo = QComboBox()
        self._populate_projects()
        project_layout.addWidget(self.project_combo, 1)
        
        refresh_btn = QToolButton()
        refresh_btn.setText("↻")
        refresh_btn.setToolTip("Refresh project list")
        refresh_btn.clicked.connect(self._populate_projects)
        project_layout.addWidget(refresh_btn)
        
        layout.addWidget(project_group)
        
        # Script Info Group
        script_group = QGroupBox("Script Information")
        script_layout = QFormLayout(script_group)
        
        self.class_name_edit = QLineEdit()
        self.class_name_edit.setPlaceholderText("MyScript")
        self.class_name_edit.textChanged.connect(self._on_name_changed)
        script_layout.addRow("Class Name:", self.class_name_edit)
        
        self.template_combo = QComboBox()
        for template_name, info in self.project_manager.SCRIPT_TEMPLATES.items():
            self.template_combo.addItem(f"{template_name} - {info['description']}", template_name)
        self.template_combo.currentIndexChanged.connect(self._update_preview)
        script_layout.addRow("Template:", self.template_combo)
        
        self.description_edit = QLineEdit()
        self.description_edit.setPlaceholderText("(Optional) Class description")
        script_layout.addRow("Description:", self.description_edit)
        
        layout.addWidget(script_group)
        
        # Preview Group
        preview_group = QGroupBox("Code Preview")
        preview_layout = QVBoxLayout(preview_group)
        
        self.preview_text = QTextEdit()
        self.preview_text.setReadOnly(True)
        self.preview_text.setFont(QFont("Consolas", 9))
        self.preview_text.setMaximumHeight(200)
        preview_layout.addWidget(self.preview_text)
        
        layout.addWidget(preview_group)
        
        # Buttons
        button_layout = QHBoxLayout()
        button_layout.addStretch()
        
        cancel_btn = QPushButton("Cancel")
        cancel_btn.clicked.connect(self.reject)
        button_layout.addWidget(cancel_btn)
        
        self.create_btn = QPushButton("Create Script")
        self.create_btn.setEnabled(False)
        self.create_btn.clicked.connect(self._create_script)
        self.create_btn.setDefault(True)
        button_layout.addWidget(self.create_btn)
        
        layout.addLayout(button_layout)
        
        self._update_preview()
        
    def _populate_projects(self):
        self.project_combo.clear()
        projects = self.project_manager.list_projects()
        
        for project in projects:
            self.project_combo.addItem(project["name"], project)
            
        # Select the provided project if any
        if self.project_path:
            for i in range(self.project_combo.count()):
                if self.project_combo.itemData(i)["path"] == self.project_path:
                    self.project_combo.setCurrentIndex(i)
                    break
                    
        self._update_preview()
        
    def _on_name_changed(self, text):
        # Validate class name (C# identifier rules)
        is_valid = bool(text.strip()) and text[0].isalpha() and all(
            c.isalnum() or c == '_' for c in text
        )
        self.create_btn.setEnabled(is_valid and self.project_combo.count() > 0)
        self._update_preview()
        
    def _update_preview(self):
        class_name = self.class_name_edit.text().strip() or "MyScript"
        template_type = self.template_combo.currentData() or "ScriptComponent"
        description = self.description_edit.text().strip() or f"{class_name} class"
        
        project_data = self.project_combo.currentData()
        namespace = project_data["namespace"] if project_data else "MyGame"
        
        template = self.project_manager.SCRIPT_TEMPLATES.get(template_type, {})
        template_str = template.get("template", "")
        
        preview = template_str.format(
            namespace=namespace,
            class_name=class_name,
            description=description
        )
        
        self.preview_text.setPlainText(preview)
        
    def _create_script(self):
        project_data = self.project_combo.currentData()
        if not project_data:
            QMessageBox.warning(self, "Warning", "Please select a project first.")
            return
            
        class_name = self.class_name_edit.text().strip()
        template_type = self.template_combo.currentData()
        description = self.description_edit.text().strip()
        
        result = self.project_manager.create_script(
            project_data["path"],
            class_name,
            project_data["namespace"],
            template_type,
            description
        )
        
        if result["success"]:
            QMessageBox.information(
                self, "Success",
                f"Script '{class_name}.cs' created successfully!"
            )
            self.script_created.emit(result["file_path"])
            self.accept()
        else:
            QMessageBox.critical(self, "Error", result["message"])


class ScriptBrowserDialog(QDialog):
    """Dialog for browsing and selecting C# scripts."""
    
    script_selected = Signal(str)  # Emits fully qualified class name
    
    def __init__(self, parent=None):
        super().__init__(parent)
        self.project_manager = get_project_manager()
        self._selected_class = None  # Store the selected class name
        self.setup_ui()
        self._populate_tree()
        
    def setup_ui(self):
        self.setWindowTitle("Select C# Script")
        self.setMinimumSize(500, 400)
        self.setModal(True)
        
        layout = QVBoxLayout(self)
        
        # Toolbar
        toolbar = QHBoxLayout()
        
        refresh_btn = QPushButton("↻ Refresh")
        refresh_btn.clicked.connect(self._populate_tree)
        toolbar.addWidget(refresh_btn)
        
        toolbar.addStretch()
        
        new_project_btn = QPushButton("+ New Project")
        new_project_btn.clicked.connect(self._create_project)
        toolbar.addWidget(new_project_btn)
        
        new_script_btn = QPushButton("+ New Script")
        new_script_btn.clicked.connect(self._create_script)
        toolbar.addWidget(new_script_btn)
        
        layout.addLayout(toolbar)
        
        # Filter
        filter_layout = QHBoxLayout()
        filter_layout.addWidget(QLabel("Filter:"))
        self.filter_edit = QLineEdit()
        self.filter_edit.setPlaceholderText("Type to filter scripts...")
        self.filter_edit.textChanged.connect(self._filter_tree)
        filter_layout.addWidget(self.filter_edit)
        
        self.components_only_cb = QtWidgets.QCheckBox("Script Components Only")
        self.components_only_cb.setChecked(True)
        self.components_only_cb.stateChanged.connect(self._populate_tree)
        filter_layout.addWidget(self.components_only_cb)
        
        layout.addLayout(filter_layout)
        
        # Tree
        self.tree = QTreeWidget()
        self.tree.setHeaderLabels(["Name", "Type", "Full Path"])
        self.tree.setColumnWidth(0, 200)
        self.tree.setColumnWidth(1, 100)
        self.tree.itemDoubleClicked.connect(self._on_item_double_clicked)
        self.tree.itemSelectionChanged.connect(self._on_selection_changed)
        layout.addWidget(self.tree)
        
        # Selected Script Info
        info_group = QGroupBox("Selected Script")
        info_layout = QFormLayout(info_group)
        
        self.selected_label = QLabel("None")
        self.selected_label.setStyleSheet("font-weight: bold;")
        info_layout.addRow("Class:", self.selected_label)
        
        layout.addWidget(info_group)
        
        # Buttons
        button_layout = QHBoxLayout()
        button_layout.addStretch()
        
        cancel_btn = QPushButton("Cancel")
        cancel_btn.clicked.connect(self.reject)
        button_layout.addWidget(cancel_btn)
        
        self.select_btn = QPushButton("Select")
        self.select_btn.setEnabled(False)
        self.select_btn.clicked.connect(self._select_script)
        self.select_btn.setDefault(True)
        button_layout.addWidget(self.select_btn)
        
        layout.addLayout(button_layout)
        
    def _populate_tree(self):
        self.tree.clear()
        components_only = self.components_only_cb.isChecked()
        
        projects = self.project_manager.list_projects()
        
        for project in projects:
            project_item = QTreeWidgetItem([project["name"], "Project", project["path"]])
            project_item.setData(0, Qt.UserRole, {"type": "project", "data": project})
            
            scripts = self.project_manager.list_scripts(project["path"])
            
            for script in scripts:
                if components_only and not script["is_script_component"]:
                    continue
                    
                script_item = QTreeWidgetItem([
                    script["class_name"],
                    script["base_class"] or "class",
                    script["full_name"]
                ])
                script_item.setData(0, Qt.UserRole, {"type": "script", "data": script})
                
                if script["is_script_component"]:
                    # Use QApplication.style() instead of self.style() to avoid deleted object issues
                    script_item.setIcon(0, QApplication.style().standardIcon(QtWidgets.QStyle.SP_FileIcon))
                    
                project_item.addChild(script_item)
            
            if project_item.childCount() > 0 or not components_only:
                self.tree.addTopLevelItem(project_item)
                project_item.setExpanded(True)
                
    def _filter_tree(self, text):
        text = text.lower()
        
        for i in range(self.tree.topLevelItemCount()):
            project_item = self.tree.topLevelItem(i)
            project_visible = False
            
            for j in range(project_item.childCount()):
                script_item = project_item.child(j)
                script_data = script_item.data(0, Qt.UserRole)
                
                if script_data and script_data["type"] == "script":
                    matches = text in script_data["data"]["class_name"].lower() or \
                              text in script_data["data"]["full_name"].lower()
                    script_item.setHidden(not matches)
                    if matches:
                        project_visible = True
                        
            project_item.setHidden(not project_visible and bool(text))
            
    def _on_selection_changed(self):
        items = self.tree.selectedItems()
        if items:
            item_data = items[0].data(0, Qt.UserRole)
            if item_data and item_data["type"] == "script":
                self.selected_label.setText(item_data["data"]["full_name"])
                self.select_btn.setEnabled(True)
                return
                
        self.selected_label.setText("None")
        self.select_btn.setEnabled(False)
        
    def _on_item_double_clicked(self, item, column):
        item_data = item.data(0, Qt.UserRole)
        if item_data and item_data["type"] == "script":
            self._select_script()
            
    def _select_script(self):
        items = self.tree.selectedItems()
        if items:
            item_data = items[0].data(0, Qt.UserRole)
            if item_data and item_data["type"] == "script":
                self._selected_class = item_data["data"]["full_name"]
                self.script_selected.emit(self._selected_class)
                self.accept()
    
    def get_selected_class(self) -> str:
        """Get the fully qualified name of the selected script class."""
        return self._selected_class
                
    def _create_project(self):
        dialog = CreateProjectDialog(self)
        dialog.project_created.connect(lambda _: self._populate_tree())
        dialog.exec_()
        
    def _create_script(self):
        # Get currently selected project
        project_path = None
        items = self.tree.selectedItems()
        if items:
            item_data = items[0].data(0, Qt.UserRole)
            if item_data:
                if item_data["type"] == "project":
                    project_path = item_data["data"]["path"]
                elif item_data["type"] == "script":
                    # Get parent project
                    parent = items[0].parent()
                    if parent:
                        parent_data = parent.data(0, Qt.UserRole)
                        if parent_data and parent_data["type"] == "project":
                            project_path = parent_data["data"]["path"]
        
        dialog = CreateScriptDialog(project_path, self)
        dialog.script_created.connect(lambda _: self._populate_tree())
        dialog.exec_()


class CSharpProjectManagerWindow(QDialog):
    """Main window for C# project management."""
    
    def __init__(self, parent=None):
        super().__init__(parent)
        self.project_manager = get_project_manager()
        self.setup_ui()
        self._refresh()
        
    def setup_ui(self):
        self.setWindowTitle("C# Project Manager")
        self.setMinimumSize(700, 500)
        
        layout = QVBoxLayout(self)
        
        # Toolbar
        toolbar = QHBoxLayout()
        
        new_project_btn = QPushButton("+ New Project")
        new_project_btn.clicked.connect(self._create_project)
        toolbar.addWidget(new_project_btn)
        
        new_script_btn = QPushButton("+ New Script")
        new_script_btn.clicked.connect(self._create_script)
        toolbar.addWidget(new_script_btn)
        
        toolbar.addStretch()
        
        build_btn = QPushButton("Build All")
        build_btn.clicked.connect(self._build_all)
        toolbar.addWidget(build_btn)
        
        refresh_btn = QPushButton("↻ Refresh")
        refresh_btn.clicked.connect(self._refresh)
        toolbar.addWidget(refresh_btn)
        
        layout.addLayout(toolbar)
        
        # Splitter with project list and details
        splitter = QSplitter(Qt.Horizontal)
        
        # Project List
        self.project_list = QListWidget()
        self.project_list.itemSelectionChanged.connect(self._on_project_selected)
        splitter.addWidget(self.project_list)
        
        # Details Panel
        details_widget = QWidget()
        details_layout = QVBoxLayout(details_widget)
        
        # Project Info
        self.project_info_group = QGroupBox("Project Information")
        project_info_layout = QFormLayout(self.project_info_group)
        
        self.info_name = QLabel("-")
        project_info_layout.addRow("Name:", self.info_name)
        
        self.info_namespace = QLabel("-")
        project_info_layout.addRow("Namespace:", self.info_namespace)
        
        self.info_path = QLabel("-")
        self.info_path.setWordWrap(True)
        project_info_layout.addRow("Path:", self.info_path)
        
        details_layout.addWidget(self.project_info_group)
        
        # Scripts List
        scripts_group = QGroupBox("Scripts")
        scripts_layout = QVBoxLayout(scripts_group)
        
        self.scripts_list = QListWidget()
        self.scripts_list.itemDoubleClicked.connect(self._open_script)
        scripts_layout.addWidget(self.scripts_list)
        
        scripts_toolbar = QHBoxLayout()
        add_script_btn = QPushButton("+ Add Script")
        add_script_btn.clicked.connect(self._add_script_to_project)
        scripts_toolbar.addWidget(add_script_btn)
        scripts_toolbar.addStretch()
        scripts_layout.addLayout(scripts_toolbar)
        
        details_layout.addWidget(scripts_group)
        
        # Build Controls
        build_group = QGroupBox("Build")
        build_layout = QHBoxLayout(build_group)
        
        self.config_combo = QComboBox()
        self.config_combo.addItems(["Release", "Debug"])
        build_layout.addWidget(QLabel("Configuration:"))
        build_layout.addWidget(self.config_combo)
        
        build_project_btn = QPushButton("Build Project")
        build_project_btn.clicked.connect(self._build_project)
        build_layout.addWidget(build_project_btn)
        
        build_layout.addStretch()
        
        details_layout.addWidget(build_group)
        
        # Settings Group
        settings_group = QGroupBox("Settings")
        settings_layout = QFormLayout(settings_group)
        
        # Managed Assembly Path (Coral.Managed)
        assembly_layout = QHBoxLayout()
        self.assembly_path_edit = QLineEdit()
        self.assembly_path_edit.setPlaceholderText("Auto-detected")
        self.assembly_path_edit.setText(self.project_manager.get_coral_managed_path())
        self.assembly_path_edit.setReadOnly(True)
        assembly_layout.addWidget(self.assembly_path_edit)
        
        browse_assembly_btn = QPushButton("Browse...")
        browse_assembly_btn.clicked.connect(self._browse_managed_assembly)
        assembly_layout.addWidget(browse_assembly_btn)
        
        clear_assembly_btn = QPushButton("Auto")
        clear_assembly_btn.setToolTip("Reset to auto-detected path")
        clear_assembly_btn.clicked.connect(self._clear_managed_assembly)
        assembly_layout.addWidget(clear_assembly_btn)
        
        settings_layout.addRow("Coral.Managed:", assembly_layout)
        
        # User Assembly Path (Game Scripts)
        user_assembly_layout = QHBoxLayout()
        self.user_assembly_edit = QLineEdit()
        self.user_assembly_edit.setPlaceholderText("GameScripts.dll")
        self.user_assembly_edit.setText(self.project_manager.get_user_assembly_name())
        self.user_assembly_edit.setToolTip(
            "Name of your game's C# assembly (e.g., 'MyGame' for MyGame.dll).\n"
            "This is the compiled output of your C# project."
        )
        user_assembly_layout.addWidget(self.user_assembly_edit)
        
        set_user_assembly_btn = QPushButton("Set")
        set_user_assembly_btn.setToolTip("Set the user assembly name")
        set_user_assembly_btn.clicked.connect(self._set_user_assembly)
        user_assembly_layout.addWidget(set_user_assembly_btn)
        
        browse_user_assembly_btn = QPushButton("Browse...")
        browse_user_assembly_btn.setToolTip("Browse for a specific assembly file")
        browse_user_assembly_btn.clicked.connect(self._browse_user_assembly)
        user_assembly_layout.addWidget(browse_user_assembly_btn)
        
        clear_user_assembly_btn = QPushButton("Default")
        clear_user_assembly_btn.setToolTip("Reset to default (GameScripts.dll)")
        clear_user_assembly_btn.clicked.connect(self._clear_user_assembly)
        user_assembly_layout.addWidget(clear_user_assembly_btn)
        
        settings_layout.addRow("User Assembly:", user_assembly_layout)
        
        # Coral Deployment Status
        coral_deploy_layout = QHBoxLayout()
        self.coral_deploy_status_label = QLabel()
        self._update_coral_deploy_status()
        coral_deploy_layout.addWidget(self.coral_deploy_status_label)
        
        deploy_coral_btn = QPushButton("Deploy Coral")
        deploy_coral_btn.setToolTip("Deploy Coral.Managed files to project's Bin/Scripts/Coral directory")
        deploy_coral_btn.clicked.connect(self._deploy_coral)
        coral_deploy_layout.addWidget(deploy_coral_btn)
        
        check_coral_btn = QPushButton("Check")
        check_coral_btn.setToolTip("Check Coral deployment status")
        check_coral_btn.clicked.connect(self._check_coral_deployment)
        coral_deploy_layout.addWidget(check_coral_btn)
        
        settings_layout.addRow("Coral:", coral_deploy_layout)
        
        # O3DE.Core Deployment Status
        core_deploy_layout = QHBoxLayout()
        self.core_deploy_status_label = QLabel()
        self._update_core_deploy_status()
        core_deploy_layout.addWidget(self.core_deploy_status_label)
        
        deploy_core_btn = QPushButton("Deploy O3DE.Core")
        deploy_core_btn.setToolTip("Deploy O3DE.Core.dll to project's Bin/Scripts directory")
        deploy_core_btn.clicked.connect(self._deploy_o3de_core)
        core_deploy_layout.addWidget(deploy_core_btn)
        
        check_core_btn = QPushButton("Check")
        check_core_btn.setToolTip("Check O3DE.Core deployment status")
        check_core_btn.clicked.connect(self._check_o3de_core_deployment)
        core_deploy_layout.addWidget(check_core_btn)
        
        settings_layout.addRow("O3DE.Core:", core_deploy_layout)
        
        # Deploy All button
        deploy_all_layout = QHBoxLayout()
        deploy_all_btn = QPushButton("Deploy All")
        deploy_all_btn.setToolTip("Deploy both Coral and O3DE.Core to the project")
        deploy_all_btn.clicked.connect(self._deploy_all)
        deploy_all_layout.addWidget(deploy_all_btn)
        deploy_all_layout.addStretch()
        
        settings_layout.addRow("", deploy_all_layout)
        
        details_layout.addWidget(settings_group)
        
        splitter.addWidget(details_widget)
        splitter.setSizes([250, 450])
        
        layout.addWidget(splitter)
        
        # Status
        self.status_label = QLabel("Ready")
        self.status_label.setStyleSheet("color: #888;")
        layout.addWidget(self.status_label)
        
    def _set_user_assembly(self):
        """Set the user assembly name from the text field."""
        name = self.user_assembly_edit.text().strip()
        if not name:
            QMessageBox.warning(self, "Warning", "Please enter an assembly name.")
            return
        
        result = self.project_manager.set_user_assembly_name(name)
        if result["success"]:
            self.status_label.setText(result["message"])
            QMessageBox.information(
                self, 
                "Success", 
                f"User assembly set to: {name}\n\n"
                "Note: You need to restart the Editor for changes to take effect."
            )
        else:
            QMessageBox.warning(self, "Error", result["message"])
    
    def _browse_user_assembly(self):
        """Browse for a user assembly file."""
        default_dir = str(self.project_manager.project_path / "Bin" / "Scripts")
        file_path, _ = QFileDialog.getOpenFileName(
            self,
            "Select User Assembly",
            default_dir,
            "Assembly Files (*.dll);;All Files (*.*)"
        )
        if file_path:
            result = self.project_manager.set_user_assembly_path(file_path)
            if result["success"]:
                self.user_assembly_edit.setText(self.project_manager.get_user_assembly_name())
                self.status_label.setText(result["message"])
                QMessageBox.information(
                    self, 
                    "Success", 
                    f"User assembly set to: {file_path}\n\n"
                    "Note: You need to restart the Editor for changes to take effect."
                )
            else:
                QMessageBox.warning(self, "Error", result["message"])
    
    def _clear_user_assembly(self):
        """Reset user assembly to default."""
        self.project_manager.clear_user_assembly_path()
        self.user_assembly_edit.setText(self.project_manager.get_user_assembly_name())
        self.status_label.setText("User assembly reset to default (GameScripts.dll)")
        
    def _update_coral_deploy_status(self):
        """Update the Coral deployment status label."""
        status = self.project_manager.check_coral_deployment()
        if status["deployed"]:
            self.coral_deploy_status_label.setText("✓ Deployed")
            self.coral_deploy_status_label.setStyleSheet("color: green;")
            self.coral_deploy_status_label.setToolTip(f"Files at: {status['path']}")
        else:
            self.coral_deploy_status_label.setText("✗ Not deployed")
            self.coral_deploy_status_label.setStyleSheet("color: red;")
            self.coral_deploy_status_label.setToolTip(status["message"])
    
    def _update_core_deploy_status(self):
        """Update the O3DE.Core deployment status label."""
        status = self.project_manager.check_o3de_core_deployment()
        if status["deployed"]:
            self.core_deploy_status_label.setText("✓ Deployed")
            self.core_deploy_status_label.setStyleSheet("color: green;")
            self.core_deploy_status_label.setToolTip(f"Files at: {status['path']}")
        else:
            self.core_deploy_status_label.setText("✗ Not deployed")
            self.core_deploy_status_label.setStyleSheet("color: red;")
            self.core_deploy_status_label.setToolTip(status["message"])
    
    def _deploy_coral(self):
        """Deploy Coral.Managed files to the project."""
        # First try auto-detection
        result = self.project_manager.deploy_coral()
        
        if not result["success"]:
            # Ask user to browse for source
            reply = QMessageBox.question(
                self, 
                "Coral Files Not Found",
                f"{result['message']}\n\nWould you like to browse for the Coral.Managed directory?",
                QMessageBox.Yes | QMessageBox.No
            )
            
            if reply == QMessageBox.Yes:
                source_dir = QFileDialog.getExistingDirectory(
                    self,
                    "Select Coral.Managed Build Output Directory",
                    str(Path(self.project_manager.coral_managed_path).parent) 
                        if self.project_manager.coral_managed_path else ""
                )
                if source_dir:
                    result = self.project_manager.deploy_coral(source_dir)
        
        if result["success"]:
            QMessageBox.information(self, "Success", result["message"])
            self._update_coral_deploy_status()
        else:
            QMessageBox.warning(self, "Deployment Failed", result["message"])
    
    def _check_coral_deployment(self):
        """Check and show Coral deployment status."""
        status = self.project_manager.check_coral_deployment()
        self._update_coral_deploy_status()
        
        files_info = "\n".join([f"  • {f}" for f in status["found_files"]]) if status["found_files"] else "  None"
        missing_info = "\n".join([f"  • {f}" for f in status["missing_files"]]) if status["missing_files"] else "  None"
        
        message = f"""Coral Deployment Status

Path: {status['path']}

Found Files:
{files_info}

Missing Files:
{missing_info}

Status: {status['message']}"""
        
        QMessageBox.information(self, "Coral Deployment Status", message)
    
    def _deploy_o3de_core(self):
        """Deploy O3DE.Core.dll to the project."""
        # First try auto-detection
        result = self.project_manager.deploy_o3de_core()
        
        if not result["success"]:
            # Ask user to browse for source
            reply = QMessageBox.question(
                self, 
                "O3DE.Core Files Not Found",
                f"{result['message']}\n\nWould you like to browse for the O3DE.Core build output directory?",
                QMessageBox.Yes | QMessageBox.No
            )
            
            if reply == QMessageBox.Yes:
                source_dir = QFileDialog.getExistingDirectory(
                    self,
                    "Select O3DE.Core Build Output Directory",
                    ""
                )
                if source_dir:
                    result = self.project_manager.deploy_o3de_core(source_dir)
        
        if result["success"]:
            QMessageBox.information(self, "Success", result["message"])
            self._update_core_deploy_status()
        else:
            QMessageBox.warning(self, "Deployment Failed", result["message"])
    
    def _check_o3de_core_deployment(self):
        """Check and show O3DE.Core deployment status."""
        status = self.project_manager.check_o3de_core_deployment()
        self._update_core_deploy_status()
        
        files_info = "\n".join([f"  • {f}" for f in status["found_files"]]) if status["found_files"] else "  None"
        missing_info = "\n".join([f"  • {f}" for f in status["missing_files"]]) if status["missing_files"] else "  None"
        
        message = f"""O3DE.Core Deployment Status

Path: {status['path']}

Found Files:
{files_info}

Missing Files:
{missing_info}

Status: {status['message']}"""
        
        QMessageBox.information(self, "O3DE.Core Deployment Status", message)
    
    def _deploy_all(self):
        """Deploy both Coral and O3DE.Core to the project."""
        results = []
        
        # Deploy Coral
        coral_result = self.project_manager.deploy_coral()
        results.append(("Coral", coral_result))
        
        # Deploy O3DE.Core
        core_result = self.project_manager.deploy_o3de_core()
        results.append(("O3DE.Core", core_result))
        
        # Update status labels
        self._update_coral_deploy_status()
        self._update_core_deploy_status()
        
        # Build result message
        messages = []
        all_success = True
        for name, result in results:
            status = "✓" if result["success"] else "✗"
            messages.append(f"{status} {name}: {result['message']}")
            if not result["success"]:
                all_success = False
        
        full_message = "\n\n".join(messages)
        
        if all_success:
            QMessageBox.information(self, "Deployment Complete", full_message)
        else:
            QMessageBox.warning(self, "Deployment Partial", full_message)
        
    def _refresh(self):
        self.project_list.clear()
        projects = self.project_manager.list_projects()
        
        for project in projects:
            item = QListWidgetItem(project["name"])
            item.setData(Qt.UserRole, project)
            self.project_list.addItem(item)
            
        self.status_label.setText(f"Found {len(projects)} project(s)")
        
    def _on_project_selected(self):
        items = self.project_list.selectedItems()
        if not items:
            self.info_name.setText("-")
            self.info_namespace.setText("-")
            self.info_path.setText("-")
            self.scripts_list.clear()
            return
            
        project = items[0].data(Qt.UserRole)
        
        self.info_name.setText(project["name"])
        self.info_namespace.setText(project["namespace"])
        self.info_path.setText(project["path"])
        
        # Populate scripts
        self.scripts_list.clear()
        scripts = self.project_manager.list_scripts(project["path"])
        
        for script in scripts:
            icon = "📜" if script["is_script_component"] else "📄"
            item = QListWidgetItem(f"{icon} {script['class_name']}")
            item.setData(Qt.UserRole, script)
            item.setToolTip(f"Full name: {script['full_name']}\nBase class: {script['base_class'] or 'None'}")
            self.scripts_list.addItem(item)
            
    def _create_project(self):
        dialog = CreateProjectDialog(self)
        dialog.project_created.connect(lambda _: self._refresh())
        dialog.exec_()
        
    def _create_script(self):
        dialog = CreateScriptDialog(None, self)
        dialog.script_created.connect(lambda _: self._on_project_selected())
        dialog.exec_()
        
    def _add_script_to_project(self):
        items = self.project_list.selectedItems()
        if not items:
            QMessageBox.warning(self, "Warning", "Please select a project first.")
            return
            
        project = items[0].data(Qt.UserRole)
        dialog = CreateScriptDialog(project["path"], self)
        dialog.script_created.connect(lambda _: self._on_project_selected())
        dialog.exec_()
        
    def _open_script(self, item):
        script = item.data(Qt.UserRole)
        if script and isinstance(script, dict) and "path" in script:
            import subprocess
            import sys
            
            # Try to open with VS Code first, then default editor
            script_path = script["path"]
            try:
                subprocess.Popen(["code", script_path])
            except:
                try:
                    if sys.platform == "win32":
                        import os
                        os.startfile(script_path)
                    else:
                        subprocess.Popen(["xdg-open", script_path])
                except Exception as e:
                    QMessageBox.warning(self, "Warning", f"Could not open script: {e}")
                    
    def _build_project(self):
        items = self.project_list.selectedItems()
        if not items:
            QMessageBox.warning(self, "Warning", "Please select a project first.")
            return
            
        project = items[0].data(Qt.UserRole)
        config = self.config_combo.currentText()
        
        self.status_label.setText(f"Building {project['name']}...")
        QtWidgets.QApplication.processEvents()
        
        result = self.project_manager.build_project(project["path"], config)
        
        if result["success"]:
            self.status_label.setText(f"Build succeeded: {result['output_path']}")
            QMessageBox.information(self, "Build Succeeded", result["message"])
        else:
            self.status_label.setText("Build failed")
            QMessageBox.critical(
                self, "Build Failed",
                f"{result['message']}\n\n{result.get('build_output', '')}"
            )
            
    def _build_all(self):
        projects = self.project_manager.list_projects()
        config = self.config_combo.currentText()
        
        success_count = 0
        fail_count = 0
        
        for project in projects:
            self.status_label.setText(f"Building {project['name']}...")
            QtWidgets.QApplication.processEvents()
            
            result = self.project_manager.build_project(project["path"], config)
            if result["success"]:
                success_count += 1
            else:
                fail_count += 1
                
        self.status_label.setText(f"Build complete: {success_count} succeeded, {fail_count} failed")
        QMessageBox.information(
            self, "Build Complete",
            f"Built {len(projects)} project(s)\n\nSucceeded: {success_count}\nFailed: {fail_count}"
        )
    
    def _browse_managed_assembly(self):
        """Browse for the Coral.Managed.dll file."""
        current_path = self.assembly_path_edit.text()
        start_dir = str(Path(current_path).parent) if current_path else ""
        
        file_path, _ = QFileDialog.getOpenFileName(
            self,
            "Select Coral.Managed.dll",
            start_dir,
            "DLL Files (*.dll);;All Files (*.*)"
        )
        
        if file_path:
            result = self.project_manager.set_coral_managed_path(file_path)
            if result["success"]:
                self.assembly_path_edit.setText(file_path)
                self.status_label.setText(result["message"])
            else:
                QMessageBox.warning(self, "Invalid Path", result["message"])
    
    def _clear_managed_assembly(self):
        """Reset managed assembly path to auto-detected value."""
        self.project_manager.clear_coral_managed_path()
        new_path = self.project_manager.get_coral_managed_path()
        self.assembly_path_edit.setText(new_path)
        self.status_label.setText("Coral.Managed path reset to auto-detected value")


# Entry points for O3DE Editor menus
def show_project_manager():
    """Show the C# Project Manager window."""
    window = CSharpProjectManagerWindow()
    window.exec_()
    

def show_create_project_dialog():
    """Show the Create Project dialog."""
    dialog = CreateProjectDialog()
    dialog.exec_()


def show_create_script_dialog():
    """Show the Create Script dialog."""
    dialog = CreateScriptDialog()
    dialog.exec_()


def show_script_browser():
    """Show the Script Browser dialog."""
    dialog = ScriptBrowserDialog()
    if dialog.exec_() == QDialog.Accepted:
        # Return the selected script name
        pass
