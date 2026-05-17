/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

#include "CSharpAssemblyWatcher.h"

#include <O3DESharp/O3DESharpBus.h>

#include <AzCore/IO/SystemFile.h>
#include <AzCore/Settings/SettingsRegistry.h>

#include <QDir>
#include <QFileInfo>
#include <QFileSystemWatcher>
#include <QStringList>
#include <QTimer>

namespace O3DESharp
{
    namespace
    {
        // Files we treat as "assembly content" worth reacting to. The .pdb
        // arrives separately on disk from the .dll but landing on the same
        // build burst, so debouncing dedupes them into a single reload.
        bool IsAssemblyFile(const QString& path)
        {
            const QString lower = path.toLower();
            return lower.endsWith(QStringLiteral(".dll"))
                || lower.endsWith(QStringLiteral(".pdb"));
        }
    } // namespace

    CSharpAssemblyWatcher::CSharpAssemblyWatcher(QObject* parent)
        : QObject(parent)
        , m_watcher(new QFileSystemWatcher(this))
        , m_debounceTimer(new QTimer(this))
    {
        // The debounce timer is single-shot. Every QFileSystemWatcher event
        // resets it; only when it actually fires (no events for debounceMs)
        // do we dispatch the reload.
        m_debounceTimer->setSingleShot(true);
        connect(m_debounceTimer, &QTimer::timeout, this, &CSharpAssemblyWatcher::OnDebounceElapsed);

        connect(m_watcher, &QFileSystemWatcher::directoryChanged,
            this, &CSharpAssemblyWatcher::OnDirectoryChanged);
        connect(m_watcher, &QFileSystemWatcher::fileChanged,
            this, &CSharpAssemblyWatcher::OnFileChanged);
    }

    CSharpAssemblyWatcher::~CSharpAssemblyWatcher()
    {
        Stop();
    }

    void CSharpAssemblyWatcher::Start(const AZStd::string& binScriptsPath, int debounceMs)
    {
        Stop();

        m_watchedDirectory = binScriptsPath;
        m_debounceMs = debounceMs > 0 ? debounceMs : 500;

        // QFileSystemWatcher::addPath rejects non-existent paths silently. If
        // Bin/Scripts/ doesn't exist yet (fresh project that hasn't built any
        // C# yet) we still want to wire the watcher up for when it appears;
        // create the directory to make that happen.
        QString qPath = QString::fromUtf8(binScriptsPath.c_str());
        QDir().mkpath(qPath);

        if (!m_watcher->addPath(qPath))
        {
            AZ_Warning(
                "O3DESharp", false,
                "CSharpAssemblyWatcher: failed to add directory watch on '%s'. "
                "Auto-reload will be inactive; use Tools > C# Scripting > Reload Scripts manually.",
                binScriptsPath.c_str());
            return;
        }

        // Subscribe to every existing DLL/PDB individually so we get
        // fileChanged signals on overwrites. directoryChanged only fires for
        // adds/removes/renames on most platforms.
        QDir dir(qPath);
        const QFileInfoList entries =
            dir.entryInfoList({ QStringLiteral("*.dll"), QStringLiteral("*.pdb") },
                              QDir::Files | QDir::NoDotAndDotDot);
        QStringList toWatch;
        for (const QFileInfo& info : entries)
        {
            toWatch << info.absoluteFilePath();
            m_individuallyWatchedFiles.insert(info.absoluteFilePath().toUtf8().constData());
        }
        if (!toWatch.isEmpty())
        {
            m_watcher->addPaths(toWatch);
        }

        AZ_TracePrintf(
            "O3DESharp",
            "CSharpAssemblyWatcher: watching '%s' for *.dll changes (debounce %d ms)\n",
            binScriptsPath.c_str(), m_debounceMs);
    }

    void CSharpAssemblyWatcher::Stop()
    {
        if (m_debounceTimer && m_debounceTimer->isActive())
        {
            m_debounceTimer->stop();
        }
        if (m_watcher)
        {
            const QStringList dirs = m_watcher->directories();
            if (!dirs.isEmpty())
            {
                m_watcher->removePaths(dirs);
            }
            const QStringList files = m_watcher->files();
            if (!files.isEmpty())
            {
                m_watcher->removePaths(files);
            }
        }
        m_individuallyWatchedFiles.clear();
        m_watchedDirectory.clear();
    }

    bool CSharpAssemblyWatcher::IsRunning() const
    {
        return !m_watchedDirectory.empty();
    }

    void CSharpAssemblyWatcher::OnDirectoryChanged(const QString& path)
    {
        // Directory event - usually means a new DLL appeared (build just
        // dropped one) or one was removed. Re-enumerate so newly added DLLs
        // get individual watches too.
        QDir dir(path);
        const QFileInfoList entries =
            dir.entryInfoList({ QStringLiteral("*.dll"), QStringLiteral("*.pdb") },
                              QDir::Files | QDir::NoDotAndDotDot);
        QStringList toAdd;
        for (const QFileInfo& info : entries)
        {
            const AZStd::string asString = info.absoluteFilePath().toUtf8().constData();
            if (m_individuallyWatchedFiles.find(asString) == m_individuallyWatchedFiles.end())
            {
                toAdd << info.absoluteFilePath();
                m_individuallyWatchedFiles.insert(asString);
            }
        }
        if (!toAdd.isEmpty())
        {
            m_watcher->addPaths(toAdd);
        }

        // Treat any directory event as "something assembly-shaped probably
        // changed". Start / extend the debounce window.
        m_debounceTimer->start(m_debounceMs);
    }

    void CSharpAssemblyWatcher::OnFileChanged(const QString& path)
    {
        if (!IsAssemblyFile(path))
        {
            return;
        }

        // Some editors / build pipelines replace a file by deleting + recreating
        // it, which removes the QFileSystemWatcher subscription. Re-add the file
        // path so we keep getting notifications.
        if (!QFileInfo::exists(path))
        {
            // File was removed; will get re-added in OnDirectoryChanged when
            // it reappears.
        }
        else if (!m_watcher->files().contains(path))
        {
            m_watcher->addPath(path);
        }

        m_debounceTimer->start(m_debounceMs);
    }

    void CSharpAssemblyWatcher::OnDebounceElapsed()
    {
        // Re-check the build-in-progress flag from the settings registry on
        // every fire so the user's C# project manager Build flow can set
        // it just before invoking dotnet build, and we'll wait until they
        // clear it.
        bool buildInProgress = false;
        if (auto* registry = AZ::SettingsRegistry::Get())
        {
            registry->Get(buildInProgress, "/O3DE/O3DESharp/BuildInProgress");
        }

        if (buildInProgress)
        {
            // Re-arm the timer; we'll re-check shortly. 200ms is short enough
            // that the user perceives the reload as immediate after build
            // finishes, but long enough not to spin.
            m_debounceTimer->start(200);
            return;
        }

        DispatchReload();
    }

    void CSharpAssemblyWatcher::DispatchReload()
    {
        AZ_Printf(
            "O3DESharp",
            "CSharpAssemblyWatcher: detected change in '%s', requesting user assembly reload\n",
            m_watchedDirectory.c_str());

        bool reloaded = false;
        O3DESharpRequestBus::BroadcastResult(reloaded, &O3DESharpRequests::ReloadUserAssemblies);

        if (!reloaded)
        {
            AZ_Warning(
                "O3DESharp", false,
                "CSharpAssemblyWatcher: ReloadUserAssemblies returned false. "
                "Check that hot-reload is enabled and the Coral host is initialized.");
        }
    }
} // namespace O3DESharp
