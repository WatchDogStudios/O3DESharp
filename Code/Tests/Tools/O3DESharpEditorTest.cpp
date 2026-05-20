/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

#include <AzTest/AzTest.h>

namespace O3DESharp::Editor::Tests
{
    class O3DESharpEditorTestFixture : public ::testing::Test
    {
    };

    TEST_F(O3DESharpEditorTestFixture, SanityCheck_BuildWired_Succeeds)
    {
        EXPECT_TRUE(true);
    }
}

AZ_UNIT_TEST_HOOK(DEFAULT_UNIT_TEST_ENV);
