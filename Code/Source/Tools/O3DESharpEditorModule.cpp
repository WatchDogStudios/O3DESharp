
#include <O3DESharp/O3DESharpTypeIds.h>
#include <O3DESharpModuleInterface.h>
#include "O3DESharpEditorSystemComponent.h"
#include "Components/EditorO3DESharpComponent.h"
#include "Components/EditorCSharpScriptComponent.h"

namespace O3DESharp
{
    //! Editor module for O3DESharp
    //! Inherits from O3DESharpModuleInterface to get runtime component registrations
    //! (needed for Asset Processor to serialize runtime components when BuildGameEntity is called)
    class O3DESharpEditorModule
        : public O3DESharpModuleInterface
    {
    public:
        AZ_RTTI(O3DESharpEditorModule, O3DESharpEditorModuleTypeId, O3DESharpModuleInterface);
        AZ_CLASS_ALLOCATOR(O3DESharpEditorModule, AZ::SystemAllocator);

        O3DESharpEditorModule()
        {
            // Base class (O3DESharpModuleInterface) already registers:
            // - O3DESharpSystemComponent
            // - CSharpScriptComponent (runtime - needed for Asset Processor to serialize spawnables)
            //
            // Here we add Editor-specific component descriptors.
            m_descriptors.insert(m_descriptors.end(), {
                O3DESharpEditorSystemComponent::CreateDescriptor(),
                EditorO3DESharpComponent::CreateDescriptor(),
                EditorCSharpScriptComponent::CreateDescriptor(),
            });
        }

        /**
         * Add required SystemComponents to the SystemEntity.
         * Non-SystemComponents should not be added here
         */
        AZ::ComponentTypeList GetRequiredSystemComponents() const override
        {
            return AZ::ComponentTypeList {
                azrtti_typeid<O3DESharpEditorSystemComponent>(),
            };
        }
    };
}// namespace O3DESharp

#if defined(O3DE_GEM_NAME)
AZ_DECLARE_MODULE_CLASS(AZ_JOIN(Gem_, O3DE_GEM_NAME, _Editor), O3DESharp::O3DESharpEditorModule)
#else
AZ_DECLARE_MODULE_CLASS(Gem_O3DESharp_Editor, O3DESharp::O3DESharpEditorModule)
#endif
