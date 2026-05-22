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

    [Fact]
    public void Step_IntegralOnly_AccumulatesOverTime()
    {
        var pid = new PidController1D { Kp = 0f, Ki = 3.0f, Kd = 0f };
        float output = 0f;
        for (int i = 0; i < 10; i++)
            output = pid.Step(error: 1.0f, dt: 0.1f);
        // 10 * 1.0 * 0.1 = integral=1.0; Ki*1.0 = 3.0
        output.Should().BeApproximately(3.0f, precision: 0.001f);
    }

    [Fact]
    public void Step_DerivativeOnly_ReflectsErrorRate()
    {
        var pid = new PidController1D { Kp = 0f, Ki = 0f, Kd = 0.01f };
        pid.Step(error: 0f, dt: 0.1f);  // seed previous error
        var output = pid.Step(error: 5.0f, dt: 0.1f);
        // (5-0)/0.1 = 50; Kd*50 = 0.5
        output.Should().BeApproximately(0.5f, precision: 0.001f);
    }

    [Fact]
    public void Step_AntiWindup_ClampsIntegralToConfiguredRange()
    {
        var pid = new PidController1D
        {
            Kp = 0f, Ki = 1.0f, Kd = 0f,
            IntegralMax = 5.0f,
            IntegralMin = -5.0f,
        };
        for (int i = 0; i < 1000; i++)
            pid.Step(error: 10.0f, dt: 0.016f);
        var final = pid.Step(error: 10.0f, dt: 0.016f);
        // integral clamped at 5.0; Ki*5.0 = 5.0
        final.Should().BeApproximately(5.0f, precision: 0.01f);
    }
}
