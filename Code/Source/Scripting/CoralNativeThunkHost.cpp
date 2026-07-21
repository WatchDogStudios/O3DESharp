/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */
#include "CoralNativeThunkHost.h"

#include <AzCore/Console/ILogger.h>
#include <Coral/HostInstance.hpp>
#include <Coral/StringHelper.hpp>

#include <filesystem>

namespace O3DESharp
{
    void CoralNativeThunkHost::SetHost(Coral::HostInstance* host, const AZ::IO::Path& scriptsDir)
    {
        if (m_host != host || m_scriptsDir != scriptsDir)
        {
            // Cached pointers belong to the previous host's runtime / layout.
            m_cache.clear();
            m_host = host;
            m_scriptsDir = scriptsDir;
        }
    }

    CoralNativeThunkHost::PinnedThunk CoralNativeThunkHost::Get(
        AZStd::string_view assemblyFileName,
        AZStd::string_view assemblyQualifiedTypeName,
        AZStd::string_view methodName)
    {
        if (m_host == nullptr || m_scriptsDir.empty())
        {
            return nullptr;
        }

        AZStd::string key(assemblyFileName);
        key += "!";
        key += assemblyQualifiedTypeName;
        key += "::";
        key += methodName;

        auto it = m_cache.find(key);
        if (it != m_cache.end())
        {
            return it->second;
        }

        // Coral takes an assembly PATH plus UCChar strings. UCChar is wchar_t on
        // Windows and char on Linux, and CORAL_STR() only works on literals, so
        // runtime strings must go through ConvertUtf8ToWide. Paid once per
        // unique key because the result (including nullptr) is memoized below.
        //
        // Composing the path via AZStd::string(...).c_str() (rather than
        // AZ::IO::PathView) matches the existing precedent in this gem - see
        // O3DESharpSystemComponent.cpp's userAssemblyPaths construction.
        const AZ::IO::Path assemblyPath = m_scriptsDir / AZStd::string(assemblyFileName).c_str();

        Coral::UCString typeNameUC = Coral::StringHelper::ConvertUtf8ToWide(
            std::string_view(assemblyQualifiedTypeName.data(), assemblyQualifiedTypeName.size()));
        Coral::UCString methodNameUC = Coral::StringHelper::ConvertUtf8ToWide(
            std::string_view(methodName.data(), methodName.size()));

        PinnedThunk thunk = m_host->GetFunctionPointer(
            std::filesystem::path(assemblyPath.c_str()),
            typeNameUC.c_str(),
            methodNameUC.c_str());

        if (thunk == nullptr)
        {
            AZLOG_WARN(
                "[O3DESharp] No pinned thunk for %.*s::%.*s in %s - falling back to InvokeMethod",
                static_cast<int>(assemblyQualifiedTypeName.size()), assemblyQualifiedTypeName.data(),
                static_cast<int>(methodName.size()), methodName.data(),
                assemblyPath.c_str());
        }

        // Memoize even nullptr: a failed resolution is stable, and caching
        // it avoids re-attempting (and re-logging) on every frame.
        m_cache[key] = thunk;
        return thunk;
    }

    void CoralNativeThunkHost::InvalidateCache()
    {
        m_cache.clear();
    }

    size_t CoralNativeThunkHost::CachedCount() const
    {
        return m_cache.size();
    }
} // namespace O3DESharp
