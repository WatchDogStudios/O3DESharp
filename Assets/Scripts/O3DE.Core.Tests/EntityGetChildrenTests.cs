//
// Copyright (c) Contributors to the Open 3D Engine Project.
// For complete copyright and license terms please see the LICENSE at the root of this distribution.
//
// SPDX-License-Identifier: Apache-2.0 OR MIT
//

using System.Collections.Generic;
using O3DE;

namespace O3DE.Core.Tests;

/// <summary>
/// Regression tests for the O(n^2)-to-O(1)-native-calls fix in
/// Entity.GetChildren(): previously it called Entity_GetChildCount once
/// then Entity_GetChildAtIndex once per child, each independently
/// re-running a full native EBus broadcast. It now calls Entity_GetChildCount
/// once and Entity_GetChildren once, regardless of child count.
/// </summary>
public class EntityGetChildrenTests
{
    public EntityGetChildrenTests()
    {
        InternalCalls.Reset();
    }

    [Fact]
    public void GetChildren_ReturnsEmptyList_WhenEntityHasNoChildren()
    {
        InternalCalls.ValidEntities.Add(1);
        InternalCalls.ChildrenByEntity[1] = new List<ulong>();
        var entity = new Entity(1);

        var children = entity.GetChildren();

        children.Should().BeEmpty();
    }

    [Fact]
    public void GetChildren_ReturnsEmptyList_WhenEntityIsInvalid()
    {
        var entity = new Entity(999);

        var children = entity.GetChildren();

        children.Should().BeEmpty();
    }

    [Fact]
    public void GetChildren_ReturnsAllChildIds_InOrder()
    {
        InternalCalls.ValidEntities.Add(1);
        InternalCalls.ChildrenByEntity[1] = new List<ulong> { 10, 11, 12 };
        var entity = new Entity(1);

        var children = entity.GetChildren();

        children.Should().HaveCount(3);
        children[0].Id.Should().Be(10);
        children[1].Id.Should().Be(11);
        children[2].Id.Should().Be(12);
    }

    [Fact]
    public void GetChildren_SkipsInvalidIdSentinel_IfPresentInBuffer()
    {
        InternalCalls.ValidEntities.Add(1);
        InternalCalls.ChildrenByEntity[1] = new List<ulong> { 10, Entity.InvalidId, 12 };
        var entity = new Entity(1);

        var children = entity.GetChildren();

        children.Should().HaveCount(2);
    }

    [Fact]
    public void GetChildren_MakesExactlyOneBulkCall_RegardlessOfChildCount()
    {
        InternalCalls.ValidEntities.Add(1);
        var manyChildren = new List<ulong>();
        for (ulong i = 0; i < 50; i++)
        {
            manyChildren.Add(100 + i);
        }
        InternalCalls.ChildrenByEntity[1] = manyChildren;
        var entity = new Entity(1);

        InternalCalls.GetChildrenCallCount = 0;
        InternalCalls.GetChildAtIndexCallCount = 0;
        var children = entity.GetChildren();

        children.Should().HaveCount(50);
        InternalCalls.GetChildrenCallCount.Should().Be(1);
        // The real regression this test guards against: a revert to the old
        // count-then-loop implementation. Asserting the new path ran once is
        // not enough on its own - this also proves the old per-child path
        // never ran at all, regardless of child count.
        InternalCalls.GetChildAtIndexCallCount.Should().Be(0);
    }
}
