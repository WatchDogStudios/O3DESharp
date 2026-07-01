//
// Copyright (c) Contributors to the Open 3D Engine Project.
// For complete copyright and license terms please see the LICENSE at the root of this distribution.
//
// SPDX-License-Identifier: Apache-2.0 OR MIT
//

using O3DE;

namespace O3DE.Core.Tests;

/// <summary>
/// Tests for Debug.cs's format-string overloads. These verify that a malformed
/// format string (e.g. a placeholder with no matching arg) is reported as a
/// log message instead of throwing FormatException back into caller code -
/// a logging API must never crash the caller just because the message it was
/// asked to log was malformed.
///
/// Note: LogDebug is [Conditional("DEBUG")] [Conditional("O3DE_DEBUG")] - calls to it are
/// stripped entirely at the call site unless the calling assembly defines DEBUG or
/// O3DE_DEBUG. It's not covered by a dedicated test here since its formatting path routes
/// through the same shared SafeFormat helper as Log - Log's coverage already exercises
/// that logic.
/// </summary>
public class DebugTests
{
    public DebugTests()
    {
        InternalCalls.Reset();
    }

    [Fact]
    public void Log_WithMismatchedPlaceholderCount_DoesNotThrow()
    {
        Action act = () => Debug.Log("bad {0} {1}", "onlyOneArg");

        act.Should().NotThrow();
    }

    [Fact]
    public void Log_WithMismatchedPlaceholderCount_LogsRawFormatAndErrorMarker()
    {
        Debug.Log("bad {0} {1}", "onlyOneArg");

        InternalCalls.InfoLogs.Should().ContainSingle();
        InternalCalls.InfoLogs[0].Should().Contain("bad {0} {1}");
        InternalCalls.InfoLogs[0].Should().Contain("[format error:");
    }

    [Fact]
    public void Log_WithValidFormatString_FormatsNormally()
    {
        Debug.Log("value is {0}", 42);

        InternalCalls.InfoLogs.Should().ContainSingle("value is 42");
    }

    [Fact]
    public void LogError_WithMismatchedPlaceholderCount_DoesNotThrow()
    {
        Action act = () => Debug.LogError("bad {0} {1}", "onlyOneArg");

        act.Should().NotThrow();
    }

    [Fact]
    public void LogError_WithMismatchedPlaceholderCount_LogsRawFormatAndErrorMarker()
    {
        Debug.LogError("bad {0} {1}", "onlyOneArg");

        InternalCalls.ErrorLogs.Should().ContainSingle();
        InternalCalls.ErrorLogs[0].Should().Contain("bad {0} {1}");
        InternalCalls.ErrorLogs[0].Should().Contain("[format error:");
    }

    [Fact]
    public void LogError_WithValidFormatString_FormatsNormally()
    {
        Debug.LogError("failed with code {0}", 7);

        InternalCalls.ErrorLogs.Should().ContainSingle("failed with code 7");
    }

    [Fact]
    public void LogWarning_WithMismatchedPlaceholderCount_DoesNotThrow()
    {
        Action act = () => Debug.LogWarning("bad {0} {1}", "onlyOneArg");

        act.Should().NotThrow();
    }

    [Fact]
    public void LogWarning_WithMismatchedPlaceholderCount_LogsRawFormatAndErrorMarker()
    {
        Debug.LogWarning("bad {0} {1}", "onlyOneArg");

        InternalCalls.WarningLogs.Should().ContainSingle();
        InternalCalls.WarningLogs[0].Should().Contain("bad {0} {1}");
        InternalCalls.WarningLogs[0].Should().Contain("[format error:");
    }

    // SafeFormat's XML doc justifies catching the broad `Exception` type (not just
    // FormatException) by naming two additional failure modes: a null args array, and an
    // args value whose ToString() throws. The tests below exercise exactly those two cases -
    // without them, the suite would pass identically if the catch clause were narrowed to
    // `catch (FormatException)`, which would undercut the documented design intent.

    [Fact]
    public void Log_WithNullArgsArray_DoesNotThrow()
    {
        Action act = () => Debug.Log("bad {0}", (object[])null!);

        act.Should().NotThrow();
    }

    [Fact]
    public void Log_WithNullArgsArray_LogsRawFormatAndErrorMarker()
    {
        Debug.Log("bad {0}", (object[])null!);

        InternalCalls.InfoLogs.Should().ContainSingle();
        InternalCalls.InfoLogs[0].Should().Contain("bad {0}");
        InternalCalls.InfoLogs[0].Should().Contain("[format error:");
    }

    private sealed class ThrowsOnToString
    {
        public override string ToString() => throw new InvalidOperationException("boom");
    }

    [Fact]
    public void Log_WithArgWhoseToStringThrows_DoesNotThrow()
    {
        Action act = () => Debug.Log("value is {0}", new ThrowsOnToString());

        act.Should().NotThrow();
    }

    [Fact]
    public void Log_WithArgWhoseToStringThrows_LogsRawFormatAndErrorMarker()
    {
        Debug.Log("value is {0}", new ThrowsOnToString());

        InternalCalls.InfoLogs.Should().ContainSingle();
        InternalCalls.InfoLogs[0].Should().Contain("value is {0}");
        InternalCalls.InfoLogs[0].Should().Contain("[format error:");
    }
}
