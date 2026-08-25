#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
# Produces a private, self-contained .NET runtime bundle for the target RID so
# a shipped game can host C# via Coral without a machine-wide .NET install.
# Opt-in via O3DESHARP_BUNDLE_DOTNET_RUNTIME (default OFF): the default build
# stays framework-dependent and is unchanged.
#
# NOTE on deployment (read this before wiring the deploy block): unlike
# Coral.Managed / O3DE.Core - a small, fixed set of files whose names are
# known at configure time - the runtime bundle's file list is NOT knowable
# until `dotnet publish --self-contained` has actually run. It varies by RID
# and by whatever .NET SDK/runtime pack gets resolved (empirically ~189
# files for win-x64, all flat - no culture/subfolder nesting - verified by
# actually running the publish; see Editor/Tests/test_runtime_bundle_contents.py
# and the M2 plan's Task 3 verification notes). Because ly_add_target_files()
# (the declarative "stage now, copy later" idiom used by StageCoral /
# StageO3DECore in CMakeLists.txt) requires an explicit FILES list and its
# underlying copy script does a per-file file(SIZE) freshness check that
# errors out on a directory, the CMakeLists.txt deploy block globs the
# staged bundle directory rather than hard-coding filenames. Practical
# consequence: on the first configure after turning the option on, the
# bundle directory does not exist yet (this target only produces it at BUILD
# time), so the glob is empty and nothing is queued for deploy that
# configure. Build ${gem_name}.StageRuntimeBundle once, then re-run CMake
# configure so the glob picks up the produced files. See README.md
# "Shipping without requiring .NET (experimental)".

function(o3de_sharp_stage_runtime_bundle out_dir_var)
    # Map CMake system + processor to a .NET RID.
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
    set(_probe "${_gem_root}/Code/Tools/RuntimeBundle/probe/probe.csproj")
    set(_bundle "${CMAKE_BINARY_DIR}/Gems/O3DESharp/RuntimeBundle/${_rid}")

    # Mirror the graceful-degradation pattern used for O3DE.Core above (this
    # file is a `Create` deliverable of an earlier task; an install/export
    # tree that ships Code/ without Code/Tools/RuntimeBundle should not fail
    # configure over an experimental, opt-in feature).
    if(NOT EXISTS "${_probe}")
        message(WARNING
            "O3DESharp: O3DESHARP_BUNDLE_DOTNET_RUNTIME is ON but the runtime "
            "probe project is missing at ${_probe}. Skipping runtime bundle staging.")
        return()
    endif()

    add_custom_target(${gem_name}.StageRuntimeBundle ALL
        COMMENT "O3DESharp: staging private .NET runtime bundle (${_rid})"
        COMMAND ${CMAKE_COMMAND} -E make_directory "${_bundle}"
        COMMAND ${DOTNET_EXECUTABLE} publish "${_probe}"
                -c Release -r ${_rid} --self-contained true
                -o "${_bundle}"
        VERBATIM
    )

    # Computed locally instead of relying on the including CMakeLists.txt's
    # relative_o3desharp_gem_root variable: that variable is (pre-existing,
    # cosmetic-only) referenced by the StageCoral/StageO3DECore FOLDER
    # properties before it is actually assigned further down that file, so
    # depending on it here would be equally order-fragile. Self-contained is
    # cheap and correct regardless of where this function gets called from.
    ly_get_engine_relative_source_dir(${_gem_root} _relative_gem_root)
    set_property(TARGET ${gem_name}.StageRuntimeBundle
        PROPERTY FOLDER "${_relative_gem_root}/Deploy")

    set(${out_dir_var} "${_bundle}" PARENT_SCOPE)
endfunction()
