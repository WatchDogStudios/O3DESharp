# SP-1a Native→Managed Pinned Thunks Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove the per-call string-lookup + .NET-reflection cost from every native→managed call, starting with `CSharpScriptComponent`'s per-frame `Tick`.

**Architecture:** Add a public function-pointer API to the Coral fork, use it to resolve `[UnmanagedCallersOnly]` managed entry points into raw callable pointers once, and route `CSharpScriptComponent`'s lifecycle calls through those pointers — keeping `Coral::ManagedObject::InvokeMethod` as an always-available fallback so a resolution failure degrades to today's behaviour rather than breaking.

**Tech Stack:** C++ (AzCore, Coral), C# (`O3DE.Core`, `[UnmanagedCallersOnly]`), .NET hosting (`hostfxr` / `load_assembly_and_get_function_pointer`), xUnit.

## Global Constraints

- This is **Half A only** of SP-1 (`docs/superpowers/specs/2026-07-15-sp1-marshaling-core-design.md`). The manifest / trampoline / `BindingRegistry` work is **Half B** and gets its own plan. Do not start it here.
- **Determinism / sim-lane scope is explicitly dropped** (spec §10). Ignore `ISimSystem` / `SimSystemBase` / tiering-lock references in the rescued comments — that second half was never written and is not in scope.
- **The fallback is mandatory and first-class.** Every thunk call site must fall back to `InvokeMethod` when the thunk is unavailable. A resolution failure is a performance regression, never a functional one.
- **No C++ in this repository can be compile-verified in the development environment** (no O3DE engine SDK present). C++ tasks must be authored against real, existing signatures — read the file before editing it — and every C++ commit message must state that it is not compile-verified. The maintainer verifies via a real engine build.
- **Task 1 is CROSS-REPO** (`WatchDogStudios/Coral`) and gates Tasks 2, 4, 7.
- Commit messages must contain **no** Claude/Anthropic co-author or attribution trailers.
- The rescued code (`Rescued/1b-native-trampoline/`, branch `feat/1b-native-trampoline-rescue`) is **unfinished and has never been compiled**. Treat it as a reference for *analysis and intent*, not as code to paste unchanged (spec §11.3).

## File structure

| File | Responsibility |
|---|---|
| `WatchDogStudios/Coral` → `HostInstance.hpp/.cpp` (modify, **cross-repo**) | Public `GetFunctionPointer` returning a raw callable pointer for an `[UnmanagedCallersOnly]` managed static. |
| `Code/Source/Scripting/CoralNativeThunkHost.h/.cpp` (create) | Thin cache over the Coral API: resolve once, memoize by (type, method), invalidate on reload. No hostfxr handling of its own. |
| `Assets/Scripts/O3DE.Core/Interop/ScriptComponentBridge.cs` (create) | Managed side: an instance-handle table plus the `[UnmanagedCallersOnly]` entry point that dispatches to a live `ScriptComponent`. |
| `Assets/Scripts/O3DE.Core.Tests/Interop/ScriptComponentBridgeTests.cs` (create) | Tests for the handle table and dispatch routing (the parts testable without a host). |
| `Code/Source/Scripting/CSharpScriptComponent.cpp/.h` (modify) | Route lifecycle calls through the thunk with `InvokeMethod` fallback. |

---

### Task 1: Coral fork — public function-pointer API — **CROSS-REPO**

**Files:**
- Modify (in a `WatchDogStudios/Coral` checkout): `Coral.Native/Include/Coral/HostInstance.hpp`, `Coral.Native/Source/Coral/HostInstance.cpp`

**Interfaces:**
- Produces (consumed by Task 2): `void* Coral::HostInstance::GetFunctionPointer(std::string_view assemblyQualifiedTypeName, std::string_view methodName)` — returns `nullptr` on failure, never throws.

> **This task is in a different repository.** It gates Tasks 2, 4 and 7. Confirm with the maintainer whether they land it or delegate it.

- [ ] **Step 1: Read the existing private path**

Open `HostInstance.cpp` and locate `LoadCoralManagedFunctionPtr` and the `s_CoreCLRFunctions.GetManagedFunctionPtr` usage inside `InitializeCoralManaged`. This is the exact mechanism to expose — Coral already uses it to bootstrap `Coral.Managed`'s own entry points. Note the stored hostfxr context member (`m_HostFXRContext` or equivalent) and the delegate's real signature.

- [ ] **Step 2: Declare the public API**

In `HostInstance.hpp`, inside the `HostInstance` public section:

```cpp
        /// Resolve an [UnmanagedCallersOnly] managed static to a raw,
        /// natively-callable function pointer. Resolution happens once;
        /// the returned pointer has no per-call managed lookup or
        /// reflection cost, unlike InvokeMethod.
        ///
        /// assemblyQualifiedTypeName: e.g. "O3DE.Interop.ScriptComponentBridge, O3DE.Core"
        /// methodName:                the static method's name
        ///
        /// Returns nullptr if the host is not initialized or resolution
        /// fails. Never throws - callers are expected to fall back to
        /// InvokeMethod.
        void* GetFunctionPointer(std::string_view assemblyQualifiedTypeName, std::string_view methodName);
```

- [ ] **Step 3: Implement it**

In `HostInstance.cpp`, implement by delegating to the same mechanism `LoadCoralManagedFunctionPtr` uses. Adapt the body to the fork's actual member and delegate names discovered in Step 1 — do not invent names:

```cpp
    void* HostInstance::GetFunctionPointer(std::string_view assemblyQualifiedTypeName, std::string_view methodName)
    {
        if (!m_Initialized)
        {
            return nullptr;
        }

        void* functionPtr = nullptr;
        // UNMANAGEDCALLERSONLY_METHOD tells the runtime the target carries
        // [UnmanagedCallersOnly], so no delegate type name is required.
        const int status = s_CoreCLRFunctions.GetManagedFunctionPtr(
            std::string(assemblyQualifiedTypeName).c_str(),
            std::string(methodName).c_str(),
            UNMANAGEDCALLERSONLY_METHOD,
            nullptr,
            nullptr,
            &functionPtr);

        return (status == 0) ? functionPtr : nullptr;
    }
```

- [ ] **Step 4: Build the Coral fork**

Build Coral.Native per its own README.
Expected: builds clean. If `GetManagedFunctionPtr`'s signature differs from the above, match the actual one — the semantic requirement is only "resolve an `[UnmanagedCallersOnly]` static to a raw pointer, return nullptr on failure."

- [ ] **Step 5: Commit and push in the Coral repository**

```bash
git add Coral.Native/Include/Coral/HostInstance.hpp Coral.Native/Source/Coral/HostInstance.cpp
git commit -m "Add HostInstance::GetFunctionPointer for [UnmanagedCallersOnly] statics

Coral exposed exactly one native->managed path, ManagedObject::InvokeMethod,
which performs a managed-side string lookup plus reflection dispatch on every
call. The reverse direction (AddInternalCall) has been a zero-per-call pinned
thunk all along; this closes that asymmetry.

Uses the same GetManagedFunctionPtr mechanism HostInstance already uses to
bootstrap Coral.Managed's own entry points - it simply makes it available to
embedders for their own assemblies. Returns nullptr on failure so callers can
fall back to InvokeMethod."
git push
```

- [ ] **Step 6: Record the resolved commit for the gem**

Note the pushed SHA. `Code/CMakeLists.txt` tracks `WatchDogStudios/Coral@main` via FetchContent, so a fresh configure picks it up; record the SHA in Task 2's commit message for traceability.

---

### Task 2: CoralNativeThunkHost on the Coral API

**Files:**
- Create: `Code/Source/Scripting/CoralNativeThunkHost.h`
- Create: `Code/Source/Scripting/CoralNativeThunkHost.cpp`
- Modify: `Code/o3desharp_private_files.cmake`

**Interfaces:**
- Consumes: `Coral::HostInstance::GetFunctionPointer` (Task 1).
- Produces (consumed by Tasks 4, 7):
  - `using PinnedThunk = void*;`
  - `void CoralNativeThunkHost::SetHost(Coral::HostInstance* host);`
  - `PinnedThunk CoralNativeThunkHost::Get(AZStd::string_view assemblyQualifiedTypeName, AZStd::string_view methodName);` — returns `nullptr` if unavailable
  - `void CoralNativeThunkHost::InvalidateCache();`

- [ ] **Step 1: Read the rescued reference**

Read `Rescued/1b-native-trampoline/Code/Source/Scripting/CoralNativeThunkHost.h` (on branch `feat/1b-native-trampoline-rescue`). Its analysis is correct and worth understanding, **but its implementation independently re-initializes hostfxr** because an earlier task forbade touching Coral. That constraint no longer applies. **Do not carry the hostfxr re-initialization forward** — Task 1 replaced the need for it.

- [ ] **Step 2: Write the header**

Create `Code/Source/Scripting/CoralNativeThunkHost.h`:

```cpp
/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */
#pragma once

#include <AzCore/std/string/string.h>
#include <AzCore/std/string/string_view.h>
#include <AzCore/std/containers/unordered_map.h>

namespace Coral
{
    class HostInstance;
}

namespace O3DESharp
{
    //! Memoizing cache over Coral::HostInstance::GetFunctionPointer.
    //!
    //! Coral's only native->managed call path, ManagedObject::InvokeMethod,
    //! does a managed-side string lookup plus reflection dispatch on EVERY
    //! call. Resolving an [UnmanagedCallersOnly] static to a raw function
    //! pointer once removes that cost from every subsequent call.
    //!
    //! Get() returns nullptr when the host is unset or resolution fails.
    //! Callers MUST treat nullptr as "use InvokeMethod instead" - the
    //! fallback is a first-class path, not an error case.
    class CoralNativeThunkHost
    {
    public:
        using PinnedThunk = void*;

        //! Set the live host. Must be called after CoralHostManager has
        //! brought up the CLR. Passing a different host clears the cache.
        void SetHost(Coral::HostInstance* host);

        //! Resolve (and memoize) a managed static. nullptr => not available.
        //! assemblyQualifiedTypeName e.g. "O3DE.Interop.ScriptComponentBridge, O3DE.Core"
        PinnedThunk Get(AZStd::string_view assemblyQualifiedTypeName, AZStd::string_view methodName);

        //! Drop all cached pointers. MUST be called on assembly reload -
        //! a pointer into an unloaded ALC is dangling.
        void InvalidateCache();

        //! Diagnostic: number of currently memoized thunks.
        size_t CachedCount() const;

    private:
        Coral::HostInstance* m_host = nullptr;
        AZStd::unordered_map<AZStd::string, PinnedThunk> m_cache;
    };
} // namespace O3DESharp
```

- [ ] **Step 3: Write the implementation**

Create `Code/Source/Scripting/CoralNativeThunkHost.cpp`:

```cpp
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

namespace O3DESharp
{
    void CoralNativeThunkHost::SetHost(Coral::HostInstance* host)
    {
        if (m_host != host)
        {
            // Cached pointers belong to the previous host's runtime.
            m_cache.clear();
            m_host = host;
        }
    }

    CoralNativeThunkHost::PinnedThunk CoralNativeThunkHost::Get(
        AZStd::string_view assemblyQualifiedTypeName, AZStd::string_view methodName)
    {
        if (m_host == nullptr)
        {
            return nullptr;
        }

        AZStd::string key(assemblyQualifiedTypeName);
        key += "::";
        key += methodName;

        auto it = m_cache.find(key);
        if (it != m_cache.end())
        {
            return it->second;
        }

        PinnedThunk thunk = m_host->GetFunctionPointer(
            std::string_view(assemblyQualifiedTypeName.data(), assemblyQualifiedTypeName.size()),
            std::string_view(methodName.data(), methodName.size()));

        if (thunk == nullptr)
        {
            AZLOG_WARN(
                "[O3DESharp] No pinned thunk for %.*s::%.*s - falling back to InvokeMethod",
                static_cast<int>(assemblyQualifiedTypeName.size()), assemblyQualifiedTypeName.data(),
                static_cast<int>(methodName.size()), methodName.data());
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
```

- [ ] **Step 4: Add to the build**

In `Code/o3desharp_private_files.cmake`, add to the `FILES` list, keeping alphabetical order with its neighbours:

```cmake
    Source/Scripting/CoralNativeThunkHost.cpp
    Source/Scripting/CoralNativeThunkHost.h
```

- [ ] **Step 5: Verify what can be verified here**

C++ cannot be compiled in this environment. Verify mechanically instead:

```bash
python -c "
import pathlib
h = pathlib.Path('Code/Source/Scripting/CoralNativeThunkHost.h').read_text(encoding='utf-8')
c = pathlib.Path('Code/Source/Scripting/CoralNativeThunkHost.cpp').read_text(encoding='utf-8')
cm = pathlib.Path('Code/o3desharp_private_files.cmake').read_text(encoding='utf-8')
assert 'hostfxr' not in (h+c).lower(), 'hostfxr re-init must NOT be carried forward'
for m in ('SetHost','Get(','InvalidateCache','CachedCount'):
    assert m in h and m.split('(')[0] in c, m
assert 'CoralNativeThunkHost.cpp' in cm and 'CoralNativeThunkHost.h' in cm
print('structural checks pass')
"
```
Expected: `structural checks pass`

- [ ] **Step 6: Commit**

```bash
git add Code/Source/Scripting/CoralNativeThunkHost.h Code/Source/Scripting/CoralNativeThunkHost.cpp Code/o3desharp_private_files.cmake
git commit -m "SP-1a: memoizing thunk cache over Coral's new GetFunctionPointer

Resolves [UnmanagedCallersOnly] managed statics to raw function pointers once
and caches them, removing the per-call string lookup + reflection dispatch that
ManagedObject::InvokeMethod performs.

Unlike the rescued 1B prototype this does NOT re-initialize hostfxr itself -
that workaround only existed because an earlier task could not modify Coral.
It now uses HostInstance::GetFunctionPointer (Coral fork <SHA from Task 1>).

Caches negative results too, so a missing thunk does not re-log every frame.
NOT COMPILE-VERIFIED: no O3DE engine SDK in the authoring environment."
```

---

### Task 3: Managed bridge — handle table + `[UnmanagedCallersOnly]` entry point

**Files:**
- Create: `Assets/Scripts/O3DE.Core/Interop/ScriptComponentBridge.cs`
- Test: `Assets/Scripts/O3DE.Core.Tests/Interop/ScriptComponentBridgeTests.cs`

**Interfaces:**
- Produces (consumed by Tasks 4, 7):
  - `static int ScriptComponentBridge.Register(object instance)` → handle (never 0)
  - `static void ScriptComponentBridge.Unregister(int handle)`
  - `static object? ScriptComponentBridge.Resolve(int handle)`
  - `[UnmanagedCallersOnly] static int ScriptComponentBridge.Invoke(int handle, int lifecycleId, float arg)` → 1 on success, 0 if unhandled
  - `enum LifecycleId { OnCreate = 1, OnDestroy = 2, Tick = 3, OnTransformChanged = 4 }`

> **Why a handle table:** the thunk target must be a **static** method, but the calls are instance lifecycle callbacks. C++ holds an opaque `int` handle per component and passes it back; the managed side resolves it to the live instance with an array index, not reflection. Using our own table (rather than Coral's internal object handles) keeps this independent of Coral internals.

- [ ] **Step 1: Write the failing tests**

Create `Assets/Scripts/O3DE.Core.Tests/Interop/ScriptComponentBridgeTests.cs`:

```csharp
//
// Copyright (c) Contributors to the Open 3D Engine Project.
// For complete copyright and license terms please see the LICENSE at the root of this distribution.
//
// SPDX-License-Identifier: Apache-2.0 OR MIT
//

using System;
using O3DE.Interop;

namespace O3DE.Core.Tests.Interop;

/// <summary>
/// The handle table is what makes a STATIC [UnmanagedCallersOnly] thunk able to
/// service INSTANCE lifecycle callbacks: C++ holds an opaque int per component
/// and hands it back on every call. These pin the properties the native side
/// relies on - handles are never 0 (0 is the native "no handle" sentinel),
/// they are not reused while live, and resolving a stale handle is safe.
/// </summary>
public class ScriptComponentBridgeTests
{
    [Fact]
    public void Register_ReturnsNonZeroHandle()
    {
        var handle = ScriptComponentBridge.Register(new object());
        try
        {
            handle.Should().NotBe(0, "0 is the native sentinel for 'no handle'");
        }
        finally
        {
            ScriptComponentBridge.Unregister(handle);
        }
    }

    [Fact]
    public void Resolve_ReturnsTheRegisteredInstance()
    {
        var instance = new object();
        var handle = ScriptComponentBridge.Register(instance);
        try
        {
            ScriptComponentBridge.Resolve(handle).Should().BeSameAs(instance);
        }
        finally
        {
            ScriptComponentBridge.Unregister(handle);
        }
    }

    [Fact]
    public void DistinctInstances_GetDistinctHandles()
    {
        var a = ScriptComponentBridge.Register(new object());
        var b = ScriptComponentBridge.Register(new object());
        try
        {
            a.Should().NotBe(b);
        }
        finally
        {
            ScriptComponentBridge.Unregister(a);
            ScriptComponentBridge.Unregister(b);
        }
    }

    [Fact]
    public void Resolve_AfterUnregister_ReturnsNull()
    {
        var handle = ScriptComponentBridge.Register(new object());
        ScriptComponentBridge.Unregister(handle);

        // Native code can legitimately race a teardown against an in-flight
        // tick; resolving a dead handle must be safe, not throw.
        ScriptComponentBridge.Resolve(handle).Should().BeNull();
    }

    [Fact]
    public void Resolve_UnknownHandle_ReturnsNull()
    {
        ScriptComponentBridge.Resolve(0).Should().BeNull();
        ScriptComponentBridge.Resolve(999999).Should().BeNull();
        ScriptComponentBridge.Resolve(-1).Should().BeNull();
    }

    [Fact]
    public void Unregister_IsIdempotent()
    {
        var handle = ScriptComponentBridge.Register(new object());
        ScriptComponentBridge.Unregister(handle);

        // Double-unregister must not throw - teardown paths can run twice.
        var act = () => ScriptComponentBridge.Unregister(handle);
        act.Should().NotThrow();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Assets/Scripts/O3DE.Core.Tests/O3DE.Core.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~ScriptComponentBridgeTests"`
Expected: build failure — `O3DE.Interop` / `ScriptComponentBridge` does not exist.

- [ ] **Step 3: Implement the bridge**

Create `Assets/Scripts/O3DE.Core/Interop/ScriptComponentBridge.cs`:

```csharp
//
// Copyright (c) Contributors to the Open 3D Engine Project.
// For complete copyright and license terms please see the LICENSE at the root of this distribution.
//
// SPDX-License-Identifier: Apache-2.0 OR MIT
//

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace O3DE.Interop
{
    /// <summary>
    /// Which lifecycle callback a native Invoke is requesting. Values are part
    /// of the native ABI - C++ passes these integers - so they must never be
    /// renumbered without changing CSharpScriptComponent.cpp in lockstep.
    /// </summary>
    public enum LifecycleId
    {
        OnCreate = 1,
        OnDestroy = 2,
        Tick = 3,
        OnTransformChanged = 4,
    }

    /// <summary>
    /// The native-callable entry point for script component lifecycle calls.
    ///
    /// Coral's ManagedObject.InvokeMethod does a managed-side string lookup plus
    /// reflection dispatch on every call, which for Tick means every frame per
    /// component. This bridge is resolved once to a raw function pointer via
    /// Coral's GetFunctionPointer, after which calls cost an indirect call plus
    /// an array index.
    ///
    /// The thunk target must be static, but lifecycle callbacks are per
    /// instance, so C++ holds an opaque int handle per component and passes it
    /// back on every call. Handle 0 is reserved as the native "no handle"
    /// sentinel and is never issued.
    /// </summary>
    public static class ScriptComponentBridge
    {
        private static readonly object s_lock = new object();
        private static readonly Dictionary<int, object> s_instances = new Dictionary<int, object>();
        private static int s_nextHandle = 1; // 0 is the "no handle" sentinel

        /// <summary>Register an instance and return its native handle (never 0).</summary>
        public static int Register(object instance)
        {
            if (instance is null)
            {
                throw new ArgumentNullException(nameof(instance));
            }

            lock (s_lock)
            {
                int handle = s_nextHandle++;
                if (s_nextHandle == 0)
                {
                    // Wrapped past int.MaxValue; skip the sentinel.
                    s_nextHandle = 1;
                }
                s_instances[handle] = instance;
                return handle;
            }
        }

        /// <summary>Drop a handle. Safe to call more than once.</summary>
        public static void Unregister(int handle)
        {
            lock (s_lock)
            {
                s_instances.Remove(handle);
            }
        }

        /// <summary>Resolve a handle, or null if it is unknown or already released.</summary>
        public static object? Resolve(int handle)
        {
            lock (s_lock)
            {
                return s_instances.TryGetValue(handle, out var instance) ? instance : null;
            }
        }

        /// <summary>
        /// Native entry point. Returns 1 if the call was dispatched, 0 if the
        /// handle was dead or the component does not implement the callback -
        /// in which case the native side simply does nothing (it must NOT fall
        /// back to InvokeMethod here; a dead handle means the component is gone).
        ///
        /// Must never throw: an exception crossing an [UnmanagedCallersOnly]
        /// boundary terminates the process.
        /// </summary>
        [UnmanagedCallersOnly]
        public static int Invoke(int handle, int lifecycleId, float arg)
        {
            try
            {
                object? instance = Resolve(handle);
                if (instance is null)
                {
                    return 0;
                }

                return Dispatch(instance, (LifecycleId)lifecycleId, arg) ? 1 : 0;
            }
            catch (Exception ex)
            {
                // Swallow: never let an exception cross the native boundary.
                Debug.LogError($"ScriptComponentBridge.Invoke failed: {ex}");
                return 0;
            }
        }

        /// <summary>
        /// Route to the concrete callback. Separated from Invoke so it is
        /// reachable from tests (Invoke itself is [UnmanagedCallersOnly] and
        /// cannot be called from managed code).
        /// </summary>
        internal static bool Dispatch(object instance, LifecycleId id, float arg)
        {
            if (instance is not ScriptComponent component)
            {
                return false;
            }

            switch (id)
            {
                case LifecycleId.OnCreate:
                    component.OnCreate();
                    return true;
                case LifecycleId.OnDestroy:
                    component.OnDestroy();
                    return true;
                case LifecycleId.Tick:
                    component.Tick(arg);
                    return true;
                case LifecycleId.OnTransformChanged:
                    component.OnTransformChanged();
                    return true;
                default:
                    return false;
            }
        }
    }
}
```

- [ ] **Step 4: Reconcile against the real `ScriptComponent`**

Verified against `Assets/Scripts/O3DE.Core/ScriptComponent.cs` on 2026-07-15:

| Member | Line | Kind | Used by `Dispatch` as |
|---|---|---|---|
| `OnCreate()` | 118 | `public virtual` | direct call |
| `OnUpdate(float)` | 126 | `public virtual` | **not called directly** |
| `OnDestroy()` | 134 | `public virtual` | direct call |
| `OnTransformChanged()` | 142 | `public virtual` | direct call |
| `Tick(float)` | 339 | `public` **non-virtual** | direct call |

**The `Tick` / `OnUpdate` distinction matters.** `Tick(float)` is the native entry point (it is what `SafeInvokeMethod("Tick", dt)` calls today) and it internally drives the `Invoke`/`InvokeRepeating` timer machinery *and* the virtual `OnUpdate`. `Dispatch` must therefore call **`Tick`**, not `OnUpdate` — calling `OnUpdate` directly would silently skip the delayed-invoke bookkeeping. Conversely, user code and test probes override **`OnUpdate`**, because `Tick` is not virtual.

Confirm these still hold before proceeding; if the class has changed, adjust `Dispatch` to match it rather than renaming the existing API.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test Assets/Scripts/O3DE.Core.Tests/O3DE.Core.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~ScriptComponentBridgeTests"`
Expected: `Passed! - Failed: 0, Passed: 6`

- [ ] **Step 6: Run the full managed suite**

Run: `dotnet test Assets/Scripts/O3DE.Core.Tests/O3DE.Core.Tests.csproj -c Release --nologo`
Expected: all pass (previous count plus 6).

- [ ] **Step 7: Commit**

```bash
git add Assets/Scripts/O3DE.Core/Interop/ScriptComponentBridge.cs Assets/Scripts/O3DE.Core.Tests/Interop/ScriptComponentBridgeTests.cs
git commit -m "SP-1a: managed bridge with handle table and [UnmanagedCallersOnly] entry

A pinned thunk must target a static method, but lifecycle callbacks are per
instance, so C++ holds an opaque int handle per component and passes it back on
every call; the managed side resolves it via a dictionary rather than
reflection. Handle 0 is reserved as the native no-handle sentinel.

Invoke never throws - an exception crossing an [UnmanagedCallersOnly] boundary
terminates the process - and returns 0 for a dead handle so a teardown racing an
in-flight tick is safe."
```

---

### Task 4: Route `Tick` through the thunk, with fallback

**Files:**
- Modify: `Code/Source/Scripting/CSharpScriptComponent.h`
- Modify: `Code/Source/Scripting/CSharpScriptComponent.cpp`

**Interfaces:**
- Consumes: `CoralNativeThunkHost::Get` (Task 2), `ScriptComponentBridge.Invoke` + `LifecycleId` (Task 3).

> `Tick` is the highest-value target: `SafeInvokeMethod("Tick", deltaTime)` runs **every frame, per component**. Convert it first and in isolation, so the differential test in Task 5 has one well-understood change to validate.

- [ ] **Step 1: Read the current implementation**

Read `Code/Source/Scripting/CSharpScriptComponent.cpp` around lines 295-380 — `Activate`/`Deactivate`, the tick handler calling `SafeInvokeMethod("Tick", deltaTime)`, and both `SafeInvokeMethod` overloads. Note exactly how `m_scriptInstance` is created and destroyed; the handle must be registered and unregistered on the same boundaries.

- [ ] **Step 2: Add the thunk members to the header**

In `CSharpScriptComponent.h`, in the private section:

```cpp
        //! Signature of ScriptComponentBridge.Invoke (see O3DE.Core's
        //! Interop/ScriptComponentBridge.cs). Returns 1 if dispatched, 0 if
        //! the handle was dead. Must match that method exactly.
        using BridgeInvokeFn = int (*)(int handle, int lifecycleId, float arg);

        //! Native handle for this component's managed instance, obtained from
        //! ScriptComponentBridge.Register. 0 means "not registered".
        int m_bridgeHandle = 0;

        //! Resolved once; nullptr means "no thunk available, use InvokeMethod".
        BridgeInvokeFn m_bridgeInvoke = nullptr;

        //! Lifecycle ids - must match O3DE.Interop.LifecycleId exactly.
        enum class LifecycleId : int
        {
            OnCreate = 1,
            OnDestroy = 2,
            Tick = 3,
            OnTransformChanged = 4,
        };

        //! Try the pinned-thunk path. Returns false if unavailable, in which
        //! case the caller MUST fall back to SafeInvokeMethod.
        bool TryInvokeViaThunk(LifecycleId id, float arg) noexcept;
```

- [ ] **Step 3: Implement the thunk path**

In `CSharpScriptComponent.cpp`, add:

```cpp
    bool CSharpScriptComponent::TryInvokeViaThunk(LifecycleId id, float arg) noexcept
    {
        if (m_bridgeInvoke == nullptr || m_bridgeHandle == 0)
        {
            return false;
        }

        // A 0 return means the managed handle is dead (component torn down).
        // That is NOT a reason to fall back to InvokeMethod - the instance is
        // gone, so doing nothing is correct.
        m_bridgeInvoke(m_bridgeHandle, static_cast<int>(id), arg);
        return true;
    }
```

- [ ] **Step 4: Route Tick through it**

Replace the tick call site's `SafeInvokeMethod("Tick", deltaTime);` with:

```cpp
            // Fast path: a resolved pinned thunk costs an indirect call plus a
            // dictionary lookup. InvokeMethod would do a managed-side string
            // lookup plus reflection dispatch - every frame, per component.
            if (!TryInvokeViaThunk(LifecycleId::Tick, deltaTime))
            {
                SafeInvokeMethod("Tick", deltaTime);
            }
```

- [ ] **Step 5: Resolve the thunk and register the handle**

Where `m_scriptInstance` is created (after it is valid), resolve the thunk and register the handle. Use the real accessor for the host that the surrounding code already uses — read the file rather than assuming:

```cpp
        // Resolve the pinned thunk once per instance. Failure is expected and
        // survivable: m_bridgeInvoke stays null and every call site falls back.
        m_bridgeInvoke = reinterpret_cast<BridgeInvokeFn>(
            thunkHost.Get("O3DE.Interop.ScriptComponentBridge, O3DE.Core", "Invoke"));

        // One-shot, so InvokeMethod's cost is irrelevant here. Uses the same
        // value-returning pattern already proven in the codebase (see
        // O3DESharpSystemComponent.cpp: InvokeMethod<Coral::String>(...)).
        m_bridgeHandle = m_scriptInstance.InvokeMethod<int>("AcquireBridgeHandle");
```

This immediately follows `m_scriptInstance = hostManager->CreateInstance(*m_scriptType);` (around `CSharpScriptComponent.cpp:578`) — the instance must exist before either call.

- [ ] **Step 6: Add the managed handle accessors**

`ScriptComponentBridge.Register` is `static`, and the proven native call path in this codebase is the **instance** method `InvokeMethod<T>`. So `ScriptComponent` needs two thin instance methods that native can call. Add to `Assets/Scripts/O3DE.Core/ScriptComponent.cs`:

```csharp
        // Native bridge handle. Assigned on first acquire; 0 means unregistered.
        private int _bridgeHandle;

        /// <summary>
        /// Register this instance with <see cref="O3DE.Interop.ScriptComponentBridge"/>
        /// and return its native handle. Called once by native code immediately
        /// after construction. Idempotent - repeated calls return the same handle.
        /// </summary>
        public int AcquireBridgeHandle()
        {
            if (_bridgeHandle == 0)
            {
                _bridgeHandle = O3DE.Interop.ScriptComponentBridge.Register(this);
            }
            return _bridgeHandle;
        }

        /// <summary>
        /// Release the native bridge handle. Called by native code during
        /// teardown, AFTER the final OnDestroy dispatch. Safe to call twice.
        /// </summary>
        public void ReleaseBridgeHandle()
        {
            if (_bridgeHandle != 0)
            {
                O3DE.Interop.ScriptComponentBridge.Unregister(_bridgeHandle);
                _bridgeHandle = 0;
            }
        }
```

Then in `Deactivate`/destruction, unregister symmetrically and clear the native members. **Order matters** — this must run *after* the `OnDestroy` dispatch (see Task 7 Step 2):

```cpp
        if (m_bridgeHandle != 0)
        {
            m_scriptInstance.InvokeMethod("ReleaseBridgeHandle");
            m_bridgeHandle = 0;
        }
        m_bridgeInvoke = nullptr;
```

- [ ] **Step 7: Structural verification**

```bash
python -c "
import pathlib
h = pathlib.Path('Code/Source/Scripting/CSharpScriptComponent.h').read_text(encoding='utf-8')
c = pathlib.Path('Code/Source/Scripting/CSharpScriptComponent.cpp').read_text(encoding='utf-8')
assert 'TryInvokeViaThunk' in h and 'TryInvokeViaThunk' in c
assert 'SafeInvokeMethod(\"Tick\"' in c, 'the InvokeMethod fallback must remain'
assert 'if (!TryInvokeViaThunk' in c, 'Tick must attempt the thunk first'
print('structural checks pass')
"
```
Expected: `structural checks pass` — note it asserts the fallback still exists.

- [ ] **Step 8: Commit**

```bash
git add Code/Source/Scripting/CSharpScriptComponent.h Code/Source/Scripting/CSharpScriptComponent.cpp
git commit -m "SP-1a: route CSharpScriptComponent::Tick through a pinned thunk

Tick ran SafeInvokeMethod(\"Tick\", dt) every frame per component, and
ManagedObject::InvokeMethod does a managed-side string lookup plus reflection
dispatch on every call. It now attempts a resolved pinned thunk first and falls
back to InvokeMethod when one is unavailable, so a resolution failure costs
speed rather than correctness.

Only Tick is converted here; the remaining lifecycle calls follow once the
differential test covers this path.

NOT COMPILE-VERIFIED: no O3DE engine SDK in the authoring environment."
```

---

### Task 5: Differential + fallback tests

**Files:**
- Modify: `Assets/Scripts/O3DE.Core.Tests/Interop/ScriptComponentBridgeTests.cs`

> Full native↔managed differential testing needs a live host and is the maintainer's engine-side verification (Task 6). What **is** testable here is the managed half of the contract: that `Dispatch` routes each `LifecycleId` to the right callback, and that the native ABI constants have not drifted.

- [ ] **Step 1: Write the failing tests**

Append to `Assets/Scripts/O3DE.Core.Tests/Interop/ScriptComponentBridgeTests.cs`:

```csharp
/// <summary>
/// Pins the native ABI. LifecycleId values are passed as raw integers from
/// CSharpScriptComponent.cpp's enum of the same name; if these drift apart,
/// Tick starts calling OnDestroy and nothing fails to compile on either side.
/// </summary>
public class LifecycleIdAbiTests
{
    [Theory]
    [InlineData(LifecycleId.OnCreate, 1)]
    [InlineData(LifecycleId.OnDestroy, 2)]
    [InlineData(LifecycleId.Tick, 3)]
    [InlineData(LifecycleId.OnTransformChanged, 4)]
    public void LifecycleId_HasStableNativeValue(LifecycleId id, int expected)
    {
        ((int)id).Should().Be(expected,
            "these integers are the native ABI - renumbering silently misroutes callbacks");
    }

    [Fact]
    public void Dispatch_UnknownLifecycleId_ReturnsFalse()
    {
        var component = new DispatchProbe();
        ScriptComponentBridge.Dispatch(component, (LifecycleId)9999, 0f).Should().BeFalse();
        component.Calls.Should().BeEmpty();
    }

    [Fact]
    public void Dispatch_NonScriptComponent_ReturnsFalse()
    {
        ScriptComponentBridge.Dispatch(new object(), LifecycleId.Tick, 0f).Should().BeFalse();
    }

    [Fact]
    public void Dispatch_RoutesTickWithDeltaTime()
    {
        var component = new DispatchProbe();

        ScriptComponentBridge.Dispatch(component, LifecycleId.Tick, 0.25f).Should().BeTrue();

        component.Calls.Should().ContainSingle().Which.Should().Be("Tick:0.25");
    }

    [Theory]
    [InlineData(LifecycleId.OnCreate, "OnCreate")]
    [InlineData(LifecycleId.OnDestroy, "OnDestroy")]
    [InlineData(LifecycleId.OnTransformChanged, "OnTransformChanged")]
    public void Dispatch_RoutesEachLifecycleToItsCallback(LifecycleId id, string expected)
    {
        var component = new DispatchProbe();

        ScriptComponentBridge.Dispatch(component, id, 0f).Should().BeTrue();

        component.Calls.Should().ContainSingle().Which.Should().Be(expected);
    }
}
```

- [ ] **Step 2: Add the probe type**

The probe must derive from the real `ScriptComponent` so `Dispatch`'s type check is genuinely exercised. Append to the same test file, adjusting the overrides to match `ScriptComponent`'s actual virtual members (read it first):

```csharp
/// <summary>
/// Records which callbacks fired, so routing can be asserted.
///
/// NOTE: overrides OnUpdate, NOT Tick. In ScriptComponent, `Tick(float)` is a
/// public NON-virtual method (the native entry point, which also drives the
/// Invoke/InvokeRepeating timer machinery) and it calls the virtual
/// `OnUpdate(float)`. Attempting `override void Tick` does not compile.
/// Dispatching LifecycleId.Tick therefore surfaces here as an OnUpdate call.
/// </summary>
internal sealed class DispatchProbe : ScriptComponent
{
    public List<string> Calls { get; } = new List<string>();

    public override void OnCreate() => Calls.Add("OnCreate");
    public override void OnDestroy() => Calls.Add("OnDestroy");
    public override void OnUpdate(float deltaTime) =>
        Calls.Add($"Tick:{deltaTime.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
    public override void OnTransformChanged() => Calls.Add("OnTransformChanged");
}
```

- [ ] **Step 3: Run to verify they fail, then pass**

Run: `dotnet test Assets/Scripts/O3DE.Core.Tests/O3DE.Core.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~Interop"`
Expected: initially failures (missing `DispatchProbe` / accessibility); after Step 2 and any signature reconciliation, all pass.

- [ ] **Step 4: Run the full managed suite**

Run: `dotnet test Assets/Scripts/O3DE.Core.Tests/O3DE.Core.Tests.csproj -c Release --nologo`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add Assets/Scripts/O3DE.Core.Tests/Interop/ScriptComponentBridgeTests.cs
git commit -m "SP-1a: pin the lifecycle ABI and dispatch routing

LifecycleId integers are the native ABI - CSharpScriptComponent.cpp declares a
matching enum and passes raw ints. If the two drift, Tick starts invoking
OnDestroy and nothing fails to compile on either side, so the values are now
asserted explicitly alongside routing tests for every callback."
```

---

### Task 6: Engine-side verification — **MAINTAINER-EXECUTED**

**Files:** none.

> Requires a real O3DE engine build. None of the C++ in Tasks 2 and 4 has been compiled.

- [ ] **Step 1: Build with the updated Coral fork**

Reconfigure so FetchContent picks up the Coral commit from Task 1, and build the gem.
Expected: builds clean. Compilation errors in `CoralNativeThunkHost` or `CSharpScriptComponent` are expected fallout of authoring C++ blind — fix and report them.

- [ ] **Step 2: Verify the thunk actually resolves**

Run the Editor with a C# script component on an entity. Confirm the log does **not** contain
`No pinned thunk for O3DE.Interop.ScriptComponentBridge, O3DE.Core::Invoke`.
If it does, the thunk is not resolving and every call is silently falling back — functional, but the task delivered nothing.

- [ ] **Step 3: Differential check**

Confirm behaviour is unchanged: `OnCreate`, `Tick`, `OnTransformChanged` and `OnDestroy` all still fire, in order, with correct `deltaTime`. Then deliberately force the fallback (e.g. temporarily rename the managed method so resolution fails) and confirm behaviour is **identical** via `InvokeMethod`. Both paths must be indistinguishable.

- [ ] **Step 4: Measure**

Capture a before/after profile of the per-frame tick cost with a meaningful number of C# components (100+). The expected win is the removal of a managed string lookup plus reflection dispatch per component per frame. Record the number — it justifies (or refutes) proceeding to Task 7 and Half B.

- [ ] **Step 5: Report**

Report build fixes, whether the thunk resolved, differential result, and the measurement.

---

### Task 7: Convert the remaining lifecycle calls

**Files:**
- Modify: `Code/Source/Scripting/CSharpScriptComponent.cpp`

> **Gated on Task 6.** Do not convert the rest until `Tick` is proven working and measured in a real build.

- [ ] **Step 1: Convert the remaining three call sites**

Apply the same pattern to `OnCreate`, `OnDestroy` and `OnTransformChanged`:

```cpp
            if (!TryInvokeViaThunk(LifecycleId::OnCreate, 0.0f))
            {
                SafeInvokeMethod("OnCreate");
            }
```

```cpp
        if (!TryInvokeViaThunk(LifecycleId::OnDestroy, 0.0f))
        {
            SafeInvokeMethod("OnDestroy");
        }
```

```cpp
            if (!TryInvokeViaThunk(LifecycleId::OnTransformChanged, 0.0f))
            {
                SafeInvokeMethod("OnTransformChanged");
            }
```

Leave `ApplyExposedProperties` and `GetExposedPropertySchemaJson` on `InvokeMethod`: they are one-shot editor-path calls that pass strings, so they gain nothing and would need a different ABI.

- [ ] **Step 2: Ordering check for OnDestroy**

`OnDestroy` runs during teardown. Confirm the bridge handle is still registered at that point and only unregistered **after** the `OnDestroy` dispatch — otherwise the thunk sees a dead handle, returns 0, and `OnDestroy` is silently skipped. This is the one ordering hazard in the conversion.

- [ ] **Step 3: Structural verification**

```bash
python -c "
import pathlib
c = pathlib.Path('Code/Source/Scripting/CSharpScriptComponent.cpp').read_text(encoding='utf-8')
for lid in ('OnCreate','OnDestroy','Tick','OnTransformChanged'):
    assert f'LifecycleId::{lid}' in c, lid
    assert f'SafeInvokeMethod(\"{lid}\"' in c, f'{lid} fallback must remain'
print('all four lifecycle calls converted, all fallbacks retained')
"
```
Expected: `all four lifecycle calls converted, all fallbacks retained`

- [ ] **Step 4: Commit**

```bash
git add Code/Source/Scripting/CSharpScriptComponent.cpp
git commit -m "SP-1a: route remaining lifecycle calls through pinned thunks

OnCreate, OnDestroy and OnTransformChanged now follow the same
try-thunk-then-fall-back pattern as Tick. ApplyExposedProperties and
GetExposedPropertySchemaJson deliberately stay on InvokeMethod: one-shot
editor-path calls passing strings, which would need a different ABI for no gain.

OnDestroy required care - the bridge handle must outlive the dispatch, or the
thunk sees a dead handle and the callback is silently skipped.

NOT COMPILE-VERIFIED: no O3DE engine SDK in the authoring environment."
```

---

## What is NOT in this plan

Half B — `NativeBindingManifest`, `ReflectionCallSiteParser`, `NativeBindingGenerator`, `BindingRegistry`, load-time manifest validation, and binding-coverage telemetry — is a separate plan, written after SP-1a lands. Half B is the larger, riskier half and is the piece dual-mode AOT consumes; it should not start until the thunk path is proven in a real build (Task 6).
