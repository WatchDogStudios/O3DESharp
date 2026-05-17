/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

#pragma once

#include <AzCore/std/string/string.h>
#include <AzCore/std/containers/unordered_set.h>

#if !defined(Q_MOC_RUN)
#include <QObject>
#include <QString>
#endif

class QFileSystemWatcher;
class QTimer;

namespace O3DESharp
{
    /**
     * Editor-only file watcher that auto-reloads C# user assemblies when DLL
     * files change in the project's Bin/Scripts/ directory.
     *
     * The actual reload is handed off to O3DESharpRequestBus::ReloadUserAssemblies,
     * which goes through CoralHostManager::ReloadUserAssemblies and the
     * O3DESharpHotReloadNotificationBus (Phase 13). This class is purely the
     * "when do we trigger that?" half - it owns:
     *   - a QFileSystemWatcher subscribed to <ProjectPath>/Bin/Scripts/
     *   - a QTimer used as a debounce so we don't react to half-written DLLs
     *     during a multi-chunk dotnet build write
     *
     * Why Qt instead of AzFramework::FileWatcher?
     *   AzFramework does NOT export a public FileWatcher. The Code/Tools/
     *   AssetProcessor/native/FileWatcher implementation is internal to AP.
     *   QFileSystemWatcher is already linked through the editor's Qt usage,
     *   fires its signals on the Qt main thread (which is also O3DE's main
     *   thread inside the editor), and works on all three platforms O3DE
     *   supports. No thread marshaling required - reloads land directly on
     *   the right thread.
     *
     * Lives in the .Editor module only - never linked into the runtime gem,
     * standalone launchers, or server launchers. Game builds load their
     * assemblies once at boot and never need a watcher.
     */
    class CSharpAssemblyWatcher : public QObject
    {
        Q_OBJECT

    public:
        explicit CSharpAssemblyWatcher(QObject* parent = nullptr);
        ~CSharpAssemblyWatcher() override;

        /**
         * Starts watching binScriptsPath for *.dll changes.
         *
         * Called from O3DESharpEditorSystemComponent::Activate once the runtime
         * gem has had a chance to spin up the Coral host (we need the
         * O3DESharpRequestBus handler online to dispatch reloads).
         *
         * Safe to call when the path doesn't yet exist - the watcher will
         * just have nothing to watch until something creates it. Re-running
         * Start() on an already-started watcher replaces the path.
         */
        void Start(const AZStd::string& binScriptsPath, int debounceMs);

        /**
         * Stops the watcher. Called from Deactivate. Cancels any pending
         * debounced reload.
         */
        void Stop();

        /**
         * Returns true if Start has been called and not yet Stopped.
         */
        bool IsRunning() const;

    private slots:
        void OnDirectoryChanged(const QString& path);
        void OnFileChanged(const QString& path);
        void OnDebounceElapsed();

    private:
        // Dispatched by OnDebounceElapsed once the user has stopped touching
        // the directory for the configured debounce window. Broadcasts on
        // O3DESharpRequestBus to do the actual reload through Coral.
        void DispatchReload();

        QFileSystemWatcher* m_watcher = nullptr;
        QTimer* m_debounceTimer = nullptr;

        AZStd::string m_watchedDirectory;
        int m_debounceMs = 500;

        // Tracks which files in the watched directory we've subscribed to
        // individually (QFileSystemWatcher needs file-level adds for some
        // platforms to fire on content changes, not just adds/removes).
        AZStd::unordered_set<AZStd::string> m_individuallyWatchedFiles;

        // Set to true while we're known to be in the middle of a build. The
        // dispatcher reschedules the reload until this clears so we don't try
        // to load a half-written DLL.
        bool m_buildInProgress = false;
    };
} // namespace O3DESharp
