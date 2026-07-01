//
// Copyright (c) Contributors to the Open 3D Engine Project.
// For complete copyright and license terms please see the LICENSE at the root of this distribution.
//
// SPDX-License-Identifier: Apache-2.0 OR MIT
//

using System;
using System.Diagnostics;
using System.Threading;
using O3DE;

namespace O3DE.Core.Tests;

/// <summary>
/// Tests for Debugger.WaitForAttach's timeout semantics. These run in a plain
/// xUnit test host with no managed debugger attached (Debugger.IsAttached is
/// false throughout), so WaitForAttach's polling loop - if it were entered -
/// would spin until its deadline. Assertions on wall-clock time prove the
/// Zero / default-parameter cases return immediately instead of entering
/// that loop at all.
/// </summary>
public class DebuggerTests
{
    private static readonly TimeSpan MaxAllowedDuration = TimeSpan.FromMilliseconds(500);

    [Fact]
    public void WaitForAttach_WithZeroTimeout_ReturnsImmediatelyWithoutBlocking()
    {
        var stopwatch = Stopwatch.StartNew();

        bool result = Debugger.WaitForAttach(TimeSpan.Zero);

        stopwatch.Stop();

        result.Should().BeFalse("no debugger is attached in the test host");
        stopwatch.Elapsed.Should().BeLessThan(MaxAllowedDuration,
            "TimeSpan.Zero must mean \"check IsAttached once and return\", not \"wait forever\"");
    }

    [Fact]
    public void WaitForAttach_WithDefaultParameter_ReturnsImmediatelyWithoutBlocking()
    {
        var stopwatch = Stopwatch.StartNew();

        // No-args call - exercises the `timeout = default` parameter, which
        // is TimeSpan.Zero. Before this fix this call blocked forever
        // because default(TimeSpan) was treated as "wait forever" instead
        // of "check once".
        bool result = Debugger.WaitForAttach();

        stopwatch.Stop();

        result.Should().BeFalse("no debugger is attached in the test host");
        stopwatch.Elapsed.Should().BeLessThan(MaxAllowedDuration,
            "WaitForAttach() with no arguments must check IsAttached once and return, not wait forever");
    }

    [Fact]
    public void WaitForAttach_WithNegativeTimeout_EntersPollingLoopInsteadOfReturningImmediately()
    {
        // We can't actually wait forever in a test. Instead we confirm that
        // a negative TimeSpan (Timeout.InfiniteTimeSpan) is routed into the
        // "wait forever" branch (not the "check once" branch) by racing it
        // against a short join timeout on a background thread: since
        // IsAttached can never become true in this test host, the only way
        // to observe the "wait forever" branch without hanging the test
        // suite is to confirm it does NOT return within well under one poll
        // interval (100ms) - i.e. it actually enters the polling loop
        // rather than short-circuiting the way Zero does.
        var thread = new Thread(() => Debugger.WaitForAttach(Timeout.InfiniteTimeSpan))
        {
            IsBackground = true
        };
        thread.Start();

        bool finishedQuickly = thread.Join(TimeSpan.FromMilliseconds(50));

        finishedQuickly.Should().BeFalse(
            "Timeout.InfiniteTimeSpan must enter the polling loop (wait forever), not return immediately like TimeSpan.Zero does");

        // The background thread is left running (IsBackground = true means
        // it will not prevent process exit); it polls IsAttached every
        // 100ms forever, which is harmless for the remaining lifetime of
        // this test process.
    }
}
