#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
# Runs `dotnet build -c Release` on CSPROJ if it exists, using the DOTNET
# executable path passed in. No-ops (exit 0) if CSPROJ doesn't exist yet -
# same graceful-degradation shape as o3desharp_copy_if_exists.cmake, for
# the same reason: a fresh clone with no reflection_data.json yet hasn't
# produced anything for BuildGeneratedBindings to build, and that must not
# fail configure/build over an experimental zero-config feature.
if(EXISTS "${CSPROJ}")
    execute_process(
        COMMAND "${DOTNET}" build "${CSPROJ}" -c Release --nologo
        RESULT_VARIABLE _build_result
    )
    if(NOT _build_result EQUAL 0)
        message(WARNING "O3DESharp: dotnet build failed for ${CSPROJ} (exit ${_build_result})")
    endif()
else()
    message(STATUS "O3DESharp: ${CSPROJ} not found yet - launch the Editor once to produce reflection_data.json, then rebuild.")
endif()
