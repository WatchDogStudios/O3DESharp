#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
#
# TLDR (Mikael A.): Functionality to verify that a suitable .NET SDK and runtime are installed on the system for Coral.

function (o3de_sharp_netverify)
    if(DOTNET_DIR AND EXISTS "${DOTNET_DIR}/dotnet")
        set(DOTNET_EXECUTABLE "${DOTNET_DIR}/dotnet")
    else()
        find_program(DOTNET_EXECUTABLE dotnet)
    endif()

    if(NOT DOTNET_EXECUTABLE)
        message(STATUS "dotnet executable not found. Attempting to download and install...")
        
        set(DOTNET_INSTALL_DIR "${CMAKE_BINARY_DIR}/.dotnet")
        file(MAKE_DIRECTORY "${DOTNET_INSTALL_DIR}")

        if(WIN32)
            set(INSTALL_SCRIPT_URL "https://dot.net/v1/dotnet-install.ps1")
            set(INSTALL_SCRIPT "${DOTNET_INSTALL_DIR}/dotnet-install.ps1")
            set(DOTNET_EXECUTABLE "${DOTNET_INSTALL_DIR}/dotnet.exe")
        elseif(UNIX)
            set(INSTALL_SCRIPT_URL "https://dot.net/v1/dotnet-install.sh")
            set(INSTALL_SCRIPT "${DOTNET_INSTALL_DIR}/dotnet-install.sh")
            set(DOTNET_EXECUTABLE "${DOTNET_INSTALL_DIR}/dotnet")
        endif()

        if(INSTALL_SCRIPT_URL)
            file(DOWNLOAD "${INSTALL_SCRIPT_URL}" "${INSTALL_SCRIPT}" STATUS DOWNLOAD_STATUS)
            list(GET DOWNLOAD_STATUS 0 STATUS_CODE)
            if(NOT STATUS_CODE EQUAL 0)
                message(FATAL_ERROR "Failed to download dotnet install script: ${DOWNLOAD_STATUS}")
            endif()

            if(WIN32)
                execute_process(
                    COMMAND powershell -ExecutionPolicy Bypass -File "${INSTALL_SCRIPT}" -InstallDir "${DOTNET_INSTALL_DIR}" -Channel LTS
                    RESULT_VARIABLE INSTALL_RESULT
                )
            else()
                execute_process(COMMAND chmod +x "${INSTALL_SCRIPT}")
                execute_process(
                    COMMAND "${INSTALL_SCRIPT}" --install-dir "${DOTNET_INSTALL_DIR}" --channel LTS
                    RESULT_VARIABLE INSTALL_RESULT
                )
            endif()

            if(NOT INSTALL_RESULT EQUAL 0)
                message(FATAL_ERROR "Failed to install .NET")
            endif()
        else()
             message(FATAL_ERROR "Unsupported platform for automatic .NET installation.")
        endif()
    endif()

    if(NOT EXISTS "${DOTNET_EXECUTABLE}")
         message(FATAL_ERROR "Could not find dotnet executable at ${DOTNET_EXECUTABLE} after attempted installation.")
    endif()

    execute_process(
        COMMAND ${DOTNET_EXECUTABLE} --version
        OUTPUT_VARIABLE DOTNET_VERSION
        OUTPUT_STRIP_TRAILING_WHITESPACE
    )

    message(STATUS "Found .NET executable: ${DOTNET_EXECUTABLE} (Version: ${DOTNET_VERSION})")
    set(DOTNET_EXECUTABLE ${DOTNET_EXECUTABLE} PARENT_SCOPE)
endfunction()