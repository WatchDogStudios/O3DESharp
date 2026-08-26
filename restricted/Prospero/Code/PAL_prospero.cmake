#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
#

# NOT SUPPORTED on this platform -- matches Xbox's restricted/Xbox/Code/PAL_xbox.cmake exactly,
# and the same conclusion the gem's own Code/CMakeLists.txt already states inline:
# "Disable O3DESharp with coral support on Xbox, PS, Nintendo, breaks XR & TRC. use Mono only
# for those platforms." (Code/CMakeLists.txt:23).
#
# The gem hosts the .NET runtime through Coral (O3DESHARP_CORAL_SOURCE_DIR / the Coral.Native
# target); there is no .NET runtime on this platform to host, and no Coral port for it. Claiming
# support would generate real targets -- O3DESharp.API, O3DESharp.BindingGenerator and the Coral
# FetchContent dependency -- for a console that cannot build or run any of them, and would also
# attempt the .NET-SDK auto-install path (o3de_sharp_netverify(), Code/CMakeLists.txt:30), which
# has no meaning for a console target.
#
# TO REVISIT only if a .NET runtime becomes available for this platform AND Coral is ported to
# it. Flipping this back without both of those will reintroduce targets that cannot build or run.
set(PAL_TRAIT_O3DESHARP_SUPPORTED FALSE)
set(PAL_TRAIT_O3DESHARP_TEST_SUPPORTED FALSE)
set(PAL_TRAIT_O3DESHARP_EDITOR_TEST_SUPPORTED FALSE)
