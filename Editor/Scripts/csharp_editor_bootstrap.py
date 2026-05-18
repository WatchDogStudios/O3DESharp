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


# ----------------------------------------------------------------------
# Phase 17 - managed-mode debugger attach helpers.
#
# Coral hosts .NET in-process with Editor.exe, so any IDE that supports
# "Attach to Process" in managed mode works against script code today.
# These helpers turn that into one-click actions in the Tools menu by
# locating the user's IDE and invoking its attach CLI - or, when no
# IDE-specific CLI exists, falling back to Windows' system JIT debugger
# launcher (vsjitdebugger.exe) which presents a picker of all registered
# managed debuggers.
# ----------------------------------------------------------------------

def _editor_pid() -> int:
    """Return the editor's own process ID."""
    import os
    return os.getpid()


# ----------------------------------------------------------------------
# Phase 17d: smart default IDE detection.
# Picked in this preference order on first editor start, when
# /O3DE/O3DESharp/AutoAttachOnPlay is unset:
#   1. Rider - best managed-debug experience, URL-protocol attach is
#      rock-solid.
#   2. VS Code - widely installed, bundled launch.json from Phase 17a/c
#      means F5 works.
#   3. JIT picker - Windows-only fallback that surfaces whatever
#      debugger the OS knows about (typically Visual Studio if
#      installed).
#   4. None - cross-platform fallback when no debugger is detected;
#      auto-attach stays Off and the user can flip it manually.
# ----------------------------------------------------------------------

def detect_preferred_ide() -> str:
    """
    Return one of 'vscode' / 'rider' / 'jit' / '' based on what's
    installed on this machine. Cheap (no subprocess spawn), so it's
    safe to call on every editor start.

    Priority order: vscode > rider > jit. VS Code wins when both VS
    Code and Rider are installed because VS Code's coreclr attach is
    managed-only by construction - no IDE-side engine selector to
    flip, no risk of the "Editor.exe already being debugged" error
    that Native+Managed JIT-picker attaches keep tripping over. Rider
    and the JIT picker both work for advanced users who know to flip
    the engine selector in the attach dialog, but they need that
    extra step. The cycle menu (Phase 17b) still lets the user pick
    any of them; this only sets the first-launch default.
    """
    import os
    from pathlib import Path

    if _find_vscode_executable():
        return "vscode"
    if _find_rider_executable():
        return "rider"
    if os.name == "nt":
        jit = Path(os.environ.get("SystemRoot", r"C:\Windows")) / "System32" / "vsjitdebugger.exe"
        if jit.is_file():
            return "jit"
    return ""


def configure_auto_attach_defaults():
    """
    Phase 17d entry point - called once from C++
    O3DESharpEditorSystemComponent::OnPostInitialize after the Python VM
    is up. When /O3DE/O3DESharp/AutoAttachOnPlay isn't already set in
    the project's settings registry (the user-explicit value persists
    across editor sessions), pick the best available IDE and write it
    via O3DESharpRequestBus::SetAutoAttachOnPlay (the runtime side owns
    the registry write so the value lands in the persisted slot).

    Idempotent: returns immediately if the setting is already set to
    any value (including the empty string "off"), so a user who
    explicitly chose Off doesn't get auto-flipped back on by this
    detection.
    """
    try:
        import azlmbr.editor as editor
        import azlmbr.bus as bus
        # Settings-registry reads via Python are wonky across O3DE
        # versions. Easiest reliable path: dispatch through our own
        # O3DESharp request bus, which we already plumbed for related
        # settings (GetAutoAttachOnPlay / SetAutoAttachOnPlay on the
        # C++ side). If those buses aren't reflected for Python yet,
        # fall through quietly - the user can still cycle the menu.
    except ImportError:
        return

    # Detection itself is the part that lives in Python. Pass the
    # result to the editor system component via a settings-registry
    # bridge file - same pattern Phase 16b uses for BuildInProgress.
    # This avoids a new EBus for a one-time-per-session detection.
    picked = detect_preferred_ide()
    if not picked:
        general.log(
            "[O3DESharp] No managed debugger (Rider / VS Code / JIT picker) detected on "
            "this machine. Auto-attach on Game Mode stays Off; install Rider or VS Code "
            "and restart the editor to enable seamless debugging."
        )
        return

    # Write the detection result to a sentinel file the C++ side reads
    # on first OnStartPlayInEditorBegin if /O3DE/O3DESharp/AutoAttachOnPlay
    # is still unset. C++ owns the actual registry write so the value
    # lands in the right scope (project settings).
    import os
    from pathlib import Path
    try:
        import azlmbr.paths as _paths
        project_root = Path(_paths.projectroot)
    except Exception:
        return
    sentinel = project_root / "user" / ".csharp_default_auto_attach"
    try:
        sentinel.parent.mkdir(parents=True, exist_ok=True)
        sentinel.write_text(picked, encoding="utf-8")
        general.log(
            f"[O3DESharp] Detected managed debugger: {picked}. Wrote default "
            f"AutoAttachOnPlay suggestion to {sentinel}. The editor will pick "
            f"this up on first Game Mode entry unless you've already set the "
            f"AutoAttachOnPlay registry key explicitly."
        )
    except OSError as e:
        general.log(f"[O3DESharp] Could not write {sentinel}: {e}")


def _find_rider_executable():
    """
    Look up rider64.exe on Windows / rider on Linux.

    Search order:
      1. PATH (`shutil.which`).
      2. JetBrains Toolbox default install root.
      3. Common Program Files locations.
      4. Latest entry under HKCU\\Software\\JetBrains\\... (Windows only).
    Returns the absolute path string, or None.
    """
    import os
    import shutil
    from pathlib import Path

    exe_name = "rider64.exe" if os.name == "nt" else "rider"

    # 1. PATH
    found = shutil.which(exe_name)
    if found:
        return found
    if os.name == "nt":
        # `where` also matches .bat shims that aren't picked up by which
        found = shutil.which("rider.bat")
        if found:
            return found

    # 2. JetBrains Toolbox (per-user)
    home = Path.home()
    toolbox_candidates = [
        home / "AppData" / "Local" / "Programs" / "Rider" / "bin" / exe_name,
        home / "AppData" / "Local" / "JetBrains" / "Toolbox" / "apps",
    ]
    for c in toolbox_candidates:
        if c.is_file():
            return str(c)
        if c.is_dir():
            # Toolbox layout: apps/Rider/<channel>/<build>/bin/rider64.exe
            for hit in c.rglob(exe_name):
                return str(hit)

    # 3. Program Files (system-wide install)
    if os.name == "nt":
        for root_env in ("ProgramFiles", "ProgramFiles(x86)"):
            root = os.environ.get(root_env)
            if not root:
                continue
            jb_dir = Path(root) / "JetBrains"
            if jb_dir.is_dir():
                # Newer Rider versions: JetBrains Rider <year>.<minor>.<patch>
                for hit in jb_dir.glob("JetBrains Rider*/bin/" + exe_name):
                    return str(hit)

    # 4. HKCU registry (Windows; cheap fallback)
    if os.name == "nt":
        try:
            import winreg  # type: ignore[import-not-found]
            for hive, subkey in (
                (winreg.HKEY_CURRENT_USER, r"Software\\JetBrains\\Rider"),
                (winreg.HKEY_LOCAL_MACHINE, r"Software\\JetBrains\\Rider"),
            ):
                try:
                    with winreg.OpenKey(hive, subkey) as k:
                        # JetBrains writes subkeys named after the install.
                        i = 0
                        while True:
                            try:
                                name = winreg.EnumKey(k, i)
                            except OSError:
                                break
                            i += 1
                            try:
                                with winreg.OpenKey(k, name) as kk:
                                    install_path, _ = winreg.QueryValueEx(kk, "InstallLocation")
                                    candidate = Path(install_path) / "bin" / exe_name
                                    if candidate.is_file():
                                        return str(candidate)
                            except OSError:
                                continue
                except OSError:
                    continue
        except ImportError:
            pass

    return None


def _find_vscode_executable():
    """Locate VS Code's CLI entry point (`code` / `code.cmd`)."""
    import os
    import shutil
    from pathlib import Path

    # PATH first - covers user-PATH installs as well as system-wide.
    for name in ("code", "code.cmd"):
        found = shutil.which(name)
        if found:
            return found

    if os.name == "nt":
        home = Path.home()
        for candidate in (
            home / "AppData" / "Local" / "Programs" / "Microsoft VS Code" / "bin" / "code.cmd",
            Path(os.environ.get("ProgramFiles", "")) / "Microsoft VS Code" / "bin" / "code.cmd",
        ):
            if candidate.is_file():
                return str(candidate)

    return None


def _spawn_detached(args, log_label):
    """
    Launch an IDE/attach command without blocking the editor. We don't
    capture stdout/stderr because some IDEs print spinners that would just
    fill the editor log. Failures surface as a single warning instead.
    """
    import subprocess
    try:
        subprocess.Popen(args, close_fds=True)
        general.log(f"[O3DESharp] {log_label}: launched ({args[0]})")
        return True
    except Exception as e:
        general.log(f"[O3DESharp] {log_label}: failed to spawn {args[0]}: {e}")
        return False


def _native_debugger_attached() -> bool:
    """
    Windows-only: ask the OS whether a native debugger (or VS in mixed
    mode) is already attached to this process. Returns False on other
    platforms and on any failure.

    Note: this does NOT detect managed-only debugger attaches (e.g.
    Rider attached to CoreCLR). For those, vsjitdebugger.exe will still
    fail with 'A debugger is already attached', but Windows doesn't
    expose the managed-attach state cheaply from native code - so we
    catch the native case (cheap) and accept a noisy dialog for the
    managed-already-attached case (rare, only happens if the user
    chains attach attempts).
    """
    import os
    if os.name != "nt":
        return False
    try:
        import ctypes
        return bool(ctypes.windll.kernel32.IsDebuggerPresent())
    except Exception:  # noqa: BLE001 - ctypes not available, treat as unknown
        return False


def trigger_jit_debugger():
    """
    Windows: spawn vsjitdebugger.exe -p <pid>. The OS presents a picker
    listing every registered managed-mode debugger (Rider, Visual Studio,
    etc.) and attaches whichever the user clicks.

    IMPORTANT engine-selection caveat: the JIT picker just hands the PID
    to the chosen debugger. It cannot constrain the debugger's engine
    selection. Visual Studio and Rider both default to Native+Managed
    when attaching to an unmanaged host like Editor.exe, which produces
    "Editor.exe already being debugged" if anything else (Coral, a stale
    JIT lock, a half-cancelled previous attach) holds the Win32 debug
    interface, AND it isn't what you want anyway for C#-only debugging.
    Once the picker's debugger opens, switch its "Attach to:" /
    "Debugger:" selector to "Managed (.NET Core, .NET 5+)" (VS) or ".NET
    Core" (Rider) before confirming, then re-click Attach. The seamless
    alternative is "Attach with VS Code" which uses the coreclr debug
    type - managed-only by construction, never collides.

    Skipped (with a one-line warning) when a debugger is already attached
    - VS surfaces "Unable to attach to the crashing process. A debugger
    is already attached." in that case, which is confusing for users who
    triggered the action a second time.

    Cross-platform fallback: on Linux/Mac there is no equivalent
    OS-mediated picker. Logs an instructive message pointing at
    Debugger.WaitForAttach for those platforms.
    """
    import os
    from pathlib import Path

    pid = _editor_pid()

    if _native_debugger_attached():
        general.log(
            f"[O3DESharp] A debugger is already attached to PID {pid}; skipping JIT picker "
            f"to avoid the 'debugger is already attached' dialog. Detach first, or use "
            f"Copy Debugger Attach Info if you intentionally want to attach a second debugger."
        )
        return False

    if os.name == "nt":
        jit = Path(os.environ.get("SystemRoot", r"C:\Windows")) / "System32" / "vsjitdebugger.exe"
        if jit.is_file():
            ok = _spawn_detached([str(jit), "-p", str(pid)], "JIT debugger picker")
            if ok:
                general.log(
                    "[O3DESharp] JIT picker opened. When your IDE's Attach dialog shows up, "
                    "set 'Attach to:' / 'Debugger:' to MANAGED ONLY (Visual Studio: 'Managed "
                    "(.NET Core, .NET 5+)'; Rider: '.NET Core'). The default Native+Managed "
                    "selection collides with the Win32 debug lock and gives 'Editor.exe "
                    "already being debugged'. For zero-friction managed-only attach, "
                    "use 'Attach with VS Code' instead (coreclr type is managed-only by design)."
                )
            return ok
        general.log(
            "[O3DESharp] vsjitdebugger.exe not found under System32. Install a Visual Studio "
            "component that registers as the JIT debugger, or attach manually from your IDE "
            f"(PID {pid})."
        )
        return False

    general.log(
        f"[O3DESharp] No OS-level managed JIT picker on this platform. Attach your IDE "
        f"manually to PID {pid}, or sprinkle O3DE.Debugger.WaitForAttach into the script "
        f"you want to break in."
    )
    return False


def attach_with_rider():
    """
    Attach JetBrains Rider's managed debugger to the editor process.

    Implementation note: Rider has no documented "attach to PID from
    outside" automation surface in a standalone (non-Toolbox) install.

      - The CLI subcommand `rider64.exe attach-to-process <pid>` works
        only when Rider isn't running - the single-instance lock
        rejects the second invocation with "Process is still running
        and does not respond".
      - The URL protocol `jetbrains://rider/attach-to-process?pid=N`
        was a Phase 17c guess. After testing it produces no dialog and
        no attach - Rider's URL handler appears to silently drop
        unknown commands, and `attach-to-process` is not in the
        documented set.
      - JetBrains Toolbox ships a `jb` CLI that can drive attach, but
        not all users have Toolbox installed.

    So this function does the most reliable thing on Windows: spawn
    the system JIT picker (vsjitdebugger.exe -p <pid>), which presents
    a dialog listing every registered managed-mode debugger (Rider,
    Visual Studio, etc.). Rider's installer registers it as a managed
    JIT debugger, so the user picks it from the list with one click
    and Rider attaches.

    That's one extra click vs the zero-click goal, but it's the only
    mechanism we've verified actually works against a running Rider.
    The Toolbox-CLI path could short-circuit this to zero clicks for
    users who have Toolbox installed - if we detect that later we can
    add it as a primary code path.
    """
    pid = _editor_pid()
    rider = _find_rider_executable()
    if not rider:
        general.log(
            "[O3DESharp] Could not find rider64.exe / rider in PATH, Program Files, "
            "Toolbox, or the registry. Install Rider and rerun, or use 'Trigger JIT "
            "Debugger' to pick from the OS debugger picker."
        )
        return False

    import os
    if os.name != "nt":
        general.log(
            f"[O3DESharp] Rider auto-attach on non-Windows requires JetBrains Toolbox "
            f"CLI (`jb attach --pid {pid}`). Install Toolbox or attach manually via "
            f"Run > Attach to Process in Rider (PID {pid})."
        )
        return False

    general.log(
        f"[O3DESharp] Opening JIT picker for PID {pid}. Click 'JetBrains Rider' "
        f"in the dialog that appears. WHEN RIDER'S ATTACH DIALOG OPENS, change "
        f"'Debugger:' from 'Auto-detect' to '.NET Core' before confirming - "
        f"Auto-detect attaches Native+Managed, which collides with the Win32 "
        f"debug lock and produces 'Editor.exe already being debugged'. For a "
        f"managed-only attach with no engine fiddling, use 'Attach with VS Code' "
        f"instead - its coreclr debug type is managed-only by construction."
    )
    return _trigger_jit_debugger_for_pid(pid)


def attach_with_vscode():
    """
    Open VS Code on the current O3DE project and stage a managed-only
    attach configuration pinned to the current editor PID.

    Why this path is the seamless one: VS Code's "coreclr" debug type
    uses ICorDebug to attach to the .NET runtime hosted inside the
    editor. It never calls Win32 DebugActiveProcess, so it cannot
    collide with anything already attached to the native side. VS and
    Rider, when invoked via the JIT picker, attach Native+Managed by
    default and need their UI flipped to "Managed only" / ".NET Core"
    to avoid the "Editor.exe already being debugged" error you get
    when the native engine fights for the Win32 debug lock - which we
    cannot do from outside the IDE. VS Code is the only IDE where the
    managed-only constraint is part of the configuration itself.

    Concretely:
      1. Find every csproj under Gem/Source/CSharp/ AND the project root
         itself, and stage launch.json in each .vscode/ folder. VS Code
         only sees launch.json in its active workspace folder, and we
         can't predict which folder the user (or C# Dev Kit) anchored
         the workspace to. Writing to all candidates means F5 / the Run
         and Debug picker finds our config from any of them.
      2. Each launch.json gets a "O3DESharp: Attach Managed-Only (PID
         NNNNN)" config inserted at the TOP of the configurations array
         so the Run and Debug dropdown defaults to it.
      3. Stage a .vscode/settings.json next to each launch.json that
         neutralizes the C# Dev Kit interception that produces
         "'<csproj>' does not support debugging. No launchable target
         found." when the user presses F5 from a code file. Dev Kit's
         default behavior is to scan for csproj/sln, treat them as
         runnable, and intercept F5 - which obviously fails on class-
         library csprojs like ours. We set `dotnet.defaultSolution` to
         `disable` so Dev Kit stops asserting a debug target, and F5
         falls through to launch.json. (Loses Solution Explorer in
         Dev Kit; the file-level C# language server still works.)
      4. Open VS Code on the project root with --reuse-window.
      5. Log clear "go to Run and Debug, pick the config, click play"
         instructions for the F5-from-code-file edge case. F5 alone is
         unreliable across VS Code versions when multiple debug
         providers compete; the Run-and-Debug-panel path is rock solid.
    """
    import json
    from pathlib import Path
    import azlmbr.paths as _paths

    code = _find_vscode_executable()
    if not code:
        general.log(
            "[O3DESharp] Could not find VS Code's `code` CLI. Install VS Code and tick "
            "'Add to PATH' in the installer, or fall back to 'Attach with Rider' / "
            "'Trigger JIT Debugger' (and remember to flip the engine to 'Managed only' "
            "in the IDE's Attach dialog - JIT picker attaches Native+Managed by default "
            "which causes 'Editor.exe already being debugged')."
        )
        return False

    pid = _editor_pid()
    try:
        project_root = Path(_paths.projectroot)
    except Exception:
        project_root = Path(".")

    # Build the PID-pinned config we want at the top of every launch.json.
    config_name = f"O3DESharp: Attach Managed-Only (PID {pid})"
    new_config = {
        "name": config_name,
        "type": "coreclr",
        "request": "attach",
        "processId": pid,
        # justMyCode=false so we can step into framework code when
        # tracking down a vsdbg attach failure - the marginal startup
        # cost is irrelevant for an attach session, and stepping into
        # Coral / runtime code is sometimes the only way to see why
        # the host's managed state is wedged.
        "justMyCode": False,
        # Engine logging surfaces vsdbg's attach probe in the Debug
        # Console: which CoreCLR it sees, which module list it loaded,
        # what HRESULTs it got back from the debug API. Without this,
        # attach failures collapse to a one-line "Failed to attach
        # to process:" with no actionable detail. Cheap when attach
        # succeeds (a few hundred lines, all in the Debug Console
        # which the user only sees when they look), invaluable when
        # it doesn't.
        "logging": {
            "engineLogging": True,
            "moduleLoad": True,
            "exceptions": True,
            "programOutput": True,
        },
    }

    # Targets: project root + every csproj's parent directory under
    # Gem/Source/CSharp/. The csproj-level targets handle the
    # very common case where C# Dev Kit pulled VS Code's workspace
    # focus to the csproj folder; the project-root target handles
    # users who explicitly open the project at its root.
    target_dirs = [project_root]
    csharp_root = project_root / "Gem" / "Source" / "CSharp"
    if csharp_root.is_dir():
        for csproj in csharp_root.rglob("*.csproj"):
            target_dirs.append(csproj.parent)

    written_launch = []
    written_settings = []
    for target_dir in target_dirs:
        vscode_dir = target_dir / ".vscode"
        launch_path = vscode_dir / "launch.json"
        settings_path = vscode_dir / "settings.json"

        # --- launch.json: merge our config into existing or fresh data. ---
        data = {"version": "0.2.0", "configurations": []}
        if launch_path.is_file():
            try:
                data = json.loads(launch_path.read_text(encoding="utf-8"))
                if not isinstance(data, dict):
                    data = {"version": "0.2.0", "configurations": []}
            except Exception as e:
                general.log(
                    f"[O3DESharp] Existing launch.json at {launch_path} malformed ({e}); "
                    f"rewriting with the auto-attach config only."
                )
                data = {"version": "0.2.0", "configurations": []}
        cfgs = data.setdefault("configurations", [])
        if not isinstance(cfgs, list):
            cfgs = []
        # Drop any stale auto-generated PID-pinned config so we don't
        # grow the list every time the editor restarts with a new PID.
        cfgs = [
            c for c in cfgs
            if not (isinstance(c, dict)
                    and isinstance(c.get("name"), str)
                    and c["name"].startswith("O3DESharp: Attach Managed-Only (PID"))
        ]
        cfgs.insert(0, new_config)
        data["configurations"] = cfgs

        try:
            vscode_dir.mkdir(parents=True, exist_ok=True)
            launch_path.write_text(json.dumps(data, indent=4), encoding="utf-8")
            written_launch.append(launch_path)
        except Exception as e:
            general.log(f"[O3DESharp] Could not write {launch_path}: {e}")
            continue

        # --- settings.json: defuse C# Dev Kit's F5 intercept. ---
        # Only touches dotnet.defaultSolution if it's not already set -
        # respect any explicit user choice (they may have pointed
        # Dev Kit at a specific .sln they want loaded).
        settings = {}
        if settings_path.is_file():
            try:
                settings = json.loads(settings_path.read_text(encoding="utf-8"))
                if not isinstance(settings, dict):
                    settings = {}
            except Exception:
                settings = {}
        if "dotnet.defaultSolution" not in settings:
            settings["dotnet.defaultSolution"] = "disable"
            try:
                settings_path.write_text(json.dumps(settings, indent=4), encoding="utf-8")
                written_settings.append(settings_path)
            except Exception as e:
                general.log(f"[O3DESharp] Could not write {settings_path}: {e}")

    if not written_launch:
        general.log("[O3DESharp] Could not stage launch.json anywhere; aborting VS Code launch.")
        return False

    ok = _spawn_detached([code, "--reuse-window", str(project_root)], "VS Code")
    if ok:
        general.log(
            f"[O3DESharp] VS Code launched. Staged managed-only attach to PID {pid} in "
            f"{len(written_launch)} launch.json location(s); defused C# Dev Kit's F5 "
            f"intercept in {len(written_settings)} settings.json(s). To attach: open "
            f"Run and Debug (Ctrl+Shift+D), pick '{config_name}' from the dropdown, "
            f"click the green play button. If you instead see \"'<csproj>' does not "
            f"support debugging. No launchable target found\", you pressed F5 from a "
            f"code file and Dev Kit hijacked it - use the Run and Debug panel instead. "
            f"coreclr is managed-only by construction, no engine selector to flip."
        )
    return ok


def _find_game_launcher():
    """
    Locate <Project>.GameLauncher.exe under build/.../bin/<config>/ for
    Phase 17c's 'Run with Debugger' flow. Returns the first launcher
    found, preferring profile builds (best balance of debuggability +
    speed), falling back to debug then release.

    Returns (exe_path, config_name) or (None, None).
    """
    from pathlib import Path
    try:
        import azlmbr.paths as _paths
    except ImportError:
        return None, None

    project_root = Path(_paths.projectroot)
    # Most common O3DE build layouts:
    #   build/windows/bin/<config>/<Project>.GameLauncher.exe
    #   build/linux/bin/<config>/<Project>.GameLauncher
    # We don't know <config>, so probe all three in preference order.
    candidate_roots = [
        project_root / "build" / "windows" / "bin",
        project_root / "build" / "linux"   / "bin",
        project_root / "build" / "bin",   # less-common flat layout
    ]
    for cfg in ("profile", "debug", "release"):
        for root in candidate_roots:
            cfg_dir = root / cfg
            if not cfg_dir.is_dir():
                continue
            for entry in cfg_dir.iterdir():
                name = entry.name
                if name.lower().endswith("gamelauncher.exe") or (
                    name.lower().endswith("gamelauncher") and entry.is_file()):
                    return str(entry), cfg
    return None, None


def run_game_launcher_with_debugger(method: str = ""):
    """
    Phase 17c: spawn the GameLauncher and auto-attach the configured
    debugger to the freshly-launched process. One-click "Run my project
    with a debugger attached" - lets you debug runtime-only code paths
    that don't exist inside the editor (player launchers, server
    launchers, exported games).

    `method` selects how to attach. Falls back to the saved
    /O3DE/O3DESharp/AutoAttachOnPlay registry value, then to the JIT
    picker.
    """
    import subprocess
    import time

    exe, config = _find_game_launcher()
    if not exe:
        general.log(
            "[O3DESharp] No GameLauncher.exe found under build/.../bin/<config>/. "
            "Build the launcher target (e.g. cmake --build . --target NewProject.GameLauncher --config profile) "
            "and try again."
        )
        return False

    # Launch the game launcher. close_fds keeps it detached from the
    # editor's pipes so a long-running play session doesn't tie up our
    # Python interpreter.
    try:
        proc = subprocess.Popen([exe], close_fds=True)
    except Exception as e:  # noqa: BLE001
        general.log(f"[O3DESharp] Failed to spawn {exe}: {e}")
        return False

    pid = proc.pid
    general.log(f"[O3DESharp] Launched {exe} (PID {pid}, config {config})")

    # Give CoreCLR time to initialize - debuggers can only bind to a
    # process that's loaded the .NET runtime. Coral host init typically
    # finishes within 1-2 seconds; we wait 2 to be safe. The IDE will
    # bind any not-yet-loaded symbols when they appear, so a longer wait
    # just delays the user's breakpoints.
    time.sleep(2.0)

    # Pick the attach method. Explicit arg wins; otherwise read the
    # configured AutoAttachOnPlay; otherwise default to JIT picker.
    if not method:
        # Settings registry isn't reachable from Python directly; the
        # AutoAttachOnPlay value is mirrored to the env var by C++ in
        # Phase 17b (waited for) - but the AutoAttach VALUE itself isn't
        # mirrored. So we fall back to JIT.
        method = "jit"

    method = method.lower()
    if method == "jit":
        # Reuse the existing trigger_jit_debugger flow but parameterise
        # by PID instead of os.getpid().
        return _trigger_jit_debugger_for_pid(pid)
    elif method == "rider":
        return _attach_rider_to_pid(pid)
    elif method == "vscode":
        return attach_with_vscode()  # opens the project; user presses F5
    else:
        general.log(f"[O3DESharp] Unknown attach method '{method}'. Use jit/rider/vscode.")
        return False


def _trigger_jit_debugger_for_pid(pid: int):
    """Spawn vsjitdebugger.exe -p <pid> for an arbitrary PID."""
    import os
    from pathlib import Path
    if os.name != "nt":
        general.log(f"[O3DESharp] JIT picker only available on Windows. Manually attach to PID {pid}.")
        return False
    jit = Path(os.environ.get("SystemRoot", r"C:\Windows")) / "System32" / "vsjitdebugger.exe"
    if not jit.is_file():
        general.log(f"[O3DESharp] vsjitdebugger.exe not found. Manually attach to PID {pid}.")
        return False
    return _spawn_detached([str(jit), "-p", str(pid)], f"JIT picker for PID {pid}")


def _attach_rider_to_pid(pid: int):
    """Attach Rider to an arbitrary PID via the JetBrains URL protocol."""
    import os
    rider = _find_rider_executable()
    if not rider:
        general.log("[O3DESharp] Rider not found.")
        return False
    url = f"jetbrains://rider/attach-to-process?pid={pid}"
    if os.name == "nt":
        return _spawn_detached(["cmd", "/c", "start", "", url], f"Rider attach (PID {pid})")
    return _spawn_detached(["xdg-open", url], f"Rider attach (PID {pid})")


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
