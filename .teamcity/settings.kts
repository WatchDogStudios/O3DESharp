/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 */

import jetbrains.buildServer.configs.kotlin.*
import jetbrains.buildServer.configs.kotlin.buildFeatures.commitStatusPublisher
import jetbrains.buildServer.configs.kotlin.buildFeatures.perfmon
import jetbrains.buildServer.configs.kotlin.buildSteps.script
import jetbrains.buildServer.configs.kotlin.triggers.vcs
import jetbrains.buildServer.configs.kotlin.vcs.GitVcsRoot

/*
 * TeamCity Cloud configuration for the O3DESharp gem.
 *
 * Mirrors what .github/workflows/ci.yml USED to do (now deleted),
 * with the same matrix shape: C# build + test on Ubuntu + Windows
 * agents, Python tests on Ubuntu.
 *
 * This file is read on every push to a branch wired to the
 * project's Versioned Settings feature. To change the build:
 *   1. Edit this file
 *   2. Commit + push
 *   3. TeamCity recompiles + applies on the next sync (usually
 *      within ~60 seconds; force one via Admin > Versioned
 *      Settings > Load project settings from VCS)
 *
 * Do NOT edit the build configurations in the TeamCity UI - those
 * edits get clobbered on the next VCS sync. The UI is read-only
 * when versioned settings are active.
 *
 * STRUCTURE NOTE: each BuildType inlines its full step list, even
 * though there is overlap between the two C# matrix entries. An
 * earlier draft used top-level extension-function helpers to share
 * the build steps, but TeamCity's Kotlin script compiler rejects
 * that pattern - the `object` declarations capture the kts script
 * class instance through the helpers, which TC's DSL forbids. The
 * inline form below is the only style the TC Cloud compiler
 * accepts cleanly for object-style BuildType declarations.
 *
 * DOTNET RUNNER NOTE: we use plain `script { }` build steps that
 * invoke the explicit dotnet binary path, NOT TC's dotnetBuild /
 * dotnetTest runners. Reason: TC's Linux cloud agent ships with
 * .NET 8.0 preinstalled at /usr/share/dotnet, but we need .NET 9
 * for O3DE.Core. The dotnet* runners locate the dotnet binary
 * eagerly (before the previous step's ##teamcity[setParameter] PATH
 * update takes effect), so they always pick up 8.0 from
 * /usr/share/dotnet and fail with NETSDK1045 "current .NET SDK does
 * not support targeting .NET 9.0". script-runner steps that call
 * $HOME/.dotnet/dotnet directly sidestep the discovery entirely.
 */

version = "2024.03"

project {
    description = "C# scripting layer for Open 3D Engine via the Coral .NET host"

    vcsRoot(O3DESharpVcs)

    buildType(CSharpLinux)
    buildType(CSharpWindows)
    buildType(PythonTests)
    buildType(AllChecks)

    params {
        param("env.DOTNET_NOLOGO", "1")
        param("env.DOTNET_CLI_TELEMETRY_OPTOUT", "1")
        param("env.DOTNET_SKIP_FIRST_TIME_EXPERIENCE", "1")
    }
}

// --------------------------------------------------------------------
// VCS root
// --------------------------------------------------------------------

object O3DESharpVcs : GitVcsRoot({
    name = "O3DESharp (GitHub)"
    url = "https://github.com/WatchDogStudios/O3DESharp.git"
    branch = "refs/heads/main"
    branchSpec = """
        +:refs/heads/*
        +:refs/pull/(*)/head
    """.trimIndent()
})

// --------------------------------------------------------------------
// C# build & test on Linux
// --------------------------------------------------------------------

object CSharpLinux : BuildType({
    id("CSharpLinux")
    name = "C# build & test (Linux)"
    description = "Builds the C# tooling + runs the xUnit suite on an Ubuntu cloud agent"

    vcs {
        root(O3DESharpVcs)
        cleanCheckout = false
    }

    requirements {
        equals("teamcity.agent.os.family", "Linux")
    }

    steps {
        script {
            name = "Install .NET 8.0 + 9.0 SDKs"
            scriptContent = """
                set -e
                # dotnet-install: idempotent installer that drops the
                # SDK into $HOME/.dotnet. We install both 8 (for the
                # BindingGenerator tool) and 9 (for O3DE.Core and the
                # smoke consumer); the system-preinstalled 8 at
                # /usr/share/dotnet does not include 9, so without
                # this step the build fails with NETSDK1045.
                if [ ! -f /tmp/dotnet-install.sh ]; then
                    curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
                    chmod +x /tmp/dotnet-install.sh
                fi
                /tmp/dotnet-install.sh --channel 8.0 --install-dir "${'$'}HOME/.dotnet"
                /tmp/dotnet-install.sh --channel 9.0 --install-dir "${'$'}HOME/.dotnet"
                "${'$'}HOME/.dotnet/dotnet" --list-sdks
            """.trimIndent()
        }
        script {
            name = "Build O3DE.Core (net9)"
            scriptContent = """
                set -e
                "${'$'}HOME/.dotnet/dotnet" build Assets/Scripts/O3DE.Core/O3DE.Core.csproj -c Release --nologo
            """.trimIndent()
        }
        script {
            name = "Build BindingGenerator (net8 tool)"
            scriptContent = """
                set -e
                "${'$'}HOME/.dotnet/dotnet" build Code/Tools/BindingGenerator/O3DESharp.BindingGenerator/O3DESharp.BindingGenerator.csproj -c Release --nologo
            """.trimIndent()
        }
        script {
            name = "Build BindingGenerator.Tasks (netstandard2.0 MSBuild task)"
            scriptContent = """
                set -e
                "${'$'}HOME/.dotnet/dotnet" build Code/Tools/BindingGenerator/O3DESharp.BindingGenerator.Tasks/O3DESharp.BindingGenerator.Tasks.csproj -c Release --nologo
            """.trimIndent()
        }
        script {
            name = "Build SourceGenerators (Roslyn analyzer)"
            scriptContent = """
                set -e
                "${'$'}HOME/.dotnet/dotnet" build Code/Tools/SourceGenerators/O3DESharp.SourceGenerators.csproj -c Release --nologo
            """.trimIndent()
        }
        script {
            name = "Build SourceGenerators smoke consumer (Phase 18-E end-to-end)"
            scriptContent = """
                set -e
                "${'$'}HOME/.dotnet/dotnet" build Code/Tools/SourceGenerators.Tests/SourceGenerators.Smoke.csproj -c Release --nologo
            """.trimIndent()
        }
        script {
            name = "Run BindingGenerator xUnit tests"
            scriptContent = """
                set -e
                "${'$'}HOME/.dotnet/dotnet" test Code/Tools/BindingGenerator.Tests/BindingGenerator.Tests.csproj -c Release --nologo --logger "console;verbosity=normal"
            """.trimIndent()
        }
        script {
            name = "Run O3DE.Core xUnit tests"
            scriptContent = """
                set -e
                "${'$'}HOME/.dotnet/dotnet" test Assets/Scripts/O3DE.Core.Tests/O3DE.Core.Tests.csproj -c Release --nologo --logger "console;verbosity=normal"
            """.trimIndent()
        }
    }

    triggers {
        vcs {
            branchFilter = "+:*"
            triggerRules = """
                -:**/*.md
                -:LICENSE*
                -:.gitignore
            """.trimIndent()
        }
    }

    features {
        commitStatusPublisher {
            publisher = github {
                githubUrl = "https://api.github.com"
                authType = personalToken {
                    token = "credentialsJSON:github_access_token"
                }
            }
        }
        perfmon { }
    }
})

// --------------------------------------------------------------------
// C# build & test on Windows
// --------------------------------------------------------------------

object CSharpWindows : BuildType({
    id("CSharpWindows")
    name = "C# build & test (Windows)"
    description = "Builds the C# tooling + runs the xUnit suite on a Windows cloud agent"

    vcs {
        root(O3DESharpVcs)
        cleanCheckout = false
    }

    requirements {
        equals("teamcity.agent.os.family", "Windows")
    }

    steps {
        script {
            name = "Install .NET 9.0 SDK"
            // TC Cloud Windows agents include .NET 8 LTS but not .NET 9.
            // The script runner wraps content in a .cmd file, so all
            // syntax must be cmd.exe-compatible: invoke powershell.exe
            // explicitly for the download/install step, then use
            // %LOCALAPPDATA% (cmd.exe variable expansion) for all
            // subsequent explicit dotnet.exe paths.
            scriptContent = """
                powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "Invoke-WebRequest -UseBasicParsing -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile '%TEMP%\dotnet-install.ps1'"
                if %errorlevel% neq 0 exit /b %errorlevel%
                powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "%TEMP%\dotnet-install.ps1" -Channel 9.0 -InstallDir "%LOCALAPPDATA%\Microsoft\dotnet"
                if %errorlevel% neq 0 exit /b %errorlevel%
                "%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe" --list-sdks
                if %errorlevel% neq 0 exit /b %errorlevel%
            """.trimIndent()
        }
        script {
            name = "Build O3DE.Core (net9)"
            scriptContent = """
                "%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe" build Assets/Scripts/O3DE.Core/O3DE.Core.csproj -c Release --nologo
                if %errorlevel% neq 0 exit /b %errorlevel%
            """.trimIndent()
        }
        script {
            name = "Build BindingGenerator (net8 tool)"
            scriptContent = """
                "%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe" build Code/Tools/BindingGenerator/O3DESharp.BindingGenerator/O3DESharp.BindingGenerator.csproj -c Release --nologo
                if %errorlevel% neq 0 exit /b %errorlevel%
            """.trimIndent()
        }
        script {
            name = "Build BindingGenerator.Tasks (netstandard2.0 MSBuild task)"
            scriptContent = """
                "%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe" build Code/Tools/BindingGenerator/O3DESharp.BindingGenerator.Tasks/O3DESharp.BindingGenerator.Tasks.csproj -c Release --nologo
                if %errorlevel% neq 0 exit /b %errorlevel%
            """.trimIndent()
        }
        script {
            name = "Build SourceGenerators (Roslyn analyzer)"
            scriptContent = """
                "%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe" build Code/Tools/SourceGenerators/O3DESharp.SourceGenerators.csproj -c Release --nologo
                if %errorlevel% neq 0 exit /b %errorlevel%
            """.trimIndent()
        }
        script {
            name = "Build SourceGenerators smoke consumer (Phase 18-E end-to-end)"
            scriptContent = """
                "%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe" build Code/Tools/SourceGenerators.Tests/SourceGenerators.Smoke.csproj -c Release --nologo
                if %errorlevel% neq 0 exit /b %errorlevel%
            """.trimIndent()
        }
        script {
            name = "Run BindingGenerator xUnit tests"
            scriptContent = """
                "%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe" test Code/Tools/BindingGenerator.Tests/BindingGenerator.Tests.csproj -c Release --nologo --logger "console;verbosity=normal"
                if %errorlevel% neq 0 exit /b %errorlevel%
            """.trimIndent()
        }
        script {
            name = "Run O3DE.Core xUnit tests"
            scriptContent = """
                "%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe" test Assets/Scripts/O3DE.Core.Tests/O3DE.Core.Tests.csproj -c Release --nologo --logger "console;verbosity=normal"
                if %errorlevel% neq 0 exit /b %errorlevel%
            """.trimIndent()
        }
    }

    triggers {
        vcs {
            branchFilter = "+:*"
            triggerRules = """
                -:**/*.md
                -:LICENSE*
                -:.gitignore
            """.trimIndent()
        }
    }

    features {
        commitStatusPublisher {
            publisher = github {
                githubUrl = "https://api.github.com"
                authType = personalToken {
                    token = "credentialsJSON:github_access_token"
                }
            }
        }
        perfmon { }
    }
})

// --------------------------------------------------------------------
// Python editor tests
// --------------------------------------------------------------------

object PythonTests : BuildType({
    id("PythonTests")
    name = "Python editor tests"
    description = "Runs the pytest suite under Editor/Tests"

    vcs {
        root(O3DESharpVcs)
        cleanCheckout = false
    }

    requirements {
        equals("teamcity.agent.os.family", "Linux")
    }

    steps {
        script {
            name = "Install pytest"
            scriptContent = """
                set -e
                python3 --version
                python3 -m pip install --upgrade --quiet pip pytest --break-system-packages
                python3 -m pytest --version
            """.trimIndent()
        }
        script {
            name = "Run pytest"
            workingDir = "Editor/Tests"
            scriptContent = """
                set -e
                python3 -m pytest -v --tb=short
            """.trimIndent()
        }
    }

    triggers {
        vcs {
            branchFilter = "+:*"
            triggerRules = """
                -:**/*.md
                -:LICENSE*
                -:.gitignore
            """.trimIndent()
        }
    }

    features {
        commitStatusPublisher {
            publisher = github {
                githubUrl = "https://api.github.com"
                authType = personalToken {
                    token = "credentialsJSON:github_access_token"
                }
            }
        }
        perfmon { }
    }
})

// --------------------------------------------------------------------
// Composite aggregator
// --------------------------------------------------------------------

object AllChecks : BuildType({
    id("AllChecks")
    name = "All checks"
    description = "Aggregate status across the C# Linux + Windows + Python builds"
    type = Type.COMPOSITE

    vcs {
        root(O3DESharpVcs)
        showDependenciesChanges = true
    }

    dependencies {
        snapshot(CSharpLinux) {
            onDependencyFailure = FailureAction.FAIL_TO_START
        }
        snapshot(CSharpWindows) {
            onDependencyFailure = FailureAction.FAIL_TO_START
        }
        snapshot(PythonTests) {
            onDependencyFailure = FailureAction.FAIL_TO_START
        }
    }

    triggers {
        vcs {
            branchFilter = "+:*"
        }
    }

    features {
        commitStatusPublisher {
            publisher = github {
                githubUrl = "https://api.github.com"
                authType = personalToken {
                    token = "credentialsJSON:github_access_token"
                }
            }
        }
    }
})
