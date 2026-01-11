
#include "O3DESharpModuleInterface.h"
#include <AzCore/Memory/Memory.h>

#include <O3DESharp/O3DESharpTypeIds.h>

#include <Clients/O3DESharpSystemComponent.h>
#include <Scripting/CSharpScriptComponent.h>

namespace O3DESharp
{
    AZ_TYPE_INFO_WITH_NAME_IMPL(O3DESharpModuleInterface,
        "O3DESharpModuleInterface", O3DESharpModuleInterfaceTypeId);
    AZ_RTTI_NO_TYPE_INFO_IMPL(O3DESharpModuleInterface, AZ::Module);
    AZ_CLASS_ALLOCATOR_IMPL(O3DESharpModuleInterface, AZ::SystemAllocator);

    O3DESharpModuleInterface::O3DESharpModuleInterface()
    {
        // Push results of [MyComponent]::CreateDescriptor() into m_descriptors here.
        // Add ALL components descriptors associated with this gem to m_descriptors.
        // This will associate the AzTypeInfo information for the components with the the SerializeContext, BehaviorContext and EditContext.
        // This happens through the [MyComponent]::Reflect() function.
        m_descriptors.insert(m_descriptors.end(), {
            O3DESharpSystemComponent::CreateDescriptor(),
            CSharpScriptComponent::CreateDescriptor(),
            });
    }

    AZ::ComponentTypeList O3DESharpModuleInterface::GetRequiredSystemComponents() const
    {
        return AZ::ComponentTypeList{
            azrtti_typeid<O3DESharpSystemComponent>(),
        };
    }
} // namespace O3DESharp