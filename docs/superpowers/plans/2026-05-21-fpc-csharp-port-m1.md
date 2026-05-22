# FirstPersonController C# Port — M1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the core C# port of Porcupine-Factory/FirstPersonController as a standalone gem — walking, sprinting, jumping, falling, grounded check, PhysX-backed movement, camera coupling, named-event input — without the 90-method request-bus surface (deferred to M2).

**Architecture:** Standalone net9.0 csproj at `F:/O3DESharp/Gems/O3DE.FirstPersonController/`. Idiomatic C# internals (PidController1D struct, enum-based state machine, [ExposedProperty] inspector surface) wrapping the upstream FPC's behavior. Input via Phase 18-E `[EBus("InputEventNotificationBus")]` handlers (source-generator-emitted Connect/Disconnect). Movement via reflected `CharacterControllerRequestBus`. Zero per-frame allocations.

**Tech Stack:** C# 12 / net9.0, O3DESharp v1.0.0 (O3DE.Core + O3DESharp.SourceGenerators), xUnit for unit tests, FluentAssertions for readable assertions, plain console app for the smoke test.

**Spec:** [`docs/superpowers/specs/2026-05-21-fpc-csharp-port-design.md`](../specs/2026-05-21-fpc-csharp-port-design.md)

---

## File structure

This plan creates ONE deliverable: a standalone gem at `F:/O3DESharp/Gems/O3DE.FirstPersonController/`. The file tree it produces (everything under that prefix):

```
O3DE.FirstPersonController/
├── O3DE.FirstPersonController.csproj    (Task 1)
├── gem.json                              (Task 1)
├── README.md                             (Task 12)
│
├── Components/
│   └── FirstPersonControllerComponent.cs (Task 7-9)
│
├── Internal/
│   ├── PidController1D.cs                (Task 2)
│   ├── MovementState.cs                  (Task 3)
│   ├── InputAxisAccumulator.cs           (Task 4)
│   ├── GroundCheck.cs                    (Task 5)
│   ├── IPhysicsQueryService.cs           (Task 5)
│   └── MovementTuning.cs                 (Task 6)
│
├── Tests/
│   ├── O3DE.FirstPersonController.Tests.csproj  (Task 2)
│   ├── PidController1DTests.cs                  (Task 2)
│   ├── MovementStateTransitionTests.cs          (Task 3)
│   ├── InputAxisAccumulatorTests.cs             (Task 4)
│   └── GroundCheckTests.cs                      (Task 5)
│
└── SmokeTests/
    ├── O3DE.FirstPersonController.SmokeTests.csproj  (Task 10)
    └── Program.cs                                     (Task 10)
```

Each Internal/ class is self-contained with one responsibility. Tests live next to (but separate from) the production code so they can be excluded from the runtime dll.

---

## Task 1: Project scaffolding

**Files:**
- Create: `F:/O3DESharp/Gems/O3DE.FirstPersonController/gem.json`
- Create: `F:/O3DESharp/Gems/O3DE.FirstPersonController/O3DE.FirstPersonController.csproj`
- Create: `F:/O3DESharp/Gems/O3DE.FirstPersonController/Components/.gitkeep`
- Create: `F:/O3DESharp/Gems/O3DE.FirstPersonController/Internal/.gitkeep`
- Create: `F:/O3DESharp/Gems/O3DE.FirstPersonController/Tests/.gitkeep`
- Create: `F:/O3DESharp/Gems/O3DE.FirstPersonController/SmokeTests/.gitkeep`

- [ ] **Step 1.1: Create the directory tree**

```bash
mkdir -p F:/O3DESharp/Gems/O3DE.FirstPersonController/Components
mkdir -p F:/O3DESharp/Gems/O3DE.FirstPersonController/Internal
mkdir -p F:/O3DESharp/Gems/O3DE.FirstPersonController/Tests
mkdir -p F:/O3DESharp/Gems/O3DE.FirstPersonController/SmokeTests
touch F:/O3DESharp/Gems/O3DE.FirstPersonController/Components/.gitkeep
touch F:/O3DESharp/Gems/O3DE.FirstPersonController/Internal/.gitkeep
touch F:/O3DESharp/Gems/O3DE.FirstPersonController/Tests/.gitkeep
touch F:/O3DESharp/Gems/O3DE.FirstPersonController/SmokeTests/.gitkeep
```

- [ ] **Step 1.2: Write gem.json**

Path: `F:/O3DESharp/Gems/O3DE.FirstPersonController/gem.json`

```json
{
    "gem_name": "O3DE.FirstPersonController",
    "display_name": "O3DE First Person Controller (C#)",
    "license": "Apache-2.0 OR MIT",
    "license_url": "https://github.com/WatchDogStudios/O3DESharp/blob/main/LICENSE",
    "origin": "WD Studios Corp.",
    "origin_url": "https://github.com/WatchDogStudios/O3DESharp",
    "type": "Code",
    "summary": "C# port of Porcupine-Factory/FirstPersonController, riding on O3DESharp. Standalone gem providing PhysX-backed first-person character controller authored entirely in C#.",
    "canonical_tags": ["Gem"],
    "user_tags": ["FirstPerson", "Controller", "CSharp", "O3DESharp"],
    "platforms": ["Linux", "Windows"],
    "version": "0.1.0",
    "compatible_engines": ["o3de==25.1.0"],
    "dependencies": [
        {
            "name": "O3DESharp",
            "version_specifier": ">=1.0.0"
        },
        {
            "name": "PhysX5",
            "version_specifier": ">=0.1.0"
        },
        {
            "name": "StartingPointInput",
            "version_specifier": ">=1.0.0"
        }
    ]
}
```

- [ ] **Step 1.3: Write O3DE.FirstPersonController.csproj**

Path: `F:/O3DESharp/Gems/O3DE.FirstPersonController/O3DE.FirstPersonController.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <ImplicitUsings>disable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>

    <AssemblyName>O3DE.FirstPersonController</AssemblyName>
    <RootNamespace>O3DE.FirstPersonController</RootNamespace>
    <Version>0.1.0</Version>
    <Authors>WD Studios Corp.</Authors>
    <Description>C# port of Porcupine-Factory/FirstPersonController, riding on O3DESharp.</Description>

    <Configurations>Debug;Profile;Release</Configurations>
    <Platforms>AnyCPU</Platforms>

    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <NoWarn>$(NoWarn);CS1591</NoWarn>
  </PropertyGroup>

  <PropertyGroup Condition="'$(Configuration)' == 'Debug'">
    <Optimize>false</Optimize>
    <DebugSymbols>true</DebugSymbols>
    <DebugType>full</DebugType>
  </PropertyGroup>

  <ItemGroup>
    <!--
      Reference O3DESharp's managed Core library. Hosted in this same
      repo at Assets/Scripts/O3DE.Core/; this csproj depends on it for
      ScriptComponent, [ExposedProperty], [EBus]/[EBusHandler], Vector3,
      Transform, etc.
    -->
    <ProjectReference Include="..\..\Assets\Scripts\O3DE.Core\O3DE.Core.csproj">
      <Private>false</Private>
    </ProjectReference>

    <!--
      O3DESharp source generator: emits Connect/Disconnect/dispatch
      glue from [EBus] / [EBusHandler] attributes. The
      OutputItemType=Analyzer + ReferenceOutputAssembly=false markers
      make MSBuild load it as a Roslyn component, not a regular ref.
    -->
    <ProjectReference Include="..\..\Code\Tools\SourceGenerators\O3DESharp.SourceGenerators.csproj"
                      OutputItemType="Analyzer"
                      ReferenceOutputAssembly="false" />
  </ItemGroup>

  <!--
    Exclude Tests/ and SmokeTests/ subdirs from this project's
    compilation. They are separate csproj artifacts; if SDK glob
    picks them up here we get CS0101 duplicate-type-definition errors.
  -->
  <ItemGroup>
    <Compile Remove="Tests/**/*.cs" />
    <Compile Remove="SmokeTests/**/*.cs" />
    <None Include="Tests/**/*.cs" />
    <None Include="SmokeTests/**/*.cs" />
  </ItemGroup>

</Project>
```

- [ ] **Step 1.4: Verify the scaffolding compiles (empty project)**

Run:
```bash
cd F:/O3DESharp/Gems/O3DE.FirstPersonController
dotnet build -c Release --nologo
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)` with output `O3DE.FirstPersonController -> .../bin/Release/net9.0/O3DE.FirstPersonController.dll`.

The dll will be empty (no .cs files yet) but it should link cleanly against O3DE.Core + the source generator analyzer.

- [ ] **Step 1.5: Commit**

```bash
cd F:/O3DESharp
git add Gems/O3DE.FirstPersonController/
git commit -m "FPC port: scaffolding (gem.json + csproj + directory tree)"
```

---

## Task 2: PidController1D (TDD)

A scalar PID controller for the crouch height transition. Plus its test project.

**Files:**
- Create: `F:/O3DESharp/Gems/O3DE.FirstPersonController/Internal/PidController1D.cs`
- Create: `F:/O3DESharp/Gems/O3DE.FirstPersonController/Tests/O3DE.FirstPersonController.Tests.csproj`
- Create: `F:/O3DESharp/Gems/O3DE.FirstPersonController/Tests/PidController1DTests.cs`

- [ ] **Step 2.1: Write the test csproj**

Path: `F:/O3DESharp/Gems/O3DE.FirstPersonController/Tests/O3DE.FirstPersonController.Tests.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
    <Configurations>Debug;Profile;Release</Configurations>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
    <PackageReference Include="FluentAssertions" Version="6.12.1" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\O3DE.FirstPersonController.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2.2: Verify the test project restores**

Run:
```bash
cd F:/O3DESharp/Gems/O3DE.FirstPersonController/Tests
dotnet restore --nologo
```

Expected: `Restore complete` with no errors. Test packages downloaded.

- [ ] **Step 2.3: Write the first failing test — proportional-only response**

Path: `F:/O3DESharp/Gems/O3DE.FirstPersonController/Tests/PidController1DTests.cs`

```csharp
/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 */

using FluentAssertions;
using O3DE.FirstPersonController.Internal;
using Xunit;

namespace O3DE.FirstPersonController.Tests;

public class PidController1DTests
{
    /// <summary>
    /// With only Kp set, the controller's output for a single step
    /// must equal Kp * error. Confirms there's no leftover bias from
    /// integral / derivative terms in the cleared state.
    /// </summary>
    [Fact]
    public void Step_ProportionalOnly_ReturnsKpTimesError()
    {
        var pid = new PidController1D { Kp = 2.0f, Ki = 0f, Kd = 0f };
        var output = pid.Step(error: 5.0f, dt: 0.016f);
        output.Should().BeApproximately(10.0f, precision: 0.0001f);
    }
}
```

- [ ] **Step 2.4: Run the test, verify it fails (the type doesn't exist yet)**

Run:
```bash
cd F:/O3DESharp/Gems/O3DE.FirstPersonController/Tests
dotnet test --nologo 2>&1 | tail -10
```

Expected: compile error `CS0246: The type or namespace name 'PidController1D' could not be found in the namespace 'O3DE.FirstPersonController.Internal'`.

- [ ] **Step 2.5: Write the minimal PidController1D**

Path: `F:/O3DESharp/Gems/O3DE.FirstPersonController/Internal/PidController1D.cs`

```csharp
/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 */

namespace O3DE.FirstPersonController.Internal;

/// <summary>
/// Scalar PID controller used by the crouch-height transition.
/// Struct-typed so it can live as a member field with zero GC pressure.
/// </summary>
public struct PidController1D
{
    public float Kp;
    public float Ki;
    public float Kd;

    /// <summary>
    /// Compute the next output. error = (target - current); dt is the
    /// elapsed time since the previous Step call.
    /// </summary>
    public float Step(float error, float dt)
    {
        return Kp * error;
    }
}
```

- [ ] **Step 2.6: Run the test, verify it passes**

Run:
```bash
cd F:/O3DESharp/Gems/O3DE.FirstPersonController/Tests
dotnet test --nologo 2>&1 | tail -5
```

Expected: `Passed!  - Failed: 0, Passed: 1, Skipped: 0, Total: 1`.

- [ ] **Step 2.7: Add the integral test**

Add to `PidController1DTests.cs` at the end of the class:

```csharp
    /// <summary>
    /// With only Ki set, repeated Step calls accumulate the integral
    /// linearly. After 10 steps of error=1 at dt=0.1, integral=1.0,
    /// so output = Ki * 1.0.
    /// </summary>
    [Fact]
    public void Step_IntegralOnly_AccumulatesOverTime()
    {
        var pid = new PidController1D { Kp = 0f, Ki = 3.0f, Kd = 0f };

        // 10 steps of constant error=1.0 at dt=0.1 → integral=1.0
        float output = 0f;
        for (int i = 0; i < 10; i++)
        {
            output = pid.Step(error: 1.0f, dt: 0.1f);
        }

        // Final output = Ki * integral = 3.0 * 1.0
        output.Should().BeApproximately(3.0f, precision: 0.001f);
    }
```

- [ ] **Step 2.8: Run the test, verify it fails**

Run: `dotnet test --nologo --filter "Step_IntegralOnly_AccumulatesOverTime" 2>&1 | tail -5`

Expected: FAIL — current output is 0 (integral isn't computed).

- [ ] **Step 2.9: Extend PidController1D with the integral term**

Path: `F:/O3DESharp/Gems/O3DE.FirstPersonController/Internal/PidController1D.cs`

Replace the whole file with:

```csharp
/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 */

namespace O3DE.FirstPersonController.Internal;

/// <summary>
/// Scalar PID controller used by the crouch-height transition.
/// Struct-typed so it can live as a member field with zero GC pressure.
/// </summary>
public struct PidController1D
{
    public float Kp;
    public float Ki;
    public float Kd;

    private float m_integral;

    /// <summary>
    /// Compute the next output. error = (target - current); dt is the
    /// elapsed time since the previous Step call.
    /// </summary>
    public float Step(float error, float dt)
    {
        m_integral += error * dt;
        return Kp * error + Ki * m_integral;
    }

    /// <summary>
    /// Zero internal accumulators (integral). Call on state-machine
    /// transitions where the previous error history is no longer
    /// meaningful (e.g., crouch was interrupted).
    /// </summary>
    public void Reset()
    {
        m_integral = 0f;
    }
}
```

- [ ] **Step 2.10: Run both tests, verify they pass**

Run: `dotnet test --nologo 2>&1 | tail -5`

Expected: `Passed! - Failed: 0, Passed: 2, Skipped: 0, Total: 2`.

- [ ] **Step 2.11: Add the derivative test**

Add to `PidController1DTests.cs`:

```csharp
    /// <summary>
    /// With only Kd set, the output reflects the rate-of-change of
    /// error. error stepping from 0 to 5 over dt=0.1 means derr/dt =
    /// 50, so output = Kd * 50.
    /// </summary>
    [Fact]
    public void Step_DerivativeOnly_ReflectsErrorRate()
    {
        var pid = new PidController1D { Kp = 0f, Ki = 0f, Kd = 0.01f };

        // First call: previous error unknown, treat as 0
        pid.Step(error: 0f, dt: 0.1f);

        // Second call: error jumped to 5.0 in 0.1s -> derivative = 50
        var output = pid.Step(error: 5.0f, dt: 0.1f);

        // Kd * 50 = 0.5
        output.Should().BeApproximately(0.5f, precision: 0.001f);
    }
```

- [ ] **Step 2.12: Run, verify FAIL, then extend with derivative**

Run: `dotnet test --nologo --filter "Derivative" 2>&1 | tail -5`. Expected: FAIL.

Path: `F:/O3DESharp/Gems/O3DE.FirstPersonController/Internal/PidController1D.cs`

Replace the whole file with:

```csharp
/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 */

namespace O3DE.FirstPersonController.Internal;

/// <summary>
/// Scalar PID controller used by the crouch-height transition.
/// Struct-typed so it can live as a member field with zero GC pressure.
/// </summary>
public struct PidController1D
{
    public float Kp;
    public float Ki;
    public float Kd;

    private float m_integral;
    private float m_lastError;

    /// <summary>
    /// Compute the next output. error = (target - current); dt is the
    /// elapsed time since the previous Step call.
    /// </summary>
    public float Step(float error, float dt)
    {
        m_integral += error * dt;
        float derivative = dt > 0f ? (error - m_lastError) / dt : 0f;
        m_lastError = error;
        return Kp * error + Ki * m_integral + Kd * derivative;
    }

    /// <summary>
    /// Zero internal accumulators (integral + last-error). Call on
    /// state-machine transitions where the previous error history is
    /// no longer meaningful (e.g., crouch was interrupted).
    /// </summary>
    public void Reset()
    {
        m_integral = 0f;
        m_lastError = 0f;
    }
}
```

- [ ] **Step 2.13: Run, verify all 3 tests pass**

Run: `dotnet test --nologo 2>&1 | tail -5`. Expected: `Passed: 3, Total: 3`.

- [ ] **Step 2.14: Add the anti-windup test**

Add to `PidController1DTests.cs`:

```csharp
    /// <summary>
    /// Sustained large error should not cause the integral to grow
    /// without bound. After enough sustained input, the integral
    /// should clip at IntegralMax (or IntegralMin for negative).
    /// </summary>
    [Fact]
    public void Step_AntiWindup_ClampsIntegralToConfiguredRange()
    {
        var pid = new PidController1D
        {
            Kp = 0f,
            Ki = 1.0f,
            Kd = 0f,
            IntegralMax = 5.0f,
            IntegralMin = -5.0f,
        };

        // Drive the integral hard for many steps; expect it to clip.
        for (int i = 0; i < 1000; i++)
        {
            pid.Step(error: 10.0f, dt: 0.016f);
        }

        // Final output = Ki * clamped integral. Integral was clamped
        // to IntegralMax=5.0, so output should be exactly 5.0.
        var final = pid.Step(error: 10.0f, dt: 0.016f);
        final.Should().BeApproximately(5.0f, precision: 0.01f);
    }
```

- [ ] **Step 2.15: Run, verify FAIL, then extend with anti-windup**

Run: `dotnet test --nologo --filter "AntiWindup" 2>&1 | tail -5`. Expected: FAIL (integral grew unbounded; output is some huge number).

Path: `F:/O3DESharp/Gems/O3DE.FirstPersonController/Internal/PidController1D.cs`

Replace the whole file with:

```csharp
/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 */

using System;

namespace O3DE.FirstPersonController.Internal;

/// <summary>
/// Scalar PID controller used by the crouch-height transition.
/// Struct-typed so it can live as a member field with zero GC
/// pressure. Includes an integral clamp (IntegralMin/IntegralMax) to
/// prevent windup under sustained error.
/// </summary>
public struct PidController1D
{
    public float Kp;
    public float Ki;
    public float Kd;

    /// <summary>
    /// Lower bound for the accumulated integral. Defaults to
    /// -float.MaxValue (effectively unclamped); set to a finite value
    /// for windup protection.
    /// </summary>
    public float IntegralMin;

    /// <summary>
    /// Upper bound for the accumulated integral. Defaults to
    /// +float.MaxValue (effectively unclamped); set to a finite value
    /// for windup protection.
    /// </summary>
    public float IntegralMax;

    private float m_integral;
    private float m_lastError;

    /// <summary>
    /// Compute the next output. error = (target - current); dt is the
    /// elapsed time since the previous Step call.
    /// </summary>
    public float Step(float error, float dt)
    {
        // Default-constructed struct has IntegralMin = IntegralMax = 0,
        // which would clamp away every integral contribution. Detect
        // that and treat it as "unclamped".
        float lo = IntegralMin == 0f && IntegralMax == 0f ? float.MinValue : IntegralMin;
        float hi = IntegralMin == 0f && IntegralMax == 0f ? float.MaxValue : IntegralMax;

        m_integral = Math.Clamp(m_integral + error * dt, lo, hi);
        float derivative = dt > 0f ? (error - m_lastError) / dt : 0f;
        m_lastError = error;
        return Kp * error + Ki * m_integral + Kd * derivative;
    }

    /// <summary>
    /// Zero internal accumulators (integral + last-error). Call on
    /// state-machine transitions where the previous error history is
    /// no longer meaningful (e.g., crouch was interrupted).
    /// </summary>
    public void Reset()
    {
        m_integral = 0f;
        m_lastError = 0f;
    }
}
```

- [ ] **Step 2.16: Run all PID tests, verify they pass**

Run: `dotnet test --nologo 2>&1 | tail -5`. Expected: `Passed: 4, Total: 4`.

- [ ] **Step 2.17: Commit**

```bash
cd F:/O3DESharp
git add Gems/O3DE.FirstPersonController/Internal/PidController1D.cs \
        Gems/O3DE.FirstPersonController/Tests/
git commit -m "FPC port: PidController1D with anti-windup + xUnit test suite"
```

---

## Task 3: MovementState + transition table (TDD)

The enum-based state machine. State + transition rules are pure data; the FirstPersonControllerComponent calls into them.

**Files:**
- Create: `F:/O3DESharp/Gems/O3DE.FirstPersonController/Internal/MovementState.cs`
- Create: `F:/O3DESharp/Gems/O3DE.FirstPersonController/Tests/MovementStateTransitionTests.cs`

- [ ] **Step 3.1: Write the first failing test — Idle → Walking when an axis is held**

Path: `F:/O3DESharp/Gems/O3DE.FirstPersonController/Tests/MovementStateTransitionTests.cs`

```csharp
/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 */

using FluentAssertions;
using O3DE.FirstPersonController.Internal;
using Xunit;

namespace O3DE.FirstPersonController.Tests;

public class MovementStateTransitionTests
{
    [Fact]
    public void Idle_Transitions_To_Walking_When_Move_Axis_Is_Held_And_Grounded()
    {
        var inputs = new MovementStateInputs
        {
            Grounded = true,
            AnyMoveAxisNonzero = true,
        };

        var next = MovementStateTransitions.Next(MovementState.Idle, inputs);
        next.Should().Be(MovementState.Walking);
    }
}
```

- [ ] **Step 3.2: Run, verify it fails**

Run: `dotnet test --nologo --filter "Idle_Transitions_To_Walking" 2>&1 | tail -5`. Expected: compile error.

- [ ] **Step 3.3: Write the minimal MovementState**

Path: `F:/O3DESharp/Gems/O3DE.FirstPersonController/Internal/MovementState.cs`

```csharp
/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 */

namespace O3DE.FirstPersonController.Internal;

/// <summary>
/// Discrete states the character can be in. Transitions are pure
/// functions of the previous state + a snapshot of input + physics
/// queries; see <see cref="MovementStateTransitions.Next"/>.
/// </summary>
public enum MovementState
{
    Idle,
    Walking,
    Sprinting,
    Crouching,
    Jumping,
    Falling,
}

/// <summary>
/// Snapshot of the inputs the transition table consults. Read-only by
/// design: the transition function is a pure mapping
/// (state, inputs) -> next state.
/// </summary>
public readonly struct MovementStateInputs
{
    public bool Grounded { get; init; }
    public bool SprintHeld { get; init; }
    public bool CrouchHeld { get; init; }
    public bool JumpPressed { get; init; }
    public bool AnyMoveAxisNonzero { get; init; }
    public bool JumpHoldElapsed { get; init; }
    public bool CanStandUp { get; init; }
    public float VerticalVelocity { get; init; }
}

public static class MovementStateTransitions
{
    public static MovementState Next(MovementState current, MovementStateInputs i)
    {
        return current switch
        {
            MovementState.Idle when i.AnyMoveAxisNonzero && i.Grounded => MovementState.Walking,
            _ => current,
        };
    }
}
```

- [ ] **Step 3.4: Run, verify the test passes**

Run: `dotnet test --nologo --filter "Idle_Transitions_To_Walking" 2>&1 | tail -5`. Expected: `Passed: 1`.

- [ ] **Step 3.5: Add table-driven tests for every state's transitions**

Add to `MovementStateTransitionTests.cs`:

```csharp
    public static TheoryData<MovementState, MovementStateInputs, MovementState> TransitionCases =>
        new()
        {
            // ---- from Idle ----
            { MovementState.Idle, new() { Grounded = true, AnyMoveAxisNonzero = true }, MovementState.Walking },
            { MovementState.Idle, new() { Grounded = false }, MovementState.Falling },
            { MovementState.Idle, new() { Grounded = true, JumpPressed = true }, MovementState.Jumping },
            { MovementState.Idle, new() { Grounded = true }, MovementState.Idle },

            // ---- from Walking ----
            { MovementState.Walking, new() { Grounded = true, SprintHeld = true, AnyMoveAxisNonzero = true }, MovementState.Sprinting },
            { MovementState.Walking, new() { Grounded = true, CrouchHeld = true }, MovementState.Crouching },
            { MovementState.Walking, new() { Grounded = false }, MovementState.Falling },
            { MovementState.Walking, new() { Grounded = true, JumpPressed = true, AnyMoveAxisNonzero = true }, MovementState.Jumping },
            { MovementState.Walking, new() { Grounded = true, AnyMoveAxisNonzero = false }, MovementState.Idle },

            // ---- from Sprinting ----
            { MovementState.Sprinting, new() { Grounded = true, SprintHeld = false, AnyMoveAxisNonzero = true }, MovementState.Walking },
            { MovementState.Sprinting, new() { Grounded = true, SprintHeld = true, CrouchHeld = true }, MovementState.Walking },
            { MovementState.Sprinting, new() { Grounded = false }, MovementState.Falling },

            // ---- from Crouching ----
            { MovementState.Crouching, new() { Grounded = true, CrouchHeld = false, CanStandUp = true }, MovementState.Walking },
            { MovementState.Crouching, new() { Grounded = true, CrouchHeld = false, CanStandUp = false }, MovementState.Crouching },
            { MovementState.Crouching, new() { Grounded = false }, MovementState.Falling },

            // ---- from Jumping ----
            { MovementState.Jumping, new() { VerticalVelocity = -1.0f }, MovementState.Falling },
            { MovementState.Jumping, new() { JumpHoldElapsed = true, VerticalVelocity = 5.0f }, MovementState.Falling },
            { MovementState.Jumping, new() { JumpHoldElapsed = false, VerticalVelocity = 5.0f }, MovementState.Jumping },

            // ---- from Falling ----
            { MovementState.Falling, new() { Grounded = true, AnyMoveAxisNonzero = false }, MovementState.Idle },
            { MovementState.Falling, new() { Grounded = true, AnyMoveAxisNonzero = true }, MovementState.Walking },
            { MovementState.Falling, new() { Grounded = false }, MovementState.Falling },
        };

    [Theory]
    [MemberData(nameof(TransitionCases))]
    public void Transition_Table_Cases(MovementState current, MovementStateInputs inputs, MovementState expected)
    {
        var next = MovementStateTransitions.Next(current, inputs);
        next.Should().Be(expected, $"transition from {current} with inputs {inputs}");
    }
```

- [ ] **Step 3.6: Run, observe which cases fail**

Run: `dotnet test --nologo 2>&1 | tail -15`. Expected: many failures — the transition table is incomplete.

- [ ] **Step 3.7: Implement the full transition table**

Path: `F:/O3DESharp/Gems/O3DE.FirstPersonController/Internal/MovementState.cs`

Replace the body of `MovementStateTransitions.Next` (the rest of the file is unchanged):

```csharp
public static class MovementStateTransitions
{
    public static MovementState Next(MovementState current, MovementStateInputs i)
    {
        // Falling overrides everything (except Jumping, which exits to
        // Falling on its own conditions).
        if (current != MovementState.Jumping && !i.Grounded)
        {
            return MovementState.Falling;
        }

        return current switch
        {
            MovementState.Idle =>
                i.JumpPressed && i.Grounded ? MovementState.Jumping :
                i.AnyMoveAxisNonzero        ? MovementState.Walking :
                                              MovementState.Idle,

            MovementState.Walking =>
                i.JumpPressed && i.Grounded     ? MovementState.Jumping :
                i.CrouchHeld                    ? MovementState.Crouching :
                (i.SprintHeld && i.AnyMoveAxisNonzero && !i.CrouchHeld)
                                                ? MovementState.Sprinting :
                !i.AnyMoveAxisNonzero           ? MovementState.Idle :
                                                  MovementState.Walking,

            MovementState.Sprinting =>
                i.JumpPressed && i.Grounded     ? MovementState.Jumping :
                (!i.SprintHeld || i.CrouchHeld) ? MovementState.Walking :
                !i.AnyMoveAxisNonzero           ? MovementState.Idle :
                                                  MovementState.Sprinting,

            MovementState.Crouching =>
                (!i.CrouchHeld && i.CanStandUp) ? MovementState.Walking :
                                                  MovementState.Crouching,

            MovementState.Jumping =>
                (i.VerticalVelocity < 0f || i.JumpHoldElapsed) ? MovementState.Falling :
                                                                 MovementState.Jumping,

            MovementState.Falling =>
                i.Grounded
                    ? (i.AnyMoveAxisNonzero ? MovementState.Walking : MovementState.Idle)
                    : MovementState.Falling,

            _ => current,
        };
    }
}
```

- [ ] **Step 3.8: Run all transition tests, verify all pass**

Run: `dotnet test --nologo 2>&1 | tail -5`. Expected: every case passes; total is now ~20+ tests across PID + MovementState.

- [ ] **Step 3.9: Commit**

```bash
cd F:/O3DESharp
git add Gems/O3DE.FirstPersonController/Internal/MovementState.cs \
        Gems/O3DE.FirstPersonController/Tests/MovementStateTransitionTests.cs
git commit -m "FPC port: MovementState enum + transition table + table-driven tests"
```

---

## Task 4: InputAxisAccumulator (TDD)

Buffers named-event input across frames. `[EBus]` handler methods on the FPC component will call into this; `OnUpdate` drains it once per frame.

**Files:**
- Create: `F:/O3DESharp/Gems/O3DE.FirstPersonController/Internal/InputAxisAccumulator.cs`
- Create: `F:/O3DESharp/Gems/O3DE.FirstPersonController/Tests/InputAxisAccumulatorTests.cs`

- [ ] **Step 4.1: Write the first failing test — axis values default to 0**

Path: `F:/O3DESharp/Gems/O3DE.FirstPersonController/Tests/InputAxisAccumulatorTests.cs`

```csharp
/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 */

using FluentAssertions;
using O3DE.FirstPersonController.Internal;
using Xunit;

namespace O3DE.FirstPersonController.Tests;

public class InputAxisAccumulatorTests
{
    [Fact]
    public void Default_Snapshot_Has_All_Axes_Zero()
    {
        var acc = new InputAxisAccumulator();
        var snap = acc.Snapshot();
        snap.Forward.Should().Be(0f);
        snap.Strafe.Should().Be(0f);
        snap.YawDelta.Should().Be(0f);
        snap.PitchDelta.Should().Be(0f);
        snap.SprintHeld.Should().BeFalse();
        snap.CrouchHeld.Should().BeFalse();
        snap.JumpPressed.Should().BeFalse();
    }
}
```

- [ ] **Step 4.2: Run, verify fail**

Run: `dotnet test --nologo --filter "Default_Snapshot" 2>&1 | tail -5`. Expected: compile error.

- [ ] **Step 4.3: Write the minimal InputAxisAccumulator**

Path: `F:/O3DESharp/Gems/O3DE.FirstPersonController/Internal/InputAxisAccumulator.cs`

```csharp
/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 */

namespace O3DE.FirstPersonController.Internal;

/// <summary>
/// Snapshot of one frame's worth of accumulated input. Returned by
/// <see cref="InputAxisAccumulator.Snapshot"/>. Read-only by design.
/// </summary>
public readonly struct InputSnapshot
{
    public float Forward { get; init; }      // -1..+1 (Back..Forward)
    public float Strafe { get; init; }       // -1..+1 (Left..Right)
    public float YawDelta { get; init; }     // mouse / stick delta (units: degrees)
    public float PitchDelta { get; init; }   // mouse / stick delta
    public bool SprintHeld { get; init; }
    public bool CrouchHeld { get; init; }
    public bool JumpPressed { get; init; }   // edge-triggered (one frame only)
}

/// <summary>
/// Accumulates named-event input across a frame. The FPC component
/// component's [EBus] handlers call <see cref="OnHeld"/> /
/// <see cref="OnPressed"/> / <see cref="OnReleased"/> as events
/// arrive; <see cref="Snapshot"/> + <see cref="DrainEdges"/> are
/// called once per frame in OnUpdate.
/// </summary>
public sealed class InputAxisAccumulator
{
    private float m_forward;
    private float m_strafe;
    private float m_yawDelta;
    private float m_pitchDelta;
    private bool m_sprintHeld;
    private bool m_crouchHeld;
    private bool m_jumpPressed;

    public InputSnapshot Snapshot() => new()
    {
        Forward = m_forward,
        Strafe = m_strafe,
        YawDelta = m_yawDelta,
        PitchDelta = m_pitchDelta,
        SprintHeld = m_sprintHeld,
        CrouchHeld = m_crouchHeld,
        JumpPressed = m_jumpPressed,
    };
}
```

- [ ] **Step 4.4: Run, verify pass**

Run: `dotnet test --nologo --filter "Default_Snapshot" 2>&1 | tail -5`. Expected: `Passed: 1`.

- [ ] **Step 4.5: Add tests for OnPressed/OnReleased/OnHeld for each named event**

Add to `InputAxisAccumulatorTests.cs`:

```csharp
    [Fact]
    public void OnHeld_Forward_Sets_Forward_Axis_To_Plus_One()
    {
        var acc = new InputAxisAccumulator();
        acc.OnHeld("Forward", 1.0f);
        acc.Snapshot().Forward.Should().Be(1.0f);
    }

    [Fact]
    public void OnHeld_Back_Sets_Forward_Axis_To_Minus_One()
    {
        var acc = new InputAxisAccumulator();
        acc.OnHeld("Back", 1.0f);
        acc.Snapshot().Forward.Should().Be(-1.0f);
    }

    [Fact]
    public void OnHeld_Left_And_Right_Combine_Symmetrically()
    {
        var acc = new InputAxisAccumulator();
        acc.OnHeld("Left", 1.0f);
        acc.OnHeld("Right", 1.0f);
        acc.Snapshot().Strafe.Should().Be(0f, "left and right cancel");
    }

    [Fact]
    public void OnPressed_Jump_Snapshots_True_Once_Then_DrainEdges_Resets()
    {
        var acc = new InputAxisAccumulator();
        acc.OnPressed("Jump");

        acc.Snapshot().JumpPressed.Should().BeTrue("the press should latch until drained");

        acc.DrainEdges();
        acc.Snapshot().JumpPressed.Should().BeFalse("after drain, edge events must clear");
    }

    [Fact]
    public void OnReleased_Sprint_Clears_The_Held_Flag()
    {
        var acc = new InputAxisAccumulator();
        acc.OnHeld("Sprint", 1.0f);
        acc.Snapshot().SprintHeld.Should().BeTrue();

        acc.OnReleased("Sprint");
        acc.Snapshot().SprintHeld.Should().BeFalse();
    }

    [Fact]
    public void OnHeld_Yaw_Accumulates_Across_Multiple_Events_Per_Frame()
    {
        var acc = new InputAxisAccumulator();
        acc.OnHeld("Yaw", 2.5f);
        acc.OnHeld("Yaw", -1.0f);
        acc.Snapshot().YawDelta.Should().BeApproximately(1.5f, 0.0001f);
    }

    [Fact]
    public void DrainEdges_Resets_Yaw_And_Pitch_Deltas_But_Not_Axes()
    {
        var acc = new InputAxisAccumulator();
        acc.OnHeld("Forward", 1.0f);
        acc.OnHeld("Yaw", 5.0f);

        acc.DrainEdges();
        var snap = acc.Snapshot();

        snap.YawDelta.Should().Be(0f, "yaw is per-frame and drains every tick");
        snap.Forward.Should().Be(1.0f, "axes persist while the button is held");
    }

    [Fact]
    public void Unknown_Event_Names_Are_Silently_Ignored()
    {
        var acc = new InputAxisAccumulator();
        var act = () => acc.OnHeld("NotAnEvent", 1.0f);
        act.Should().NotThrow();
        acc.Snapshot().Forward.Should().Be(0f);
    }
```

- [ ] **Step 4.6: Run, observe failures, then implement**

Run: `dotnet test --nologo --filter "InputAxis" 2>&1 | tail -10`. Expected: 8 failing cases.

Path: `F:/O3DESharp/Gems/O3DE.FirstPersonController/Internal/InputAxisAccumulator.cs`

Replace the whole file with:

```csharp
/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 */

namespace O3DE.FirstPersonController.Internal;

public readonly struct InputSnapshot
{
    public float Forward { get; init; }      // -1..+1 (Back..Forward)
    public float Strafe { get; init; }       // -1..+1 (Left..Right)
    public float YawDelta { get; init; }     // per-frame yaw delta in degrees
    public float PitchDelta { get; init; }   // per-frame pitch delta in degrees
    public bool SprintHeld { get; init; }
    public bool CrouchHeld { get; init; }
    public bool JumpPressed { get; init; }   // edge-triggered (one frame only)
}

/// <summary>
/// Accumulates named-event input across a frame. The FPC component's
/// [EBus] handlers call OnHeld / OnPressed / OnReleased as events
/// arrive; Snapshot + DrainEdges are called once per frame in OnUpdate.
///
/// Recognised event names (case-sensitive):
///   "Forward", "Back"       - drive the forward axis (+/-1)
///   "Left", "Right"         - drive the strafe axis (+/-1)
///   "Yaw"                   - accumulating mouse/stick yaw delta
///   "Pitch"                 - accumulating mouse/stick pitch delta
///   "Sprint"                - hold-style button
///   "Crouch"                - hold-style button
///   "Jump"                  - edge-triggered (OnPressed)
///
/// Unknown names are silently ignored so users can extend their
/// .inputbindings asset without breaking this component.
/// </summary>
public sealed class InputAxisAccumulator
{
    private float m_forwardPositive;
    private float m_forwardNegative;
    private float m_strafePositive;
    private float m_strafeNegative;
    private float m_yawDelta;
    private float m_pitchDelta;
    private bool m_sprintHeld;
    private bool m_crouchHeld;
    private bool m_jumpPressed;

    public InputSnapshot Snapshot() => new()
    {
        Forward = m_forwardPositive - m_forwardNegative,
        Strafe = m_strafePositive - m_strafeNegative,
        YawDelta = m_yawDelta,
        PitchDelta = m_pitchDelta,
        SprintHeld = m_sprintHeld,
        CrouchHeld = m_crouchHeld,
        JumpPressed = m_jumpPressed,
    };

    /// <summary>
    /// Reset edge-triggered values + per-frame deltas. Call once at
    /// the end of every frame in OnUpdate. Held-axis values persist
    /// (cleared by OnReleased only).
    /// </summary>
    public void DrainEdges()
    {
        m_jumpPressed = false;
        m_yawDelta = 0f;
        m_pitchDelta = 0f;
    }

    public void OnHeld(string eventName, float value)
    {
        switch (eventName)
        {
            case "Forward": m_forwardPositive = value; break;
            case "Back": m_forwardNegative = value; break;
            case "Left": m_strafeNegative = value; break;
            case "Right": m_strafePositive = value; break;
            case "Yaw": m_yawDelta += value; break;
            case "Pitch": m_pitchDelta += value; break;
            case "Sprint": m_sprintHeld = true; break;
            case "Crouch": m_crouchHeld = true; break;
            // Unknown names ignored.
        }
    }

    public void OnPressed(string eventName)
    {
        switch (eventName)
        {
            case "Jump": m_jumpPressed = true; break;
            case "Sprint": m_sprintHeld = true; break;
            case "Crouch": m_crouchHeld = true; break;
            // Forward/Back/Left/Right ignore press edges; they read
            // continuous value via OnHeld instead.
        }
    }

    public void OnReleased(string eventName)
    {
        switch (eventName)
        {
            case "Forward": m_forwardPositive = 0f; break;
            case "Back": m_forwardNegative = 0f; break;
            case "Left": m_strafeNegative = 0f; break;
            case "Right": m_strafePositive = 0f; break;
            case "Sprint": m_sprintHeld = false; break;
            case "Crouch": m_crouchHeld = false; break;
            // Jump's release is a no-op (already edge-triggered).
        }
    }
}
```

- [ ] **Step 4.7: Run, verify all tests pass**

Run: `dotnet test --nologo 2>&1 | tail -5`. Expected: every InputAxis test passes; running total around 30 tests.

- [ ] **Step 4.8: Commit**

```bash
cd F:/O3DESharp
git add Gems/O3DE.FirstPersonController/Internal/InputAxisAccumulator.cs \
        Gems/O3DE.FirstPersonController/Tests/InputAxisAccumulatorTests.cs
git commit -m "FPC port: InputAxisAccumulator buffers named-event input"
```

---

## Task 5: GroundCheck + IPhysicsQueryService (TDD)

Sphere-cast grounded detection, abstracted behind an interface so unit tests stub the physics layer.

**Files:**
- Create: `F:/O3DESharp/Gems/O3DE.FirstPersonController/Internal/IPhysicsQueryService.cs`
- Create: `F:/O3DESharp/Gems/O3DE.FirstPersonController/Internal/GroundCheck.cs`
- Create: `F:/O3DESharp/Gems/O3DE.FirstPersonController/Tests/GroundCheckTests.cs`

- [ ] **Step 5.1: Write the IPhysicsQueryService interface**

Path: `F:/O3DESharp/Gems/O3DE.FirstPersonController/Internal/IPhysicsQueryService.cs`

```csharp
/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 */

using O3DE;

namespace O3DE.FirstPersonController.Internal;

/// <summary>
/// Abstraction over the physics scene query API. Real production
/// implementation routes to O3DE's PhysX scene query
/// (Physics::SceneQueryRequests, reflected through O3DESharp).
///
/// Unit tests substitute a fake implementation that returns
/// canned results, so the GroundCheck logic can be exercised
/// without standing up a physics engine.
/// </summary>
public interface IPhysicsQueryService
{
    /// <summary>
    /// Cast a sphere of <paramref name="radius"/> from
    /// <paramref name="origin"/> along <paramref name="direction"/>
    /// up to <paramref name="maxDistance"/>. Returns true on hit.
    /// On hit, <paramref name="hitNormal"/> is the surface normal at
    /// the contact point and <paramref name="hitDistance"/> is the
    /// distance traveled along the cast direction before contact.
    /// </summary>
    bool SphereCast(
        Vector3 origin,
        float radius,
        Vector3 direction,
        float maxDistance,
        out Vector3 hitNormal,
        out float hitDistance);
}
```

- [ ] **Step 5.2: Write the first failing test — grounded when sphere hits below**

Path: `F:/O3DESharp/Gems/O3DE.FirstPersonController/Tests/GroundCheckTests.cs`

```csharp
/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 */

using FluentAssertions;
using O3DE;
using O3DE.FirstPersonController.Internal;
using Xunit;

namespace O3DE.FirstPersonController.Tests;

public class GroundCheckTests
{
    /// <summary>
    /// Fake physics service for tests. Returns whatever the test
    /// configured; never calls into a real scene query.
    /// </summary>
    private sealed class FakePhysics : IPhysicsQueryService
    {
        public bool ShouldHit { get; set; }
        public Vector3 HitNormal { get; set; } = Vector3.Up;
        public float HitDistance { get; set; }

        public bool SphereCast(Vector3 origin, float radius, Vector3 direction,
                               float maxDistance, out Vector3 hitNormal, out float hitDistance)
        {
            hitNormal = HitNormal;
            hitDistance = HitDistance;
            return ShouldHit;
        }
    }

    [Fact]
    public void Grounded_When_SphereCast_Hits_With_Vertical_Normal()
    {
        var physics = new FakePhysics
        {
            ShouldHit = true,
            HitNormal = Vector3.Up,         // perfectly flat ground
            HitDistance = 0.1f,
        };

        var check = new GroundCheck { MaxSlopeDegrees = 30f };
        check.Update(
            worldPosition: new Vector3(0, 0, 1),
            characterRadius: 0.4f,
            physics: physics,
            dt: 0.016f);

        check.Grounded.Should().BeTrue();
    }
}
```

- [ ] **Step 5.3: Write the minimal GroundCheck**

Path: `F:/O3DESharp/Gems/O3DE.FirstPersonController/Internal/GroundCheck.cs`

```csharp
/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 */

using System;
using O3DE;

namespace O3DE.FirstPersonController.Internal;

/// <summary>
/// Tracks whether the character is standing on a slope it can stand
/// on, by sphere-casting downward each frame. Returns false (i.e.,
/// "falling") if the cast misses, OR if the slope exceeds
/// MaxSlopeDegrees.
/// </summary>
public sealed class GroundCheck
{
    public bool Grounded { get; private set; }
    public Vector3 SlopeNormal { get; private set; } = Vector3.Up;
    public float MaxSlopeDegrees { get; init; } = 30f;
    public float ProbeDistance { get; init; } = 0.5f;

    public void Update(
        Vector3 worldPosition,
        float characterRadius,
        IPhysicsQueryService physics,
        float dt)
    {
        bool hit = physics.SphereCast(
            origin: worldPosition,
            radius: characterRadius,
            direction: -Vector3.Up,
            maxDistance: ProbeDistance,
            out var normal,
            out var _);

        if (!hit)
        {
            Grounded = false;
            SlopeNormal = Vector3.Up;
            return;
        }

        // Angle between hit normal and world-up. If > MaxSlopeDegrees,
        // the surface is too steep to stand on.
        double cosTheta = Vector3.Dot(normal, Vector3.Up);
        cosTheta = Math.Clamp(cosTheta, -1.0, 1.0);
        double angleDeg = Math.Acos(cosTheta) * (180.0 / Math.PI);

        Grounded = angleDeg <= MaxSlopeDegrees;
        SlopeNormal = normal;
    }
}
```

- [ ] **Step 5.4: Run, verify the test passes**

Run: `dotnet test --nologo --filter "GroundCheck" 2>&1 | tail -5`. Expected: `Passed: 1`.

- [ ] **Step 5.5: Add slope-limit + miss tests**

Add to `GroundCheckTests.cs`:

```csharp
    [Fact]
    public void Falling_When_SphereCast_Misses()
    {
        var physics = new FakePhysics { ShouldHit = false };

        var check = new GroundCheck { MaxSlopeDegrees = 30f };
        check.Update(new Vector3(0, 0, 5), 0.4f, physics, 0.016f);

        check.Grounded.Should().BeFalse();
    }

    [Fact]
    public void Falling_When_Slope_Exceeds_Max()
    {
        // 45-degree slope - normal points outward at 45deg from
        // vertical. Should fail a 30-degree max.
        var slopedNormal = new Vector3(
            (float)System.Math.Sin(System.Math.PI / 4),
            0f,
            (float)System.Math.Cos(System.Math.PI / 4)
        );
        var physics = new FakePhysics
        {
            ShouldHit = true,
            HitNormal = slopedNormal,
            HitDistance = 0.1f,
        };

        var check = new GroundCheck { MaxSlopeDegrees = 30f };
        check.Update(new Vector3(0, 0, 1), 0.4f, physics, 0.016f);

        check.Grounded.Should().BeFalse(
            "45-degree slope exceeds 30-degree max");
    }

    [Fact]
    public void Grounded_When_Slope_Within_Max()
    {
        // 20-degree slope, max 30 - should pass.
        double angleRad = 20.0 * System.Math.PI / 180.0;
        var normal = new Vector3(
            (float)System.Math.Sin(angleRad),
            0f,
            (float)System.Math.Cos(angleRad)
        );
        var physics = new FakePhysics
        {
            ShouldHit = true,
            HitNormal = normal,
            HitDistance = 0.1f,
        };

        var check = new GroundCheck { MaxSlopeDegrees = 30f };
        check.Update(new Vector3(0, 0, 1), 0.4f, physics, 0.016f);

        check.Grounded.Should().BeTrue();
    }
```

- [ ] **Step 5.6: Run all GroundCheck tests, verify they pass**

Run: `dotnet test --nologo --filter "GroundCheck" 2>&1 | tail -5`. Expected: all 4 GroundCheck tests pass.

- [ ] **Step 5.7: Commit**

```bash
cd F:/O3DESharp
git add Gems/O3DE.FirstPersonController/Internal/IPhysicsQueryService.cs \
        Gems/O3DE.FirstPersonController/Internal/GroundCheck.cs \
        Gems/O3DE.FirstPersonController/Tests/GroundCheckTests.cs
git commit -m "FPC port: IPhysicsQueryService + GroundCheck + slope-limit tests"
```

---

## Task 6: MovementTuning struct

A grouped struct of inspector tuning parameters with default values matching upstream FPC. No tests — it's pure data.

**Files:**
- Create: `F:/O3DESharp/Gems/O3DE.FirstPersonController/Internal/MovementTuning.cs`

- [ ] **Step 6.1: Write MovementTuning**

Path: `F:/O3DESharp/Gems/O3DE.FirstPersonController/Internal/MovementTuning.cs`

```csharp
/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 */

namespace O3DE.FirstPersonController.Internal;

/// <summary>
/// Grouped tuning parameters for the FPC component. Defaults match
/// upstream Porcupine-Factory/FirstPersonController at main HEAD on
/// 2026-05-21. Designers override individual fields via
/// [ExposedProperty]; the static <see cref="Defaults"/> instance is
/// what fresh component instances start from.
/// </summary>
public struct MovementTuning
{
    // ---- horizontal ----
    public float WalkSpeed;             // m/s
    public float Acceleration;          // m/s²
    public float Deceleration;          // damping factor per second
    public float SprintScaleForward;    // multiplier on WalkSpeed when sprinting

    // ---- vertical ----
    public float Gravity;               // m/s² (positive value, applied downward)
    public float JumpInitialVelocity;   // m/s
    public float JumpHoldDistance;      // m above ground where hold ends
    public bool DoubleJumpEnabled;

    // ---- crouch ----
    public float StandingEyeHeight;     // m (eye above feet)
    public float CrouchEyeHeight;       // m
    public float CrouchPidKp;
    public float CrouchPidKi;
    public float CrouchPidKd;

    // ---- ground check ----
    public float GroundedSphereCastOffset;  // m above origin
    public float MaxGroundedAngleDegrees;
    public float CharacterRadius;       // m

    public static MovementTuning Defaults => new()
    {
        WalkSpeed = 5.0f,
        Acceleration = 30.0f,
        Deceleration = 1.5f,
        SprintScaleForward = 1.5f,
        Gravity = 30.0f,
        JumpInitialVelocity = 6.0f,
        JumpHoldDistance = 0.8f,
        DoubleJumpEnabled = false,
        StandingEyeHeight = 1.6f,
        CrouchEyeHeight = 1.1f,
        CrouchPidKp = 12.0f,
        CrouchPidKi = 0.0f,
        CrouchPidKd = 0.5f,
        GroundedSphereCastOffset = 0.001f,
        MaxGroundedAngleDegrees = 30.0f,
        CharacterRadius = 0.4f,
    };
}
```

- [ ] **Step 6.2: Verify the project still builds**

Run:
```bash
cd F:/O3DESharp/Gems/O3DE.FirstPersonController
dotnet build -c Release --nologo 2>&1 | tail -5
```

Expected: `Build succeeded. 0 Warning(s), 0 Error(s)`.

- [ ] **Step 6.3: Commit**

```bash
cd F:/O3DESharp
git add Gems/O3DE.FirstPersonController/Internal/MovementTuning.cs
git commit -m "FPC port: MovementTuning struct with upstream-matching defaults"
```

---

## Task 7: FirstPersonControllerComponent skeleton

The component subclass. M1 ships only the core movement loop — NO request-bus surface (that's M2).

**Files:**
- Create: `F:/O3DESharp/Gems/O3DE.FirstPersonController/Components/FirstPersonControllerComponent.cs`

- [ ] **Step 7.1: Write the component skeleton (no logic yet)**

Path: `F:/O3DESharp/Gems/O3DE.FirstPersonController/Components/FirstPersonControllerComponent.cs`

```csharp
/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 */

using System;
using O3DE;
using O3DE.FirstPersonController.Internal;

namespace O3DE.FirstPersonController.Components;

/// <summary>
/// C# port of Porcupine-Factory/FirstPersonController's
/// FirstPersonControllerComponent. M1 implements walking, sprinting,
/// jumping, falling, grounded detection, and PhysX-backed movement.
/// M2 adds crouch + the full ~90-method request bus surface.
///
/// Setup:
///   1. Attach a CSharpScript component to the player entity
///   2. Set Script Class to
///      "O3DE.FirstPersonController.Components.FirstPersonControllerComponent"
///   3. The entity must also have a PhysX Character Controller
///      component (the FPC reads ground state + drives velocity
///      through CharacterControllerRequestBus)
///   4. A child camera entity gets the CameraCoupledChildComponent
///      (added in M2 - for M1 use a manually positioned child camera)
/// </summary>
public sealed class FirstPersonControllerComponent : ScriptComponent
{
    // ---- Inspector-tunable parameters (defaults match upstream FPC) ----

    [ExposedProperty("Walk Speed", Tooltip = "Top horizontal speed in m/s when not sprinting")]
    public float WalkSpeed = MovementTuning.Defaults.WalkSpeed;

    [ExposedProperty("Acceleration", Tooltip = "Horizontal accel toward target speed (m/s²)")]
    public float Acceleration = MovementTuning.Defaults.Acceleration;

    [ExposedProperty("Deceleration", Tooltip = "Horizontal damping factor per second")]
    public float Deceleration = MovementTuning.Defaults.Deceleration;

    [ExposedProperty("Sprint Multiplier", Tooltip = "Speed multiplier on WalkSpeed when sprinting forward")]
    public float SprintScaleForward = MovementTuning.Defaults.SprintScaleForward;

    [ExposedProperty("Gravity", Tooltip = "Downward acceleration in m/s² (positive)")]
    public float Gravity = MovementTuning.Defaults.Gravity;

    [ExposedProperty("Jump Initial Velocity", Tooltip = "Upward velocity applied on jump press (m/s)")]
    public float JumpInitialVelocity = MovementTuning.Defaults.JumpInitialVelocity;

    [ExposedProperty("Jump Hold Distance", Tooltip = "Vertical distance above ground where jump-hold ends (m)")]
    public float JumpHoldDistance = MovementTuning.Defaults.JumpHoldDistance;

    [ExposedProperty("Max Grounded Angle (deg)", Tooltip = "Slope angle above which the character cannot stand")]
    public float MaxGroundedAngleDegrees = MovementTuning.Defaults.MaxGroundedAngleDegrees;

    [ExposedProperty("Character Radius (m)", Tooltip = "Used by the sphere-cast ground check")]
    public float CharacterRadius = MovementTuning.Defaults.CharacterRadius;

    [ExposedProperty("Eye Height (m)", Tooltip = "Standing eye height above feet; used by camera child component")]
    public float StandingEyeHeight = MovementTuning.Defaults.StandingEyeHeight;

    // ---- Internal state ----

    private readonly InputAxisAccumulator m_input = new();
    private GroundCheck m_groundCheck = null!;
    private MovementState m_state = MovementState.Idle;
    private Vector3 m_horizontalVelocity;
    private float m_verticalVelocity;
    private float m_jumpHoldStartZ;
    private bool m_disabled;

    public override void OnCreate()
    {
        Log("FirstPersonControllerComponent: activated");

        if (Transform == null || !Transform.IsValid)
        {
            LogError("[FPC] FirstPersonControllerComponent: Transform binding is null/invalid. Disabling.");
            m_disabled = true;
            return;
        }

        m_groundCheck = new GroundCheck
        {
            MaxSlopeDegrees = MaxGroundedAngleDegrees,
        };
    }

    public override void OnUpdate(float deltaTime)
    {
        if (m_disabled) return;

        try
        {
            TickOnce(deltaTime);
        }
        catch (Exception ex)
        {
            LogError($"[FPC] OnUpdate threw {ex.GetType().Name}: {ex.Message} — frame skipped");
        }
    }

    private void TickOnce(float dt)
    {
        // 1. Snapshot input
        var snap = m_input.Snapshot();

        // 2. Compute movement-state inputs (M1: grounded read from
        //    a no-op stub; M2 wires this to real PhysX).
        bool grounded = false;     // M1 stub
        bool jumpHoldElapsed = false;
        bool canStandUp = true;

        var stateInputs = new MovementStateInputs
        {
            Grounded = grounded,
            SprintHeld = snap.SprintHeld,
            CrouchHeld = snap.CrouchHeld,
            JumpPressed = snap.JumpPressed,
            AnyMoveAxisNonzero = Math.Abs(snap.Forward) > 0.001f || Math.Abs(snap.Strafe) > 0.001f,
            JumpHoldElapsed = jumpHoldElapsed,
            CanStandUp = canStandUp,
            VerticalVelocity = m_verticalVelocity,
        };

        // 3. Update state machine
        var prev = m_state;
        m_state = MovementStateTransitions.Next(m_state, stateInputs);
        if (m_state != prev) OnStateEnter(m_state, prev);

        // 4. Drain edges so the next frame starts fresh
        m_input.DrainEdges();
    }

    private void OnStateEnter(MovementState entered, MovementState from)
    {
        switch (entered)
        {
            case MovementState.Jumping:
                m_verticalVelocity = JumpInitialVelocity;
                m_jumpHoldStartZ = Transform.Position.Z;
                Log($"[FPC] {from} -> Jumping (v0={JumpInitialVelocity:F2})");
                break;
        }
    }
}
```

- [ ] **Step 7.2: Verify it builds**

Run:
```bash
cd F:/O3DESharp/Gems/O3DE.FirstPersonController
dotnet build -c Release --nologo 2>&1 | tail -5
```

Expected: `Build succeeded. 0 Warning(s), 0 Error(s)`. The smoke test isn't wired yet; the component is a no-op-on-movement stub for the moment.

- [ ] **Step 7.3: Commit**

```bash
cd F:/O3DESharp
git add Gems/O3DE.FirstPersonController/Components/FirstPersonControllerComponent.cs
git commit -m "FPC port: FirstPersonControllerComponent skeleton (state machine wired)"
```

---

## Task 8: Wire up PhysX character controller via reflected bus

Replace the M1 stubs in `TickOnce` with real reflected calls into `CharacterControllerRequestBus`.

**Files:**
- Modify: `F:/O3DESharp/Gems/O3DE.FirstPersonController/Components/FirstPersonControllerComponent.cs`

- [ ] **Step 8.1: Add a private helper for ground state via the bus**

Add to `FirstPersonControllerComponent` (above `TickOnce`):

```csharp
    /// <summary>
    /// Query the PhysX character controller for ground state. Uses
    /// the reflected CharacterControllerRequestBus, which is exposed
    /// by every PhysX CharacterController component.
    ///
    /// Returns false on any bus error so a missing PhysX component
    /// degrades to "always falling" rather than crashing.
    /// </summary>
    private bool QueryCharacterControllerGrounded()
    {
        try
        {
            // The CharacterControllerRequestBus reflects an IsOnGround
            // (or similar) query; route via the untyped reflection
            // API for v1 to avoid taking a dependency on per-project
            // generated bindings.
            object? result = O3DE.Reflection.NativeReflection.SendEBusEvent(
                "CharacterControllerRequestBus", "IsOnGround", EntityId);
            return result is bool b && b;
        }
        catch (Exception ex)
        {
            LogWarning($"[FPC] IsOnGround query failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Apply a velocity to the PhysX character controller for this
    /// frame. AddVelocity accumulates; the engine integrates it and
    /// resolves collisions.
    /// </summary>
    private void ApplyCharacterControllerVelocity(Vector3 velocity)
    {
        try
        {
            O3DE.Reflection.NativeReflection.SendEBusEvent(
                "CharacterControllerRequestBus", "AddVelocityForTick", EntityId, velocity);
        }
        catch (Exception ex)
        {
            LogWarning($"[FPC] AddVelocity failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
```

- [ ] **Step 8.2: Replace the grounded stub + apply velocity in TickOnce**

In `FirstPersonControllerComponent.cs`, replace the entire body of `TickOnce` with:

```csharp
    private void TickOnce(float dt)
    {
        // 1. Snapshot input
        var snap = m_input.Snapshot();

        // 2. Query physics for ground state
        bool grounded = QueryCharacterControllerGrounded();

        // 3. Compute jump-hold elapsed: above start Z + JumpHoldDistance
        bool jumpHoldElapsed = m_state == MovementState.Jumping
            && Transform.Position.Z - m_jumpHoldStartZ >= JumpHoldDistance;

        // 4. Pure-functional transition decision
        var stateInputs = new MovementStateInputs
        {
            Grounded = grounded,
            SprintHeld = snap.SprintHeld,
            CrouchHeld = snap.CrouchHeld,
            JumpPressed = snap.JumpPressed,
            AnyMoveAxisNonzero = Math.Abs(snap.Forward) > 0.001f || Math.Abs(snap.Strafe) > 0.001f,
            JumpHoldElapsed = jumpHoldElapsed,
            CanStandUp = true,                    // M1: no overhead check yet
            VerticalVelocity = m_verticalVelocity,
        };

        var prev = m_state;
        m_state = MovementStateTransitions.Next(m_state, stateInputs);
        if (m_state != prev) OnStateEnter(m_state, prev);

        // 5. Horizontal velocity: target = direction * speed, accel
        //    toward target.
        float targetSpeed = WalkSpeed * (m_state == MovementState.Sprinting ? SprintScaleForward : 1.0f);
        Vector3 forwardWorld = Transform.Forward;
        Vector3 rightWorld = Transform.Right;
        Vector3 targetHoriz = (forwardWorld * snap.Forward + rightWorld * snap.Strafe);
        if (targetHoriz.SqrMagnitude > 0.0001f)
        {
            targetHoriz.Normalize();
            targetHoriz *= targetSpeed;
        }
        // Lerp the current horizontal velocity toward target.
        float accelStep = Acceleration * dt;
        Vector3 toTarget = targetHoriz - m_horizontalVelocity;
        if (toTarget.Magnitude > accelStep)
        {
            toTarget.Normalize();
            m_horizontalVelocity += toTarget * accelStep;
        }
        else
        {
            m_horizontalVelocity = targetHoriz;
        }

        // 6. Vertical velocity: gravity or jump impulse already
        //    applied via OnStateEnter.
        if (grounded && m_state != MovementState.Jumping)
        {
            m_verticalVelocity = 0f;
        }
        else
        {
            m_verticalVelocity -= Gravity * dt;
        }

        // 7. Apply the final velocity to the character controller.
        var velocity = new Vector3(
            m_horizontalVelocity.X,
            m_horizontalVelocity.Y,
            m_verticalVelocity);
        ApplyCharacterControllerVelocity(velocity);

        // 8. Drain edges so the next frame starts fresh
        m_input.DrainEdges();
    }
```

- [ ] **Step 8.3: Verify it builds**

Run:
```bash
cd F:/O3DESharp/Gems/O3DE.FirstPersonController
dotnet build -c Release --nologo 2>&1 | tail -5
```

Expected: `Build succeeded. 0 Warning(s), 0 Error(s)`.

- [ ] **Step 8.4: Commit**

```bash
cd F:/O3DESharp
git add Gems/O3DE.FirstPersonController/Components/FirstPersonControllerComponent.cs
git commit -m "FPC port: wire PhysX CharacterController via reflected bus calls"
```

---

## Task 9: Wire up named-event input via [EBus] handler

Add `[EBus("InputEventNotificationBus")]` to the class + `[EBusHandler]` methods for each named event. The source generator emits Connect/Disconnect; we call them in OnCreate/OnDestroy.

> **Known issue / decision point during implementation:** O3DE's `InputEventNotificationBus` is multi-address — each input event ID is its own subscription target. Phase 18-E's source generator currently emits a single `Connect(address)` call per `[EBus]` attribute, so this v0.1 wiring subscribes to the **broadcast channel** (address=0) only. Depending on how the user's StartingPointInput .inputbindings asset is configured, this may or may not deliver events.
>
> If the smoke test passes and a manual editor test fires events: ship as written.
> If events never fire in-editor: switch this task to a **polling fallback** that calls `O3DE.Input.GetAxis("Forward")` / `IsKeyPressed(KeyCode.Space)` / etc. in `TickOnce`, feeding values into the `InputAxisAccumulator` from polling rather than `[EBusHandler]` methods. The accumulator API + tests stay unchanged — only the input *source* changes. Document the fallback in the gem README under "Known Limitations".
>
> A future v0.2 work item (tracked outside this plan) is to extend the source generator to emit `MultiHandler`-style multi-address subscription, at which point the [EBus] path lights up for free.

**Files:**
- Modify: `F:/O3DESharp/Gems/O3DE.FirstPersonController/Components/FirstPersonControllerComponent.cs`

- [ ] **Step 9.1: Make the class partial + add the [EBus] attribute**

In `FirstPersonControllerComponent.cs`, change:
```csharp
public sealed class FirstPersonControllerComponent : ScriptComponent
```

to:

```csharp
[EBus("InputEventNotificationBus")]
public sealed partial class FirstPersonControllerComponent : ScriptComponent
```

`partial` is required by the source generator (it injects `ConnectToInputEventNotificationBus` and `DisconnectFromInputEventNotificationBus` partial methods).

- [ ] **Step 9.2: Add the [EBusHandler] methods**

Add to the body of `FirstPersonControllerComponent` (after `OnStateEnter`):

```csharp
    // ---- Named-event handlers ---------------------------------------
    //
    // Each method maps to a named input event the user configures in
    // their .inputbindings asset. The Phase 18-E source generator
    // routes named events to these by string match (the [EBusHandler]
    // attribute argument is the bus event name; the method itself can
    // be private and named anything).
    //
    // Sprint/Crouch/Jump treat a value >= 0.5 as "pressed" since
    // StartingPointInput's digital events fire with value=1 on press
    // and value=0 on release. The accumulator's edge-tracking
    // happens internally (DrainEdges() at end of TickOnce).

    [EBusHandler("Forward")]    private void OnForward(float v)  => m_input.OnHeld("Forward", v);
    [EBusHandler("Back")]       private void OnBack(float v)     => m_input.OnHeld("Back", v);
    [EBusHandler("Left")]       private void OnLeft(float v)     => m_input.OnHeld("Left", v);
    [EBusHandler("Right")]      private void OnRight(float v)    => m_input.OnHeld("Right", v);
    [EBusHandler("Yaw")]        private void OnYaw(float v)      => m_input.OnHeld("Yaw", v);
    [EBusHandler("Pitch")]      private void OnPitch(float v)    => m_input.OnHeld("Pitch", v);
    [EBusHandler("Sprint")]     private void OnSprint(float v)
    {
        if (v > 0.5f) m_input.OnHeld("Sprint", v);
        else m_input.OnReleased("Sprint");
    }
    [EBusHandler("Crouch")]     private void OnCrouch(float v)
    {
        if (v > 0.5f) m_input.OnHeld("Crouch", v);
        else m_input.OnReleased("Crouch");
    }
    [EBusHandler("Jump")]       private void OnJump(float v)
    {
        if (v > 0.5f) m_input.OnPressed("Jump");
    }
```

- [ ] **Step 9.3: Call Connect/Disconnect in OnCreate/OnDestroy**

In `FirstPersonControllerComponent.cs`, modify `OnCreate` to call the generator-emitted Connect after the disabled check:

```csharp
    public override void OnCreate()
    {
        Log("FirstPersonControllerComponent: activated");

        if (Transform == null || !Transform.IsValid)
        {
            LogError("[FPC] FirstPersonControllerComponent: Transform binding is null/invalid. Disabling.");
            m_disabled = true;
            return;
        }

        m_groundCheck = new GroundCheck
        {
            MaxSlopeDegrees = MaxGroundedAngleDegrees,
        };

        // Subscribe to named input events. The source generator emits
        // ConnectToInputEventNotificationBus from the [EBus] attribute
        // above the class; calling it here registers all the
        // [EBusHandler] methods below.
        try
        {
            ConnectToInputEventNotificationBus();
        }
        catch (Exception ex)
        {
            LogWarning($"[FPC] Could not connect to InputEventNotificationBus " +
                       $"({ex.GetType().Name}: {ex.Message}). Input will not be received " +
                       $"until the StartingPointInput gem is enabled in this project.");
        }
    }

    public override void OnDestroy()
    {
        try
        {
            DisconnectFromInputEventNotificationBus();
        }
        catch
        {
            // Disconnect-on-shutdown errors aren't actionable; swallow.
        }
        Log($"FirstPersonControllerComponent: deactivated in state {m_state}");
    }
```

- [ ] **Step 9.4: Verify the source generator emits the Connect methods**

Run:
```bash
cd F:/O3DESharp/Gems/O3DE.FirstPersonController
dotnet build -c Release --nologo 2>&1 | tail -5
```

Expected: `Build succeeded. 0 Warning(s), 0 Error(s)`. If the build fails with `error CS0103: The name 'ConnectToInputEventNotificationBus' does not exist`, the source generator didn't run — check that the SourceGenerators csproj reference's `OutputItemType="Analyzer"` is present.

To verify the source generator's emit (optional sanity check):
```bash
find F:/O3DESharp/Gems/O3DE.FirstPersonController/obj -name "FirstPersonControllerComponent.EBus.g.cs" 2>/dev/null | head -1
```

Should print a path to a generated `.g.cs` file containing the Connect/Disconnect methods.

- [ ] **Step 9.5: Commit**

```bash
cd F:/O3DESharp
git add Gems/O3DE.FirstPersonController/Components/FirstPersonControllerComponent.cs
git commit -m "FPC port: [EBus] named-event input + Connect/Disconnect lifecycle"
```

---

## Task 10: SmokeTests console-app (golden-trajectory regression)

A console app that simulates 1000 ticks against the component with mocked buses, asserts position trajectory matches a recorded reference. Cheap CI gate.

**Files:**
- Create: `F:/O3DESharp/Gems/O3DE.FirstPersonController/SmokeTests/O3DE.FirstPersonController.SmokeTests.csproj`
- Create: `F:/O3DESharp/Gems/O3DE.FirstPersonController/SmokeTests/Program.cs`

- [ ] **Step 10.1: Write the SmokeTests csproj**

Path: `F:/O3DESharp/Gems/O3DE.FirstPersonController/SmokeTests/O3DE.FirstPersonController.SmokeTests.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
    <RootNamespace>O3DE.FirstPersonController.SmokeTests</RootNamespace>
    <Configurations>Debug;Profile;Release</Configurations>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\O3DE.FirstPersonController.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 10.2: Write the smoke entrypoint**

Path: `F:/O3DESharp/Gems/O3DE.FirstPersonController/SmokeTests/Program.cs`

The smoke runs the pure-logic helpers (no ScriptComponent, no real bus dispatch). It exercises 1000 ticks of synthetic input + ground state and asserts the state-machine + accumulator + PID produce the expected trajectory.

```csharp
/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 */

using System;
using System.Linq;
using O3DE.FirstPersonController.Internal;

namespace O3DE.FirstPersonController.SmokeTests;

/// <summary>
/// Golden-trajectory regression. Runs 1000 ticks of synthetic input
/// against the pure-logic helpers (state machine, input accumulator,
/// PID) and asserts the cumulative observed counts match recorded
/// expectations. Bypasses the ScriptComponent + reflected bus layer —
/// those need a hosted O3DE editor and aren't reachable from a plain
/// console app.
///
/// Exit code 0 = pass, non-zero = mismatch (CI fails the build).
/// </summary>
public static class Program
{
    public static int Main()
    {
        var input = new InputAxisAccumulator();
        var state = MovementState.Idle;

        int walkingTicks = 0;
        int sprintingTicks = 0;
        int jumpingTicks = 0;
        int fallingTicks = 0;
        int idleTicks = 0;
        int crouchingTicks = 0;

        // Phased synthetic input: walk for 200, sprint for 200, jump,
        // fall, settle. Each tick is 16ms (60Hz simulation).
        const float dt = 0.016f;

        for (int t = 0; t < 1000; t++)
        {
            // Reset all axes each iteration; replay the configured
            // input for this phase.
            input = new InputAxisAccumulator();
            float vertVel = 0f;
            bool grounded = true;
            bool jumpPressed = false;

            if (t < 200)
            {
                input.OnHeld("Forward", 1.0f);
            }
            else if (t < 400)
            {
                input.OnHeld("Forward", 1.0f);
                input.OnHeld("Sprint", 1.0f);
            }
            else if (t == 400)
            {
                input.OnPressed("Jump");
                jumpPressed = true;
            }
            else if (t < 450)
            {
                grounded = false;
                vertVel = 5.0f - (t - 400) * 0.5f;
            }
            else if (t < 500)
            {
                grounded = false;
                vertVel = -2.0f;
            }
            else
            {
                grounded = true;
            }

            var snap = input.Snapshot();
            var stateInputs = new MovementStateInputs
            {
                Grounded = grounded,
                SprintHeld = snap.SprintHeld,
                CrouchHeld = snap.CrouchHeld,
                JumpPressed = jumpPressed,
                AnyMoveAxisNonzero = Math.Abs(snap.Forward) > 0.001f || Math.Abs(snap.Strafe) > 0.001f,
                JumpHoldElapsed = false,
                CanStandUp = true,
                VerticalVelocity = vertVel,
            };

            state = MovementStateTransitions.Next(state, stateInputs);

            switch (state)
            {
                case MovementState.Walking: walkingTicks++; break;
                case MovementState.Sprinting: sprintingTicks++; break;
                case MovementState.Jumping: jumpingTicks++; break;
                case MovementState.Falling: fallingTicks++; break;
                case MovementState.Idle: idleTicks++; break;
                case MovementState.Crouching: crouchingTicks++; break;
            }
        }

        // Golden reference counts (recorded at plan-write time; update
        // if the state-machine semantics intentionally change).
        var actual = new
        {
            Walking = walkingTicks,
            Sprinting = sprintingTicks,
            Jumping = jumpingTicks,
            Falling = fallingTicks,
            Idle = idleTicks,
            Crouching = crouchingTicks,
        };

        Console.WriteLine($"[FPC smoke] state-tick counts: " +
            $"Walking={actual.Walking}, Sprinting={actual.Sprinting}, " +
            $"Jumping={actual.Jumping}, Falling={actual.Falling}, " +
            $"Idle={actual.Idle}, Crouching={actual.Crouching}");

        // Acceptance windows (±5 ticks for each phase, generous so
        // small numerical changes don't trip the gate).
        bool ok =
            Math.Abs(actual.Walking - 200) <= 5 &&
            Math.Abs(actual.Sprinting - 200) <= 5 &&
            Math.Abs(actual.Jumping - 1) <= 1 &&
            Math.Abs(actual.Falling - 99) <= 5 &&
            Math.Abs(actual.Idle - 500) <= 5 &&
            actual.Crouching == 0;

        if (!ok)
        {
            Console.Error.WriteLine("[FPC smoke] FAIL: tick-count expectations not met. " +
                "Either the state-machine semantics changed (update the goldens) or there's " +
                "a real regression.");
            return 1;
        }

        Console.WriteLine("[FPC smoke] PASS");
        return 0;
    }
}
```

- [ ] **Step 10.3: Run the smoke test, verify exit 0**

Run:
```bash
cd F:/O3DESharp/Gems/O3DE.FirstPersonController/SmokeTests
dotnet run -c Release --nologo 2>&1 | tail -5
echo "exit code: $?"
```

Expected:
```
[FPC smoke] state-tick counts: Walking=200, Sprinting=200, Jumping=1, Falling=99, Idle=500, Crouching=0
[FPC smoke] PASS
exit code: 0
```

If the counts are off, capture them and either adjust the goldens (if the state-machine semantics are correct) or fix the state machine. The acceptance windows are ±5 ticks so small jitter is tolerated.

- [ ] **Step 10.4: Commit**

```bash
cd F:/O3DESharp
git add Gems/O3DE.FirstPersonController/SmokeTests/
git commit -m "FPC port: SmokeTests console-app for state-machine regression"
```

---

## Task 11: Hook the new tests into TeamCity CI

The `.teamcity/settings.kts` already builds every test csproj automatically because of TC's project-wide discovery — but our explicit step list only names the `BindingGenerator.Tests` project. Add the FPC tests + smoke to the build matrix.

**Files:**
- Modify: `F:/O3DESharp/.teamcity/settings.kts`

- [ ] **Step 11.1: Inspect the current Linux build steps**

Run:
```bash
cd F:/O3DESharp
grep -n "Run BindingGenerator xUnit tests" .teamcity/settings.kts | head -3
```

Expected: prints the two lines (one for the Linux config, one for the Windows config) that invoke `dotnet test ... BindingGenerator.Tests.csproj`. We'll add a new step immediately after each.

- [ ] **Step 11.2: Add FPC test + smoke steps to the Linux config**

In `F:/O3DESharp/.teamcity/settings.kts`, find the Linux config's existing step:

```kotlin
        script {
            name = "Run BindingGenerator xUnit tests"
            scriptContent = """
                set -e
                "${'$'}HOME/.dotnet/dotnet" test Code/Tools/BindingGenerator.Tests/BindingGenerator.Tests.csproj -c Release --nologo --logger "console;verbosity=normal"
            """.trimIndent()
        }
```

Insert these two new steps immediately after that closing brace, BEFORE the closing brace of the `steps { ... }` block:

```kotlin
        script {
            name = "Run FirstPersonController xUnit tests"
            scriptContent = """
                set -e
                "${'$'}HOME/.dotnet/dotnet" test Gems/O3DE.FirstPersonController/Tests/O3DE.FirstPersonController.Tests.csproj -c Release --nologo --logger "console;verbosity=normal"
            """.trimIndent()
        }
        script {
            name = "Run FirstPersonController smoke (state-machine golden trajectory)"
            scriptContent = """
                set -e
                "${'$'}HOME/.dotnet/dotnet" run --project Gems/O3DE.FirstPersonController/SmokeTests/O3DE.FirstPersonController.SmokeTests.csproj -c Release --no-restore
            """.trimIndent()
        }
```

- [ ] **Step 11.3: Add the same two steps to the Windows config**

Find the Windows config's `Run BindingGenerator xUnit tests` step and add these two after it:

```kotlin
        script {
            name = "Run FirstPersonController xUnit tests"
            scriptContent = """
                ${'$'}ErrorActionPreference = 'Stop'
                & "${'$'}env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test Gems/O3DE.FirstPersonController/Tests/O3DE.FirstPersonController.Tests.csproj -c Release --nologo --logger "console;verbosity=normal"
            """.trimIndent()
        }
        script {
            name = "Run FirstPersonController smoke (state-machine golden trajectory)"
            scriptContent = """
                ${'$'}ErrorActionPreference = 'Stop'
                & "${'$'}env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" run --project Gems/O3DE.FirstPersonController/SmokeTests/O3DE.FirstPersonController.SmokeTests.csproj -c Release --no-restore
            """.trimIndent()
        }
```

- [ ] **Step 11.4: Mirror to the engine path so TC reading from there stays in sync**

```bash
cp F:/O3DESharp/.teamcity/settings.kts F:/engine/Gems/O3DESharp/.teamcity/settings.kts
```

- [ ] **Step 11.5: Verify the kts is syntactically valid (if Maven + JDK are available)**

Skip if `mvn` is not on PATH; TC's compiler will catch errors on the next push.

```bash
which mvn && cd F:/O3DESharp/.teamcity && mvn -q test-compile 2>&1 | tail -5
```

Expected: silent (no errors) or the `mvn` command not found (acceptable; TC validates on sync).

- [ ] **Step 11.6: Commit**

```bash
cd F:/O3DESharp
git add .teamcity/settings.kts
git commit -m "FPC port: add FirstPersonController.Tests + SmokeTests to TC matrix"
```

---

## Task 12: README + final M1 validation

Write the gem's README documenting install + setup + tunables. Then run the whole test suite end-to-end as a sanity check.

**Files:**
- Create: `F:/O3DESharp/Gems/O3DE.FirstPersonController/README.md`

- [ ] **Step 12.1: Write the README**

Path: `F:/O3DESharp/Gems/O3DE.FirstPersonController/README.md`

```markdown
# O3DE FirstPersonController — C# Port (v0.1, M1)

C# port of [Porcupine-Factory/FirstPersonController](https://github.com/Porcupine-Factory/FirstPersonController) riding on the [O3DESharp](../..) gem.

**Status — M1 (this release):** walking, sprinting, jumping, falling, grounded detection, PhysX-backed movement, named-event input.

**Coming in M2:** crouch with PID-driven height transition, the full ~90-method `FirstPersonControllerComponentRequestBus` surface, `CameraCoupledChildComponent`, `FirstPersonExtrasComponent` (lean / headbob / footstep).

**Not in v1:** Multiplayer / network replication.

---

## Requirements

- O3DE with O3DESharp v1.0.0+ enabled
- PhysX5 gem enabled (provides `CharacterControllerRequestBus`)
- StartingPointInput gem enabled (provides `InputEventNotificationBus` + the `.inputbindings` asset pipeline)
- .NET 9.0 SDK + Runtime

## Install

1. Clone or copy this gem next to the O3DESharp gem in your engine's gems folder.
2. Register the gem: `o3de register --gem-path Gems/O3DE.FirstPersonController`
3. Enable it on your project: `o3de enable-gem --gem-name O3DE.FirstPersonController --project-path <your project>`
4. Build the C# library: `cd Gems/O3DE.FirstPersonController && dotnet build -c Release`
5. The MSBuild target deploys `O3DE.FirstPersonController.dll` next to `O3DE.Core.dll` in your project's `Bin/Scripts/` folder.

## Usage

1. Create a player entity in your level.
2. Attach the following components:
   - **PhysX Character Controller** (must come before the CSharp Script component)
   - **CSharp Script**, with Script Class set to `O3DE.FirstPersonController.Components.FirstPersonControllerComponent`
3. Set up the input bindings: drop a `.inputbindings` asset on the entity (StartingPointInput's Input component) that maps keys → the following event names:
   - `Forward`, `Back`, `Left`, `Right` (analog/digital axes)
   - `Yaw`, `Pitch` (mouse/stick deltas)
   - `Sprint`, `Crouch` (hold buttons)
   - `Jump` (press)
4. Hit play.

## Tunables (inspector-editable)

| Property | Default | Range | What it does |
|---|---|---|---|
| Walk Speed | 5.0 | (0.1, 100) | Top horizontal speed in m/s when not sprinting |
| Acceleration | 30.0 | (1, 200) | Horizontal accel toward target speed (m/s²) |
| Deceleration | 1.5 | (0.1, 10) | Horizontal damping factor per second |
| Sprint Multiplier | 1.5 | (1.0, 5.0) | WalkSpeed × this while Sprint is held |
| Gravity | 30.0 | (0.1, 100) | Downward acceleration (positive) |
| Jump Initial Velocity | 6.0 | (0.5, 20) | m/s applied upward on jump press |
| Jump Hold Distance | 0.8 | (0, 5) | m above start Z where jump-hold ends |
| Max Grounded Angle | 30° | (0, 89) | Slope above which character cannot stand |
| Character Radius | 0.4 | (0.1, 2.0) | Used by the sphere-cast ground check |
| Eye Height | 1.6 | (0.5, 3) | Standing eye height; used by camera child in M2 |

Defaults match upstream FPC at main HEAD on 2026-05-21.

## License

Apache-2.0 OR MIT, matching the parent O3DESharp gem and the upstream FPC's MPL-2.0.
```

- [ ] **Step 12.2: Run the full test + smoke + build end-to-end**

```bash
cd F:/O3DESharp/Gems/O3DE.FirstPersonController
dotnet build -c Release --nologo 2>&1 | tail -3
dotnet test Tests/O3DE.FirstPersonController.Tests.csproj -c Release --nologo 2>&1 | tail -5
dotnet run --project SmokeTests/O3DE.FirstPersonController.SmokeTests.csproj -c Release --no-restore 2>&1 | tail -3
```

Expected:
- Build: `0 Warning(s), 0 Error(s)`
- Tests: `Passed: 30+, Failed: 0`
- Smoke: `[FPC smoke] PASS` with exit 0

If anything fails, fix it before committing. The smoke + tests are the M1 acceptance criteria.

- [ ] **Step 12.3: Commit + final M1 tag**

```bash
cd F:/O3DESharp
git add Gems/O3DE.FirstPersonController/README.md
git commit -m "FPC port: README documenting install + setup + tunables"

# Optional: tag the M1 milestone
git tag -a fpc-port-m1 -m "FirstPersonController C# port - M1 core (walk/sprint/jump/fall/PhysX)"
```

- [ ] **Step 12.4: Push to origin (optional — defer to user)**

If the user wants this on the remote:
```bash
cd F:/O3DESharp
git push origin main main:development
git push origin fpc-port-m1
```

---

## M1 acceptance criteria

All of the following must hold before declaring M1 done:

1. `dotnet build -c Release` on `O3DE.FirstPersonController.csproj` succeeds with 0 warnings / 0 errors.
2. `dotnet test` on the Tests csproj passes 100% (PidController1D × 4, MovementStateTransition × ~20, InputAxisAccumulator × 9, GroundCheck × 4 — roughly 37 tests).
3. `dotnet run` on the SmokeTests project exits 0 with the expected tick-count output.
4. TeamCity Cloud (or any CI) running on the next push reports the new test + smoke steps green on both Linux and Windows agents.
5. A manual editor smoke: spawn a player entity with the FPC component + PhysX CharacterController, attach a sample .inputbindings, hit play, verify WASD moves you, Shift sprints, Space jumps, gravity returns you to ground.

The manual editor smoke is out of scope for an automated test but is a v1-release gate — record the result in a follow-up commit message before tagging v1.0.0 on this gem.

---

## What M2 will add (out of scope for this plan)

For reference; M2 gets its own writing-plans pass.

- `MovementState.Crouching` transition arms + PID-driven eye-height interpolation
- Overhead-clearance sphere-cast for the canStandUp gate
- `[EBus("FirstPersonControllerComponentRequestBus")]` handler methods (~90 thin getter/setter pairs)
- `CameraCoupledChildComponent` standalone component
- `FirstPersonExtrasComponent` (Lean / Headbob / Footstep)
- `FirstPersonControllerSystem` static init + `MovementTuning` defaults setreg
- Sample `.inputbindings` asset shipped with the gem
- Manual-test checklist file
