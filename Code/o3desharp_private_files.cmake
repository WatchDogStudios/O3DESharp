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
    # Exports reflection data to JSON for the Python binding generator
    Source/Scripting/Reflection/ReflectionDataExporter.h
    Source/Scripting/Reflection/ReflectionDataExporter.cpp
)
