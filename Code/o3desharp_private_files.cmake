#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
#

set(FILES
    Source/O3DESharpModuleInterface.cpp
    Source/O3DESharpModuleInterface.h
    Source/Clients/O3DESharpSystemComponent.cpp
    Source/Clients/O3DESharpSystemComponent.h
    Source/Components/O3DESharpComponent.h
    Source/Components/O3DESharpComponent.cpp
    Source/Components/O3DESharpComponentController.h
    Source/Components/O3DESharpComponentController.cpp
    Source/Render/O3DESharpFeatureProcessor.h
    Source/Render/O3DESharpFeatureProcessor.cpp

    # C# Scripting Support via Coral
    Source/Scripting/CoralHostManager.h
    Source/Scripting/CoralHostManager.cpp
    Source/Scripting/ScriptBindings.h
    Source/Scripting/ScriptBindings.cpp
    Source/Scripting/CSharpScriptComponent.h
    Source/Scripting/CSharpScriptComponent.cpp

    # BehaviorContext Reflection System
    Source/Scripting/Reflection/BehaviorContextReflector.h
    Source/Scripting/Reflection/BehaviorContextReflector.cpp
    Source/Scripting/Reflection/GenericDispatcher.h
    Source/Scripting/Reflection/GenericDispatcher.cpp

    # Phase 18-A: BehaviorContext <-> JSON marshaling. Shared utility
    # used by GenericDispatcher's EBus dispatch path and the upcoming
    # 18-B managed-handler bridge. Kept independent of EditorPythonBindings
    # so the runtime gem doesn't pull in the EPB Static dependency just
    # to reuse a converter.
    Source/Scripting/Marshaling/BehaviorContextMarshaling.h
    Source/Scripting/Marshaling/BehaviorContextMarshaling.cpp
    # Exports reflection data to JSON for the Python binding generator
    Source/Scripting/Reflection/ReflectionDataExporter.h
    Source/Scripting/Reflection/ReflectionDataExporter.cpp

    # Generator output (Phase 15). The binding generator overwrites these as
    # part of its normal run; placeholder versions are committed so the gem
    # links on fresh clones. RegisterBindings is called from
    # CoralHostManager::RegisterInternalCalls alongside the hand-written
    # ScriptBindings::RegisterAll.
    Source/Scripting/Generated/BindingRegistration.g.cpp
    Source/Scripting/Generated/O3DESharp_HotReload.g.h
)
