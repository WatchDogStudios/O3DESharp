#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
#

# Not yet a claimed target (see gem.json). Coral hosts desktop CoreCLR only, so
# Android is re-enabled with the Mono milestone (JIT-banned platforms). Keeping
# this FALSE makes the support gate in Code/CMakeLists.txt cleanly skip the gem
# here instead of attempting an unsupported build.
set(PAL_TRAIT_O3DESHARP_SUPPORTED FALSE)
set(PAL_TRAIT_O3DESHARP_TEST_SUPPORTED FALSE)
set(PAL_TRAIT_O3DESHARP_EDITOR_TEST_SUPPORTED FALSE)
