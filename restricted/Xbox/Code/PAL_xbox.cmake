#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
#

# NOT SUPPORTED on this platform.
#
# This file previously said TRUE, because it was copied verbatim from
# Code/Platform/Windows/PAL_windows.cmake when the platform was scaffolded. That is wrong: the gem
# hosts the .NET runtime through Coral (see O3DESHARP_CORAL_SOURCE_DIR / the Coral.Native target in
# Code/CMakeLists.txt), and there is no .NET runtime on this platform to host. Claiming support
# generated real targets -- O3DESharp.API, O3DESharp.BindingGenerator and the Coral dependency --
# for a console that cannot run any of them.
#
# With this FALSE, Code/CMakeLists.txt:24 takes the unsupported branch and produces only the stub
# module, which is the same pattern the Vulkan and Metal RHI gems already use here
# (PAL_TRAIT_ATOM_RHI_VULKAN_SUPPORTED / _METAL_SUPPORTED are both FALSE for this platform).
#
# TO REVISIT only if a .NET runtime becomes available for this platform AND Coral is ported to it.
# Flipping this back without both of those will reintroduce targets that cannot build or run.
set(PAL_TRAIT_O3DESHARP_SUPPORTED FALSE)
set(PAL_TRAIT_O3DESHARP_TEST_SUPPORTED FALSE)
set(PAL_TRAIT_O3DESHARP_EDITOR_TEST_SUPPORTED FALSE)
