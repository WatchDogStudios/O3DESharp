/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 */

import jetbrains.buildServer.configs.kotlin.*
import jetbrains.buildServer.configs.kotlin.buildFeatures.commitStatusPublisher
import jetbrains.buildServer.configs.kotlin.buildFeatures.perfmon
import jetbrains.buildServer.configs.kotlin.buildSteps.dotnetBuild
import jetbrains.buildServer.configs.kotlin.buildSteps.dotnetTest
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
 */

version = "2024.03"

project {
    description = "C# scripting layer for Open 3D Engine via the Coral .NET host"

    vcsRoot(O3DESharpVcs)

    buildType(CSharpLinux)
    buildType(CSharpWindows)
    buildType(PythonTests)
    buildType(AllChecks)

    // Default project params. Override via TeamCity Cloud > Admin >
    // Parameters if you need a different agent pool or feature flag.
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
    // 'main' is the long-lived release branch (development merges
    // in via PR). The branchSpec below picks up every branch and
    // every PR head, so triggers fire on PR pushes against both
    // main and development.
    branch = "refs/heads/main"
    branchSpec = """
        +:refs/heads/*
        +:refs/pull/(*)/head
    """.trimIndent()
    // The Cloud GitHub App handles authentication; no token needed
    // in this file. If you self-host TeamCity later, populate via
    // an authMethod block here.
})

// --------------------------------------------------------------------
// Helpers
// --------------------------------------------------------------------

/*
 * Adds the dotnet build + test step sequence that's shared between
 * the Linux and Windows C# build configurations. The platform-
 * specific SDK install step is added separately by the caller
 * BEFORE invoking this helper, so the step order in the resulting
 * BuildType is "SDK install" then everything here.
 */
fun BuildType.addDotnetMatrixSteps() {
    steps {
        // 1. Build O3DE.Core (net9). The smoke consumer references
        //    this, so it has to land first.
        dotnetBuild {
            name = "Build O3DE.Core (net9)"
            projects = "Assets/Scripts/O3DE.Core/O3DE.Core.csproj"
            configuration = "Release"
            args = "--nologo"
        }

        // 2. ClangSharp-based binding generator + its MSBuild task.
        //    These build O3DESharp.BindingGenerator.dll and the .Tasks
        //    MSBuild plugin that user csprojs load to design-time
        //    generate bindings.
        dotnetBuild {
            name = "Build BindingGenerator (net8 tool)"
            projects = "Code/Tools/BindingGenerator/O3DESharp.BindingGenerator/O3DESharp.BindingGenerator.csproj"
            configuration = "Release"
            args = "--nologo"
        }

        dotnetBuild {
            name = "Build BindingGenerator.Tasks (netstandard2.0 MSBuild task)"
            projects = "Code/Tools/BindingGenerator/O3DESharp.BindingGenerator.Tasks/O3DESharp.BindingGenerator.Tasks.csproj"
            configuration = "Release"
            args = "--nologo"
        }

        // 3. Roslyn source generator (Phase 18-E). Targets
        //    netstandard2.0 since Roslyn analyzers are restricted to
        //    that framework. The Smoke consumer below references it
        //    as an Analyzer.
        dotnetBuild {
            name = "Build SourceGenerators (Roslyn analyzer)"
            projects = "Code/Tools/SourceGenerators/O3DESharp.SourceGenerators.csproj"
            configuration = "Release"
            args = "--nologo"
        }

        // 4. SourceGenerators smoke consumer. Drives the [EBus] /
        //    [EBusHandler] generator end-to-end against a partial
        //    ScriptComponent with primitive args, math args, zero-
        //    arg events, and an 8-arg event. If the build succeeds
        //    the analyzer emitted compilable C#; if the emit shape
        //    regresses this is the first thing that fails.
        dotnetBuild {
            name = "Build SourceGenerators smoke consumer (Phase 18-E end-to-end)"
            projects = "Code/Tools/SourceGenerators.Tests/SourceGenerators.Smoke.csproj"
            configuration = "Release"
            args = "--nologo"
        }

        // 5. xUnit test suite. 104 tests at last count.
        dotnetTest {
            name = "Run BindingGenerator xUnit tests"
            projects = "Code/Tools/BindingGenerator.Tests/BindingGenerator.Tests.csproj"
            configuration = "Release"
            args = """--nologo --logger "console;verbosity=normal""""
        }
    }
}

/*
 * Shared trigger + feature block: VCS trigger that ignores
 * docs-only changes, GitHub commit-status publisher, perfmon.
 */
fun BuildType.addStandardTriggersAndFeatures() {
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
                    // The token is supplied via the project-level
                    // parameter gh-status-token, set in the
                    // TeamCity Cloud UI as a stored secret. Don't
                    // paste a real token here.
                    token = "credentialsJSON:gh-status-token"
                }
            }
        }
        perfmon { }
    }
}

// --------------------------------------------------------------------
// Build configurations
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
        // TC Cloud agent OS labels: "Linux" or "Windows".
        equals("teamcity.agent.os.family", "Linux")
    }

    // SDK install runs first; matrix steps appended after via the
    // addDotnetMatrixSteps() call below. The step order in the
    // generated BuildType matches the order steps are added.
    steps {
        script {
            name = "Install .NET 8.0 + 9.0 SDKs"
            scriptContent = """
                set -e
                # dotnet-install: idempotent installer. Channel 8.0
                # + 9.0 each install the latest patch into ~/.dotnet,
                # then we prepend to PATH for the rest of the build.
                if [ ! -f /tmp/dotnet-install.sh ]; then
                    curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
                    chmod +x /tmp/dotnet-install.sh
                fi
                /tmp/dotnet-install.sh --channel 8.0 --install-dir "${'$'}HOME/.dotnet"
                /tmp/dotnet-install.sh --channel 9.0 --install-dir "${'$'}HOME/.dotnet"
                echo "##teamcity[setParameter name='env.PATH' value='${'$'}HOME/.dotnet:${'$'}PATH']"
                "${'$'}HOME/.dotnet/dotnet" --list-sdks
            """.trimIndent()
        }
    }
    addDotnetMatrixSteps()

    addStandardTriggersAndFeatures()
})

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
        // Windows TC Cloud agents ship with VS Build Tools + a
        // recent .NET SDK. The preinstalled SDK is usually .NET 8
        // LTS; the 9.0 SDK we need is added per build.
        script {
            name = "Install .NET 9.0 SDK"
            scriptContent = """
                Invoke-WebRequest -UseBasicParsing -Uri https://dot.net/v1/dotnet-install.ps1 -OutFile dotnet-install.ps1
                ./dotnet-install.ps1 -Channel 9.0 -InstallDir "${'$'}env:LOCALAPPDATA\Microsoft\dotnet"
                ${'$'}env:PATH = "${'$'}env:LOCALAPPDATA\Microsoft\dotnet;${'$'}env:PATH"
                Write-Host "##teamcity[setParameter name='env.PATH' value='${'$'}env:PATH']"
                & "${'$'}env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" --list-sdks
            """.trimIndent()
        }
    }
    addDotnetMatrixSteps()

    addStandardTriggersAndFeatures()
})

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
                python3 -m pip install --upgrade --quiet pip pytest
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

    addStandardTriggersAndFeatures()
})

/*
 * Aggregator build. Depends on all three matrix entries; turning
 * green only when every one of them is green. Branch protection
 * rules in GitHub should require THIS build to pass, not the
 * individual matrix entries - that way the required check name is
 * stable even if we add or rename a matrix entry later.
 */
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
                    token = "credentialsJSON:gh-status-token"
                }
            }
        }
    }
})
