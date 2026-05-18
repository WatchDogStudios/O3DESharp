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

# Locate (or install) a .NET SDK before we try to build C# projects.
# Sets DOTNET_EXECUTABLE in this scope, used by binding generator and C# test custom targets below.
o3de_sharp_netverify()

# CORAL CLONE INFO
# ===========================
# NOTE(Mikael A.): Stay on main, do not change to internal coral branch!
# Pin to a specific commit so dev and install paths are reproducible.
# Bump this SHA in lockstep with the value embedded in the install code
# block below (search for "O3DE_SUBDIRECTORY_INSTALL_CODE").
set(CORAL_GIT_REPO "https://github.com/WatchDogStudios/Coral.git")
set(CORAL_GIT_TAG "0d3f30e0449956049fa330a3a44a47b7860a2409")

# Local-checkout override: if O3DESHARP_CORAL_SOURCE_DIR is set (cache
# variable or env var) and points at a Coral working tree, FetchContent
# uses that path instead of the GIT_REPOSITORY clone. Lets developers
# iterate on Coral changes (the debugger-attach support added for the
# managed-debug initiative, for instance) without pushing to the fork +
# bumping the pinned SHA on every rebuild.
#
# Two ways to set it:
#   1. -DO3DESHARP_CORAL_SOURCE_DIR=<path>  on the cmake command line,
#      or set as a CMake cache variable in your IDE's CMake settings.
#   2. set the O3DESHARP_CORAL_SOURCE_DIR environment variable before
#      configuring (handy when invoking CMake from a per-developer
#      build script).
#
# Convention-based auto-detect (no machine-specific absolute paths -
# anything machine-specific belongs in the env var above): if a Coral
# fork checkout sits at ../Coral-O3DESharp next to this gem
# (CMAKE_CURRENT_LIST_DIR/../.. since this file is Code/CMakeLists.txt),
# use it. Easy to opt in by placing the fork there; easy to opt out by
# not placing it there. No-op on machines without that layout.
if(NOT DEFINED CACHE{O3DESHARP_CORAL_SOURCE_DIR})
    if(DEFINED ENV{O3DESHARP_CORAL_SOURCE_DIR})
        set(_o3desharp_coral_src "$ENV{O3DESHARP_CORAL_SOURCE_DIR}")
    else()
        set(_o3desharp_coral_candidate "${CMAKE_CURRENT_LIST_DIR}/../../Coral-O3DESharp")
        if(IS_DIRECTORY "${_o3desharp_coral_candidate}"
                AND EXISTS "${_o3desharp_coral_candidate}/Coral.Native/Source/Coral/HostInstance.cpp")
            set(_o3desharp_coral_src "${_o3desharp_coral_candidate}")
        endif()
        unset(_o3desharp_coral_candidate)
    endif()
    set(O3DESHARP_CORAL_SOURCE_DIR "${_o3desharp_coral_src}" CACHE PATH
        "Optional local Coral fork checkout. When set, used instead of the GIT_REPOSITORY clone.")
    unset(_o3desharp_coral_src)
endif()

# Disable Coral examples and tests before FetchContent to speed up configuration
set(CORAL_EXAMPLE OFF CACHE BOOL "" FORCE)
set(CORAL_TESTING OFF CACHE BOOL "" FORCE)

include(FetchContent)

# Use GIT_SHALLOW to only fetch the latest commit (much faster than full clone)
# FIND_PACKAGE_ARGS allows using a pre-installed Coral if available
if(O3DESHARP_CORAL_SOURCE_DIR AND IS_DIRECTORY "${O3DESHARP_CORAL_SOURCE_DIR}")
    message(STATUS "O3DESharp: Using local Coral checkout at ${O3DESHARP_CORAL_SOURCE_DIR} (override GIT_REPOSITORY)")
    FetchContent_Declare(
        Coral
        SOURCE_DIR "${O3DESHARP_CORAL_SOURCE_DIR}"
        SOURCE_SUBDIR cmake
    )
else()
    FetchContent_Declare(
        Coral
        GIT_REPOSITORY ${CORAL_GIT_REPO}
        GIT_TAG ${CORAL_GIT_TAG}
        GIT_SHALLOW TRUE
        GIT_PROGRESS TRUE
        SOURCE_SUBDIR cmake
    )
endif()

# please always be really clear about what third parties your gem uses.
message(STATUS "O3DESharp Gem uses ${CORAL_GIT_REPO} (License: MIT)")

# Use FetchContent_MakeAvailable instead of the older
# FetchContent_GetProperties + FetchContent_Populate + add_subdirectory
# dance. CMake 4.0 set policy CMP0169 to NEW by default, which
# deprecates calling FetchContent_Populate with a content name and
# made it a hard error under some configurations - "add_subdirectory
# given source ... which is not an existing directory" comes from
# the populate step being skipped under the new policy.
#
# FetchContent_MakeAvailable handles both the GIT_REPOSITORY clone case
# and the SOURCE_DIR override case uniformly: it populates the content
# (cloning if needed, or just using the SOURCE_DIR as-is) and runs
# add_subdirectory on the SOURCE_SUBDIR (the `cmake` subfolder of the
# Coral tree) - which is exactly what the old explicit dance was doing.
FetchContent_MakeAvailable(Coral)

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
# The C# project builds to bin/<Configuration>/net9.0/
set(O3DE_CORE_BUILD_OUTPUT_RELEASE "${O3DE_CORE_SCRIPTS_DIR}/bin/Release/net9.0")
set(O3DE_CORE_BUILD_OUTPUT_DEBUG "${O3DE_CORE_SCRIPTS_DIR}/bin/Debug/net9.0")

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

# ============================================================
# ClangSharp Binding Generator Tool
# ============================================================
# Build the ClangSharp-based C# binding generator tool.
# This tool performs static analysis of C++ headers to generate C# bindings.

# Option to control automatic binding generation
option(O3DESHARP_AUTO_GENERATE_BINDINGS "Automatically generate C# bindings during build" ON)

get_property(gem_root_for_bindings GLOBAL PROPERTY "@GEMROOT:${gem_name}@")
set(BINDING_GENERATOR_SOURCE_DIR "${gem_root_for_bindings}/Code/Tools/BindingGenerator")
set(BINDING_GENERATOR_BINARY_DIR "${CMAKE_BINARY_DIR}/Gems/O3DESharp/BindingGenerator")
set(BINDING_GENERATOR_OUTPUT_DIR "${CMAKE_BINARY_DIR}/bin/BindingGenerator")

# Check if the binding generator source exists
if(EXISTS "${BINDING_GENERATOR_SOURCE_DIR}/O3DESharp.BindingGenerator/O3DESharp.BindingGenerator.csproj")
    # Use ExternalProject to build the ClangSharp binding generator
    include(ExternalProject)
    
    # Determine build configuration
    set(BINDING_GENERATOR_CONFIG "Release")
    if(CMAKE_BUILD_TYPE MATCHES "Debug")
        set(BINDING_GENERATOR_CONFIG "Debug")
    endif()
    
    ExternalProject_Add(
        O3DESharp.BindingGenerator
        SOURCE_DIR "${BINDING_GENERATOR_SOURCE_DIR}"
        BINARY_DIR "${BINDING_GENERATOR_BINARY_DIR}"
        CONFIGURE_COMMAND ""
        BUILD_COMMAND ${DOTNET_EXECUTABLE} build "${BINDING_GENERATOR_SOURCE_DIR}/O3DESharp.BindingGenerator/O3DESharp.BindingGenerator.csproj"
            -c ${BINDING_GENERATOR_CONFIG}
            -o "${BINDING_GENERATOR_OUTPUT_DIR}"
        INSTALL_COMMAND ""
        BUILD_ALWAYS OFF
        BUILD_BYPRODUCTS "${BINDING_GENERATOR_OUTPUT_DIR}/O3DESharp.BindingGenerator.dll"
    )
    
    # Export the path to the binding generator for Python scripts
    set(O3DESHARP_BINDING_GENERATOR_PATH "${BINDING_GENERATOR_OUTPUT_DIR}" CACHE PATH 
        "Path to O3DESharp binding generator tool" FORCE)
    
    message(STATUS "O3DESharp: Binding generator will be built to ${BINDING_GENERATOR_OUTPUT_DIR}")
    
    # ============================================================
    # Automatic Binding Generation Target
    # ============================================================
    # Generate C# bindings automatically during build if enabled.
    # The ClangSharp tool has built-in incremental build support via file hash caching.
    
    if(O3DESHARP_AUTO_GENERATE_BINDINGS)
        set(BINDINGS_OUTPUT_DIR "${CMAKE_BINARY_DIR}/Gems/O3DESharp/Generated/Bindings")
        set(BINDINGS_MARKER_FILE "${CMAKE_BINARY_DIR}/.o3desharp_bindings_generated")
        set(BINDING_CONFIG_FILE "${gem_root_for_bindings}/binding_config.json")
        
        # Create binding generation target
        add_custom_target(${gem_name}.GenerateBindings
            COMMENT "O3DESharp: Generating C# bindings with ClangSharp tool"
            COMMAND ${CMAKE_COMMAND} -E echo "Checking if C# bindings need regeneration..."
            COMMAND ${CMAKE_COMMAND} -E make_directory "${BINDINGS_OUTPUT_DIR}"
            COMMAND ${DOTNET_EXECUTABLE} run
                --project "${BINDING_GENERATOR_SOURCE_DIR}/O3DESharp.BindingGenerator/O3DESharp.BindingGenerator.csproj"
                -- generate
                --project "${CMAKE_SOURCE_DIR}"
                --output "${BINDINGS_OUTPUT_DIR}"
                --incremental
            COMMAND ${CMAKE_COMMAND} -E touch "${BINDINGS_MARKER_FILE}"
            DEPENDS O3DESharp.BindingGenerator
            WORKING_DIRECTORY "${gem_root_for_bindings}"
            VERBATIM
        )
        
        # Make the binding generation target part of the default build
        # but only if the config file exists (prevents errors in fresh checkouts)
        if(EXISTS "${BINDING_CONFIG_FILE}")
            set_property(TARGET ${gem_name}.BindingGenerator PROPERTY FOLDER "${relative_o3desharp_gem_root}/Bindings")
            set_property(TARGET ${gem_name}.GenerateBindings PROPERTY FOLDER "${relative_o3desharp_gem_root}/Bindings")
            message(STATUS "O3DESharp: Automatic binding generation ENABLED (output: ${BINDINGS_OUTPUT_DIR})")
            message(STATUS "O3DESharp:   Set O3DESHARP_AUTO_GENERATE_BINDINGS=OFF to disable")
        else()
            message(STATUS "O3DESharp: Automatic binding generation DISABLED (binding_config.json not found)")
            message(STATUS "O3DESharp:   Create ${BINDING_CONFIG_FILE} to enable automatic generation")
        endif()
    else()
        message(STATUS "O3DESharp: Automatic binding generation DISABLED by user")
        message(STATUS "O3DESharp:   Set O3DESHARP_AUTO_GENERATE_BINDINGS=ON to enable")
    endif()
else()
    message(WARNING "O3DESharp: Binding generator source not found at ${BINDING_GENERATOR_SOURCE_DIR}")
    message(WARNING "O3DESharp: C# binding generation will not be available")
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
    # NOTE: Keep this repo + SHA in sync with the dev-side CORAL_GIT_REPO /
    # CORAL_GIT_TAG above. Different values here mean dev builds and exported
    # installer builds will fetch different Coral sources and cannot produce
    # bit-identical binaries.
    set(CORAL_GIT_REPO "https://github.com/WatchDogStudios/Coral.git")
    set(CORAL_GIT_TAG "0d3f30e0449956049fa330a3a44a47b7860a2409")
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

# ============================================================
# Deploy Coral and O3DE.Core files to launcher targets
# ============================================================
# Now that the launcher target aliases exist, we can deploy runtime files to them

# Deploy Coral.Managed files if the staging target exists
if(TARGET ${gem_name}.StageCoral)
    ly_add_target_files(
        TARGETS ${gem_name}.Clients ${gem_name}.Servers ${gem_name}.Unified
        FILES
            "${CORAL_STAGING_DIR}/Coral.Managed.dll"
            "${CORAL_STAGING_DIR}/Coral.Managed.runtimeconfig.json"
            "${CORAL_STAGING_DIR}/Coral.Managed.deps.json"
        OUTPUT_SUBDIRECTORY "Bin/Scripts/Coral"
    )
    message(STATUS "O3DESharp: Coral will be deployed to Bin/Scripts/Coral/ for runtime and export")
endif()

# Deploy O3DE.Core files if the staging target exists
if(TARGET ${gem_name}.StageO3DECore)
    ly_add_target_files(
        TARGETS ${gem_name}.Clients ${gem_name}.Servers ${gem_name}.Unified
        FILES
            "${O3DE_CORE_STAGING_DIR}/O3DE.Core.dll"
            "${O3DE_CORE_STAGING_DIR}/O3DE.Core.deps.json"
        OUTPUT_SUBDIRECTORY "Bin/Scripts"
    )
    message(STATUS "O3DESharp: O3DE.Core will be deployed to Bin/Scripts/ for runtime and export")
endif()

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
    #
    # AUTOMOC is enabled here because Phase 16's CSharpAssemblyWatcher uses
    # Q_OBJECT (it owns a QFileSystemWatcher and connects to its signals via
    # QObject::connect). The existing CSharpExposedPropertiesHandler only uses
    # QWidget without Q_OBJECT, so AUTOMOC wasn't needed previously - but the
    # new watcher trips MOC's "Q_OBJECT without moc'd output" error without it.
    ly_add_target(
        NAME ${gem_name}.Editor.Private.Object STATIC
        NAMESPACE Gem
        AUTOMOC
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
                AZ::AzQtComponents
                ${gem_name}.Private.Object
                Gem::Atom_Utils.Static
                Gem::Atom_Feature_Common.Public
                Gem::CommonFeaturesAtom.Static
                # Needed for EditorPythonBindings::EditorPythonBindingsNotificationBus,
                # which O3DESharpEditorSystemComponent connects to so its
                # OnPostInitialize() hook can import csharp_editor_bootstrap as soon
                # as the Python VM is ready (instead of waiting for the first menu
                # click).
                Gem::EditorPythonBindings.Static
                # Phase 16: CSharpAssemblyWatcher uses QFileSystemWatcher / QTimer
                # to debounce and react to Bin/Scripts/ changes. AzQtComponents
                # already brings Qt6Widgets transitively, but listing Qt6::Core
                # explicitly makes the dependency intent obvious.
                3rdParty::Qt::Core
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

    # Deploy the C# Script component icons next to the Editor exe so the
    # EditContext "Icon" / "ViewportIcon" attributes resolve. The editor
    # warns "Unable to find icon path 'Icons/Components/csharp.svg' for
    # component type: C# Script" otherwise, because SandboxIntegration
    # searches paths relative to bin/<config>/ and the asset processor
    # doesn't deploy these editor-time icons on its own.
    get_property(gem_root_for_icons GLOBAL PROPERTY "@GEMROOT:${gem_name}@")
    ly_add_target_files(
        TARGETS ${gem_name}.Editor
        FILES
            "${gem_root_for_icons}/Assets/Editor/Icons/Components/csharp.svg"
        OUTPUT_SUBDIRECTORY "Icons/Components"
    )
    ly_add_target_files(
        TARGETS ${gem_name}.Editor
        FILES
            "${gem_root_for_icons}/Assets/Editor/Icons/Components/Viewport/csharp.svg"
        OUTPUT_SUBDIRECTORY "Icons/Components/Viewport"
    )
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

    # ============================================================
    # Python Binding Generator Tests
    # ============================================================
    # Add pytest tests for Python binding generator orchestration scripts
    if(PAL_TRAIT_BUILD_HOST_TOOLS)
        get_property(gem_root_for_python_tests GLOBAL PROPERTY "@GEMROOT:${gem_name}@")
        set(PYTHON_TESTS_DIR "${gem_root_for_python_tests}/Editor/Tests")

        if(EXISTS "${PYTHON_TESTS_DIR}/conftest.py")
            # Create custom target to run Python tests with pytest
            add_custom_target(${gem_name}.Tests.Python
                COMMAND ${CMAKE_COMMAND} -E echo "Running O3DESharp Python tests..."
                COMMAND pytest "${PYTHON_TESTS_DIR}" -v --tb=short
                WORKING_DIRECTORY "${gem_root_for_python_tests}"
                COMMENT "Running O3DESharp Python binding generator tests with pytest"
                VERBATIM
            )

            # Add to test folder
            set_property(TARGET ${gem_name}.Tests.Python PROPERTY FOLDER "${relative_o3desharp_gem_root}/Tests")

            # Register with CTest if available
            if(BUILD_TESTING)
                add_test(
                    NAME O3DESharp.Tests.Python
                    COMMAND pytest "${PYTHON_TESTS_DIR}" -v --tb=short
                    WORKING_DIRECTORY "${gem_root_for_python_tests}"
                )
                set_tests_properties(O3DESharp.Tests.Python PROPERTIES
                    LABELS "python;bindings;unit"
                    TIMEOUT 300
                )
            endif()

            message(STATUS "O3DESharp: Python tests target created (run with: cmake --build . --target ${gem_name}.Tests.Python)")
        else()
            message(STATUS "O3DESharp: Python tests not found at ${PYTHON_TESTS_DIR}")
        endif()
    endif()

    # ============================================================
    # C# Binding Generator Tests
    # ============================================================
    # Add xUnit tests for ClangSharp binding generator tool
    if(PAL_TRAIT_BUILD_HOST_TOOLS)
        get_property(gem_root_for_csharp_tests GLOBAL PROPERTY "@GEMROOT:${gem_name}@")
        set(CSHARP_TESTS_DIR "${gem_root_for_csharp_tests}/Code/Tools/BindingGenerator.Tests")

        if(EXISTS "${CSHARP_TESTS_DIR}/BindingGenerator.Tests.csproj")
            # Create custom target to run C# tests with dotnet test
            add_custom_target(${gem_name}.Tests.BindingGenerator
                COMMAND ${CMAKE_COMMAND} -E echo "Running O3DESharp C# Binding Generator tests..."
                COMMAND ${DOTNET_EXECUTABLE} test "${CSHARP_TESTS_DIR}/BindingGenerator.Tests.csproj" --verbosity normal
                WORKING_DIRECTORY "${CSHARP_TESTS_DIR}"
                COMMENT "Running O3DESharp ClangSharp binding generator tests with xUnit"
                DEPENDS O3DESharp.BindingGenerator
                VERBATIM
            )

            # Add to test folder
            set_property(TARGET ${gem_name}.Tests.BindingGenerator PROPERTY FOLDER "${relative_o3desharp_gem_root}/Tests")

            # Register with CTest if available
            if(BUILD_TESTING)
                add_test(
                    NAME O3DESharp.Tests.BindingGenerator
                    COMMAND ${DOTNET_EXECUTABLE} test "${CSHARP_TESTS_DIR}/BindingGenerator.Tests.csproj" --verbosity normal
                    WORKING_DIRECTORY "${CSHARP_TESTS_DIR}"
                )
                set_tests_properties(O3DESharp.Tests.BindingGenerator PROPERTIES
                    LABELS "csharp;bindings;unit"
                    TIMEOUT 600
                )
            endif()

            message(STATUS "O3DESharp: C# Binding Generator tests target created (run with: cmake --build . --target ${gem_name}.Tests.BindingGenerator)")
        else()
            message(STATUS "O3DESharp: C# Binding Generator tests not found at ${CSHARP_TESTS_DIR}")
        endif()
    endif()
endif()

# ============================================================
# C# Projects Integration
# ============================================================
# NOTE(Mikael A.): Yes, i know this is hacky, will add proper support soon.
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
