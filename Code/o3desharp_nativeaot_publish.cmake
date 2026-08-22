#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
# Publishes O3DE.Core as a NativeAOT shared library for the shipping desktop
# build: no CoreCLR, no hostfxr, no Coral. NativeAotHost LoadLibrary/dlopen's
# the result and resolves one exported symbol.
#
# Opt-in via O3DESHARP_PUBLISH_NATIVEAOT (default OFF). The default build stays
# on the Coral path and is unchanged. The two artifacts are mutually exclusive
# per launcher - a NativeAOT image has no JIT and nothing for nethost -> hostfxr
# to attach to - which is why they deploy to different directories rather than
# both landing in Bin/Scripts.
#
# NOTE on deployment, same shape as the M2 runtime bundle: the published file
# set is not knowable at configure time (it varies by RID and by which
# ILCompiler resolves), so the CMakeLists.txt deploy block globs the published
# directory. Practical consequence: on the first configure after turning the
# option on the directory does not exist yet, the glob is empty, and nothing is
# queued. Build ${gem_name}.PublishNativeAot once, then re-run CMake configure.
#
# NOTE on the toolchain: ILCompiler's native link step shells out to vswhere.exe
# on Windows. A build launched from an environment without the Visual Studio
# Installer directory on PATH fails with MSB3073 exit code 123. CMake-driven
# builds from the VS generator inherit a developer environment and are fine;
# command-line builds from a bare shell may not be.

function(o3de_sharp_publish_nativeaot out_dir_var)
    # Desktop RIDs only. Console/mobile is the Mono milestone, not this one.
    if(WIN32)
        set(_rid "win-x64")
    elseif(APPLE)
        if(CMAKE_SYSTEM_PROCESSOR MATCHES "arm64|aarch64")
            set(_rid "osx-arm64")
        else()
            set(_rid "osx-x64")
        endif()
    else()
        set(_rid "linux-x64")
    endif()

    get_property(_gem_root GLOBAL PROPERTY "@GEMROOT:${gem_name}@")
    set(_csproj "${_gem_root}/Assets/Scripts/O3DE.Core/O3DE.Core.csproj")
    set(_out "${CMAKE_BINARY_DIR}/Gems/O3DESharp/NativeAot/${_rid}")

    # Same graceful degradation as the runtime bundle: an install/export tree
    # that ships Code/ without the C# sources must not fail configure over an
    # experimental, opt-in feature.
    if(NOT EXISTS "${_csproj}")
        message(WARNING
            "O3DESharp: O3DESHARP_PUBLISH_NATIVEAOT is ON but O3DE.Core.csproj is missing at "
            "${_csproj}. Skipping the NativeAOT publish.")
        return()
    endif()

    add_custom_target(${gem_name}.PublishNativeAot
        COMMENT "O3DESharp: publishing O3DE.Core as a NativeAOT shared library (${_rid})"
        COMMAND ${CMAKE_COMMAND} -E make_directory "${_out}"
        COMMAND ${DOTNET_EXECUTABLE} publish "${_csproj}"
                -c Release -r ${_rid}
                # NativeLib=Shared is what makes this a loadable library rather
                # than an executable; without it there is nothing to dlopen.
                -p:PublishAot=true
                -p:NativeLib=Shared
                # Without the host mode HostExportsGenerator emits no
                # O3DESharp_GetManagedExports and NativeAotHost cannot resolve
                # anything - the image loads and is then useless.
                -p:O3DESharpHostMode=NativeAot
                -o "${_out}"
        VERBATIM
    )

    # Computed locally rather than relying on the including file's
    # relative_o3desharp_gem_root, which is referenced above its own assignment
    # in CMakeLists.txt - same reasoning as o3desharp_runtime_bundle.cmake.
    ly_get_engine_relative_source_dir(${_gem_root} _relative_gem_root)
    set_property(TARGET ${gem_name}.PublishNativeAot
        PROPERTY FOLDER "${_relative_gem_root}/Deploy")

    set(${out_dir_var} "${_out}" PARENT_SCOPE)
endfunction()
