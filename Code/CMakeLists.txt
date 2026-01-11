#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
include(o3desharp_netverify.cmake)

# Currently we are in the Code folder: ${CMAKE_CURRENT_LIST_DIR}
# Get the platform specific folder ${pal_dir} for the current folder: ${CMAKE_CURRENT_LIST_DIR}/Platform/${PAL_PLATFORM_NAME}
# Note: o3de_pal_dir will take care of the details for us, as this may be a restricted platform
#       in which case it will see if that platform is present here or in the restricted folder.
#       i.e. It could here in our gem : Gems/O3DESharp/Code/Platform/<platorm_name>  or
#            <restricted_folder>/<platform_name>/Gems/O3DESharp/Code
o3de_pal_dir(pal_dir ${CMAKE_CURRENT_LIST_DIR}/Platform/${PAL_PLATFORM_NAME} "${gem_restricted_path}" "${gem_path}" "${gem_parent_relative_path}")

# Now that we have the platform abstraction layer (PAL) folder for this folder, thats where we will find the
# traits for this platform. Traits for a platform are defines for things like whether or not something in this gem
# is supported by this platform.
include(${pal_dir}/PAL_${PAL_PLATFORM_NAME_LOWERCASE}.cmake)

# Check to see if building the Gem Modules are supported for the current platform
# INTERNAL-NOTE(Mikael A.): Disable O3DESharp with coral support on Xbox, PS, Nintendo, breaks XR & TRC. use Mono only for those platforms.
if(NOT PAL_TRAIT_O3DESHARP_SUPPORTED)
    return()
endif()

# CORAL CLONE INFO
# ===========================
# NOTE(Mikael A.): Stay on main, do not change to internal coral branch!
set(CORAL_GIT_REPO "https://github.com/WatchDogStudios/Coral.git")
set(CORAL_GIT_TAG "main")

# Disable Coral examples and tests before FetchContent to speed up configuration
set(CORAL_EXAMPLE OFF CACHE BOOL "" FORCE)
set(CORAL_TESTING OFF CACHE BOOL "" FORCE)

include(FetchContent)

# Use GIT_SHALLOW to only fetch the latest commit (much faster than full clone)
# FIND_PACKAGE_ARGS allows using a pre-installed Coral if available
FetchContent_Declare(
    Coral
    GIT_REPOSITORY ${CORAL_GIT_REPO}
    GIT_TAG ${CORAL_GIT_TAG}
    GIT_SHALLOW TRUE
    GIT_PROGRESS TRUE
    SOURCE_SUBDIR cmake
)

# please always be really clear about what third parties your gem uses.
message(STATUS "O3DESharp Gem uses ${CORAL_GIT_REPO} (License: MIT)")

# Check if already populated to avoid redundant work
FetchContent_GetProperties(Coral)
if(NOT coral_POPULATED)
    message(STATUS "O3DESharp: Fetching Coral (this may take a moment on first run)...")
    FetchContent_Populate(Coral)
    add_subdirectory(${coral_SOURCE_DIR}/cmake ${coral_BINARY_DIR})
endif()

FetchContent_GetProperties(Coral)

# ============================================================
# Coral.Managed Deployment Target
# ============================================================
# Create a staging directory for Coral.Managed files that can be easily deployed to projects.
# This copies Coral.Managed.dll, runtimeconfig.json, and deps.json to a known location.

if(TARGET Coral.Managed)
    # Get Coral.Managed output directory from its target properties
    get_target_property(CORAL_MANAGED_OUTPUT Coral.Managed DOTNET_OUTPUT_DIR)
    if(NOT CORAL_MANAGED_OUTPUT)
        # Fallback: Coral's default output is in Build/<Config>
        set(CORAL_MANAGED_OUTPUT "${coral_BINARY_DIR}/Build/$<CONFIG>")
    endif()
    
    # Staging directory in the gem's bin folder
    get_property(gem_root_for_staging GLOBAL PROPERTY "@GEMROOT:${gem_name}@")
    set(CORAL_STAGING_DIR "${gem_root_for_staging}/bin/Coral")
    
    # Create a custom target to stage Coral.Managed files
    add_custom_target(${gem_name}.StageCoral ALL
        COMMENT "O3DESharp: Staging Coral.Managed files for deployment"
        COMMAND ${CMAKE_COMMAND} -E make_directory "${CORAL_STAGING_DIR}"
        COMMAND ${CMAKE_COMMAND} -E copy_if_different 
            "${CORAL_MANAGED_OUTPUT}/Coral.Managed.dll" 
            "${CORAL_STAGING_DIR}/Coral.Managed.dll"
        COMMAND ${CMAKE_COMMAND} -E copy_if_different 
            "${CORAL_MANAGED_OUTPUT}/Coral.Managed.runtimeconfig.json" 
            "${CORAL_STAGING_DIR}/Coral.Managed.runtimeconfig.json"
        COMMAND ${CMAKE_COMMAND} -E copy_if_different 
            "${CORAL_MANAGED_OUTPUT}/Coral.Managed.deps.json" 
            "${CORAL_STAGING_DIR}/Coral.Managed.deps.json"
        DEPENDS Coral.Managed
    )
    
    set_property(TARGET ${gem_name}.StageCoral PROPERTY FOLDER "${relative_o3desharp_gem_root}/Deploy")
    
    message(STATUS "O3DESharp: Coral.Managed will be staged to ${CORAL_STAGING_DIR}")
endif()

# ============================================================
# O3DE.Core Deployment Target
# ============================================================
# Create a staging directory for O3DE.Core.dll (the C# scripting API) that can be deployed to projects.
# This copies O3DE.Core.dll from its build output to a known staging location.
# NOTE: O3DE.Core.dll is built by the O3DE.Core C# project, not by CMake directly.
# This target copies the pre-built output from the Scripts/O3DE.Core/bin directory.

get_property(gem_root_for_o3de_core GLOBAL PROPERTY "@GEMROOT:${gem_name}@")
set(O3DE_CORE_SCRIPTS_DIR "${gem_root_for_o3de_core}/Assets/Scripts/O3DE.Core")
set(O3DE_CORE_STAGING_DIR "${gem_root_for_o3de_core}/bin/O3DE.Core")

# Determine O3DE.Core build output path (Release build preferred, fallback to Debug)
# The C# project builds to bin/<Configuration>/net8.0/
set(O3DE_CORE_BUILD_OUTPUT_RELEASE "${O3DE_CORE_SCRIPTS_DIR}/bin/Release/net8.0")
set(O3DE_CORE_BUILD_OUTPUT_DEBUG "${O3DE_CORE_SCRIPTS_DIR}/bin/Debug/net8.0")

# Use generator expression to pick the right config, with Release as fallback
# For C# projects, we typically want Release for deployment
set(O3DE_CORE_BUILD_OUTPUT "${O3DE_CORE_BUILD_OUTPUT_RELEASE}")

if(EXISTS "${O3DE_CORE_BUILD_OUTPUT}/O3DE.Core.dll")
    # Create custom target to stage O3DE.Core files
    add_custom_target(${gem_name}.StageO3DECore ALL
        COMMENT "O3DESharp: Staging O3DE.Core files for deployment"
        COMMAND ${CMAKE_COMMAND} -E make_directory "${O3DE_CORE_STAGING_DIR}"
        COMMAND ${CMAKE_COMMAND} -E copy_if_different 
            "${O3DE_CORE_BUILD_OUTPUT}/O3DE.Core.dll" 
            "${O3DE_CORE_STAGING_DIR}/O3DE.Core.dll"
        COMMAND ${CMAKE_COMMAND} -E copy_if_different 
            "${O3DE_CORE_BUILD_OUTPUT}/O3DE.Core.deps.json" 
            "${O3DE_CORE_STAGING_DIR}/O3DE.Core.deps.json"
    )
    
    # Add dependency on O3DE.Core C# project if it exists as a target
    if(TARGET O3DE.Core)
        add_dependencies(${gem_name}.StageO3DECore O3DE.Core)
    endif()
    
    # Also ensure Coral.Managed is staged first since O3DE.Core depends on it
    if(TARGET ${gem_name}.StageCoral)
        add_dependencies(${gem_name}.StageO3DECore ${gem_name}.StageCoral)
    endif()
    
    set_property(TARGET ${gem_name}.StageO3DECore PROPERTY FOLDER "${relative_o3desharp_gem_root}/Deploy")
    
    message(STATUS "O3DESharp: O3DE.Core will be staged to ${O3DE_CORE_STAGING_DIR}")
else()
    message(STATUS "O3DESharp: O3DE.Core.dll not found at ${O3DE_CORE_BUILD_OUTPUT}/O3DE.Core.dll")
    message(STATUS "O3DESharp: Build O3DE.Core C# project first (dotnet build -c Release)")
endif()

# CREDIT Olex A. (Amazon, ReactNavagation)
# # Place external Coral libraries under Sharp gem.
get_property(o3desharp_gem_root GLOBAL PROPERTY "@GEMROOT:${gem_name}@")
ly_get_engine_relative_source_dir(${o3desharp_gem_root} relative_o3desharp_gem_root)
set(coralLibraries Coral.Native)
foreach(coralLibrary ${coralLibraries})
    if(TARGET ${coralLibrary})
        set_property(TARGET ${coralLibrary} PROPERTY FOLDER "${relative_o3desharp_gem_root}/External")
    else()
        message(WARNING "O3DESharp: Coral target '${coralLibrary}' not found. Check if the Coral repository structure has changed.")
    endif()
endforeach()

# Export external Coral targets for installers.
# Note that the following property in the square brackets is the only part of this entire file
# that gets included in installers.  This is why we need to duplicate the github repo from above
# and cannot reuse the variables above.  The rest of this file won't be there to refer to.
set_property(DIRECTORY APPEND PROPERTY O3DE_SUBDIRECTORY_INSTALL_CODE [[
    set(CORAL_GIT_REPO "https://github.com/StudioCherno/Coral.git")
    set(CORAL_GIT_TAG "25437c915b452a08949d636d154a766d6c65601a")
    message(STATUS "O3DESharp Gem uses ${CORAL_GIT_REPO} (License: MIT)")

    set(coralLibraries Coral.Native)
    foreach(coralLibrary ${coralLibraries})
        add_library(o3desharp::${coralLibrary} STATIC IMPORTED GLOBAL)

        set_target_properties(o3desharp::${coralLibrary}
            PROPERTIES
                IMPORTED_LOCATION
                    "${LY_ROOT_FOLDER}/lib/${CMAKE_STATIC_LIBRARY_PREFIX}${coralLibrary}${CMAKE_STATIC_LIBRARY_SUFFIX}")


        ly_target_include_system_directories(TARGET o3desharp::${coralLibrary}
            INTERFACE
                "${LY_ROOT_FOLDER}/include/Coral")
    endforeach()
]]
)

# Place coral under o3dereflect.
get_property(o3dereflect_gem_root GLOBAL PROPERTY "@GEMROOT:${gem_name}@")
ly_get_engine_relative_source_dir(${o3dereflect_gem_root} relative_o3dereflect_gem_root)
set(o3dereflectLibraries Coral.Native Coral.Managed)
foreach(o3dereflectLibrary ${o3dereflectLibraries})
    set_property(TARGET ${o3dereflectLibrary} PROPERTY FOLDER "${relative_o3dereflect_gem_root}/External")
endforeach()

# Coral has a different warning level requirements than O3DE, so we need to adjust the compile options
# to disable warnings-as-errors for Coral targets.
set(coralLibraries Coral.Native)
foreach(coralLibrary ${coralLibraries})
    if(TARGET ${coralLibrary})
        target_compile_options(${coralLibrary}
            PRIVATE
                ${O3DE_COMPILE_OPTION_DISABLE_WARNINGS}   # Coral does not pass Warning Level 4
                ${O3DE_COMPILE_OPTION_ENABLE_EXCEPTIONS}   # Coral requires Exceptions enabled in it, and things that use it
        )
        # Also add INTERFACE options so code including Coral headers suppresses specific warnings
        # C4146: unary minus operator applied to unsigned type (used in HostInstance.hpp)
        # C4459: declaration of 'X' hides global declaration (Coral defines global CharType/StringView
        #        that conflict with local template parameters in AzCore)
        if(MSVC)
            target_compile_options(${coralLibrary}
                INTERFACE
                    /wd4146  # Suppress C4146 in code that includes Coral headers
                    /wd4459  # Suppress C4459 - Coral's global typedefs conflict with AzCore template params
            )
        endif()
    else()
        message(WARNING "O3DESharp: Coral target '${coralLibrary}' not found. Check if the Coral repository structure has changed.")
    endif()
endforeach()
# The ${gem_name}.API target declares the common interface that users of this gem should depend on in their targets
ly_add_target(
    NAME ${gem_name}.API INTERFACE
    NAMESPACE Gem
    FILES_CMAKE
        o3desharp_api_files.cmake
        ${pal_dir}/o3desharp_api_files.cmake
    INCLUDE_DIRECTORIES
        INTERFACE
            Include
    BUILD_DEPENDENCIES
        INTERFACE
           AZ::AzCore
           Gem::Atom_RPI.Public
           Gem::Atom_Feature_Common
           Gem::Atom_Feature_Common.Public
)

# The ${gem_name}.Private.Object target is an internal target
# It should not be used outside of this Gems CMakeLists.txt
ly_add_target(
    NAME ${gem_name}.Private.Object STATIC
    NAMESPACE Gem
    FILES_CMAKE
        o3desharp_private_files.cmake
        ${pal_dir}/o3desharp_private_files.cmake
    TARGET_PROPERTIES
        O3DE_PRIVATE_TARGET TRUE
    INCLUDE_DIRECTORIES
        PRIVATE
            Include
            Source
    BUILD_DEPENDENCIES
        PUBLIC
            AZ::AzCore
            AZ::AzFramework
            Gem::Atom_RPI.Public
            Gem::Atom_Utils.Static
            Gem::Atom_Feature_Common
            Gem::CommonFeaturesAtom.Static
            Coral.Native
)

# Here add ${gem_name} target, it depends on the Private Object library and Public API interface
ly_add_target(
    NAME ${gem_name} ${PAL_TRAIT_MONOLITHIC_DRIVEN_MODULE_TYPE}
    NAMESPACE Gem
    FILES_CMAKE
        o3desharp_shared_files.cmake
        ${pal_dir}/o3desharp_shared_files.cmake
    INCLUDE_DIRECTORIES
        PUBLIC
            Include
        PRIVATE
            Source
    BUILD_DEPENDENCIES
        PUBLIC
            Gem::${gem_name}.API
        PRIVATE
            Gem::${gem_name}.Private.Object
)

# Include the gem name into the Client Module source file
# for use with the AZ_DECLARE_MODULE_CLASS macro
# This is to allow renaming of the gem to also cause
# the CreateModuleClass_Gem_<gem-name> function which
# is used to bootstrap the gem in monolithic builds to link to the new gem name
ly_add_source_properties(
SOURCES
    Source/Clients/O3DESharpModule.cpp
PROPERTY COMPILE_DEFINITIONS
    VALUES
        O3DE_GEM_NAME=${gem_name}
        O3DE_GEM_VERSION=${gem_version})

# By default, we will specify that the above target ${gem_name} would be used by
# Client and Server type targets when this gem is enabled.  If you don't want it
# active in Clients or Servers by default, delete one of both of the following lines:
ly_create_alias(NAME ${gem_name}.Clients NAMESPACE Gem TARGETS Gem::${gem_name})
ly_create_alias(NAME ${gem_name}.Servers NAMESPACE Gem TARGETS Gem::${gem_name})
ly_create_alias(NAME ${gem_name}.Unified NAMESPACE Gem TARGETS Gem::${gem_name})

# For the Client and Server variants of ${gem_name} Gem, an alias to the ${gem_name}.API target will be made
ly_create_alias(NAME ${gem_name}.Clients.API NAMESPACE Gem TARGETS Gem::${gem_name}.API)
ly_create_alias(NAME ${gem_name}.Servers.API NAMESPACE Gem TARGETS Gem::${gem_name}.API)
ly_create_alias(NAME ${gem_name}.Unified.API NAMESPACE Gem TARGETS Gem::${gem_name}.API)

# Add in CMake dependencies for each gem dependency listed in this gem's gem.json file
# for the Clients, Servers and Unified gem variants
o3de_add_variant_dependencies_for_gem_dependencies(GEM_NAME ${gem_name} VARIANTS Clients Servers Unified)

# If we are on a host platform, we want to add the host tools targets like the ${gem_name}.Editor MODULE target
if(PAL_TRAIT_BUILD_HOST_TOOLS)
    # The ${gem_name}.Editor.API target can be used by other gems that want to interact with the ${gem_name}.Editor module
    ly_add_target(
        NAME ${gem_name}.Editor.API INTERFACE
        NAMESPACE Gem
        FILES_CMAKE
            o3desharp_editor_api_files.cmake
            ${pal_dir}/o3desharp_editor_api_files.cmake
        INCLUDE_DIRECTORIES
            INTERFACE
                Include
        BUILD_DEPENDENCIES
            INTERFACE
                AZ::AzToolsFramework
    )

    # The ${gem_name}.Editor.Private.Object target is an internal target
    # which is only to be used by this gems CMakeLists.txt and any subdirectories
    # Other gems should not use this target
    ly_add_target(
        NAME ${gem_name}.Editor.Private.Object STATIC
        NAMESPACE Gem
        FILES_CMAKE
            o3desharp_editor_private_files.cmake
        TARGET_PROPERTIES
            O3DE_PRIVATE_TARGET TRUE
        INCLUDE_DIRECTORIES
            PRIVATE
                Include
                Source
        BUILD_DEPENDENCIES
            PUBLIC
                AZ::AzToolsFramework
                ${gem_name}.Private.Object
                Gem::Atom_Utils.Static
                Gem::Atom_Feature_Common.Public
                Gem::CommonFeaturesAtom.Static
    )

    ly_add_target(
        NAME ${gem_name}.Editor GEM_MODULE
        NAMESPACE Gem
        AUTOMOC
        FILES_CMAKE
            o3desharp_editor_shared_files.cmake
        INCLUDE_DIRECTORIES
            PRIVATE
                Source
            PUBLIC
                Include
        BUILD_DEPENDENCIES
            PUBLIC
                Gem::${gem_name}.Editor.API
            PRIVATE
                Gem::${gem_name}.Editor.Private.Object
    )

    # Include the gem name into the Editor Module source file
    # for use with the AZ_DECLARE_MODULE_CLASS macro
    # This is to allow renaming of the gem to also cause
    # the CreateModuleClass_Gem_<gem-name> function which
    # is used to bootstrap the gem in monolithic builds to link to the new gem name
    ly_add_source_properties(
    SOURCES
        Source/Tools/O3DESharpEditorModule.cpp
    PROPERTY COMPILE_DEFINITIONS
        VALUES
            O3DE_GEM_NAME=${gem_name}
            O3DE_GEM_VERSION=${gem_version})

    # By default, we will specify that the above target ${gem_name} would be used by
    # Tool and Builder type targets when this gem is enabled.  If you don't want it
    # active in Tools or Builders by default, delete one of both of the following lines:
    ly_create_alias(NAME ${gem_name}.Tools    NAMESPACE Gem TARGETS Gem::${gem_name}.Editor)
    ly_create_alias(NAME ${gem_name}.Builders NAMESPACE Gem TARGETS Gem::${gem_name}.Editor)

    # For the Tools and Builders variants of ${gem_name} Gem, an alias to the ${gem_name}.Editor API target will be made
    ly_create_alias(NAME ${gem_name}.Tools.API NAMESPACE Gem TARGETS Gem::${gem_name}.Editor.API)
    ly_create_alias(NAME ${gem_name}.Builders.API NAMESPACE Gem TARGETS Gem::${gem_name}.Editor.API)

    # Add in CMake dependencies for each gem dependency listed in this gem's gem.json file
    # for the Tools and Builders gem variants
    o3de_add_variant_dependencies_for_gem_dependencies(GEM_NAME ${gem_name} VARIANTS Tools Builders)
endif()

################################################################################
# Tests
################################################################################
# See if globally, tests are supported
if(PAL_TRAIT_BUILD_TESTS_SUPPORTED)
    # We globally support tests, see if we support tests on this platform for ${gem_name}.Tests
    if(PAL_TRAIT_O3DESHARP_TEST_SUPPORTED)
        # We support ${gem_name}.Tests on this platform, add dependency on the Private Object target
        ly_add_target(
            NAME ${gem_name}.Tests ${PAL_TRAIT_TEST_TARGET_TYPE}
            NAMESPACE Gem
            FILES_CMAKE
                o3desharp_tests_files.cmake
            INCLUDE_DIRECTORIES
                PRIVATE
                    Tests
                    Source
                    Include
            BUILD_DEPENDENCIES
                PRIVATE
                    AZ::AzTest
                    AZ::AzFramework
                    Gem::${gem_name}.Private.Object
        )

        # Add ${gem_name}.Tests to googletest
        ly_add_googletest(
            NAME Gem::${gem_name}.Tests
        )
    endif()

    # If we are a host platform we want to add tools test like editor tests here
    if(PAL_TRAIT_BUILD_HOST_TOOLS)
        # We are a host platform, see if Editor tests are supported on this platform
        if(PAL_TRAIT_O3DESHARP_EDITOR_TEST_SUPPORTED)
            # We support ${gem_name}.Editor.Tests on this platform, add ${gem_name}.Editor.Tests target which depends on
            # private ${gem_name}.Editor.Private.Object target
            ly_add_target(
                NAME ${gem_name}.Editor.Tests ${PAL_TRAIT_TEST_TARGET_TYPE}
                NAMESPACE Gem
                FILES_CMAKE
                    o3desharp_editor_tests_files.cmake
                INCLUDE_DIRECTORIES
                    PRIVATE
                        Tests
                        Source
                        Include
                BUILD_DEPENDENCIES
                    PRIVATE
                        AZ::AzTest
                        Gem::${gem_name}.Editor.Private.Object
            )

            # Add ${gem_name}.Editor.Tests to googletest
            ly_add_googletest(
                NAME Gem::${gem_name}.Editor.Tests
            )
        endif()
    endif()
endif()

# ============================================================
# C# Projects Integration
# ============================================================
# Include C# projects in the Visual Studio solution so they appear alongside C++ projects.
# This allows developers to work with all O3DESharp code (C++ and C#) in a single solution.
#
# NOTE: Coral.Managed is already added by FetchContent_MakeAvailable(Coral), so we only
# need to add O3DE.Core here. The Coral projects are organized in the folder structure
# by the earlier set_property(TARGET ... PROPERTY FOLDER ...) calls.

if(MSVC)
    # Get the gem root for proper folder organization
    get_property(gem_root GLOBAL PROPERTY "@GEMROOT:${gem_name}@")
    
    # Define paths to C# projects
    set(O3DESHARP_CSHARP_SCRIPTS_DIR "${gem_root}/Assets/Scripts")
    
    # Include O3DE.Core C# project (the main scripting API)
    # This is NOT part of Coral, it's our own scripting API built on top of Coral
    if(NOT TARGET O3DE.Core)
        if(EXISTS "${O3DESHARP_CSHARP_SCRIPTS_DIR}/O3DE.Core/O3DE.Core.csproj")
            include_external_msproject(
                O3DE.Core
                "${O3DESHARP_CSHARP_SCRIPTS_DIR}/O3DE.Core/O3DE.Core.csproj"
                GUID "5C583E17-1553-1928-6D67-CFF052F07C0E"
                PLATFORM "Any CPU"
            )
            set_target_properties(O3DE.Core PROPERTIES 
                FOLDER "Gems/${gem_name}/Scripts"
            )
            
            # Add dependency so C# builds after Coral.Managed
            if(TARGET Coral.Managed)
                add_dependencies(O3DE.Core Coral.Managed)
            endif()
            
            message(STATUS "O3DESharp: Added O3DE.Core C# project to solution")
        else()
            message(STATUS "O3DESharp: O3DE.Core.csproj not found at ${O3DESHARP_CSHARP_SCRIPTS_DIR}/O3DE.Core/")
        endif()
    endif()
    
    message(STATUS "O3DESharp: C# projects integrated into Visual Studio solution")
endif()
