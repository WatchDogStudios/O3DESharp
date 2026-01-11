
#include <O3DESharp/O3DESharpTypeIds.h>
#include <AzCore/Memory/Memory.h>
#include <AzCore/Module/Module.h>
#include "O3DESharpEditorSystemComponent.h"
#include "Components/EditorO3DESharpComponent.h"
#include "Components/EditorCSharpScriptComponent.h"

namespace O3DESharp
{
    //! Editor module for O3DESharp - only registers Editor-specific components
    //! Runtime components (CSharpScriptComponent, etc.) are registered by the runtime module
    class O3DESharpEditorModule
        : public AZ::Module
    {
    public:
        AZ_RTTI(O3DESharpEditorModule, O3DESharpEditorModuleTypeId, AZ::Module);
        AZ_CLASS_ALLOCATOR(O3DESharpEditorModule, AZ::SystemAllocator);

        O3DESharpEditorModule()
        {
            // Only register Editor-specific component descriptors here.
            // Runtime components are registered by O3DESharpModule (the runtime module).
            // Do NOT register CSharpScriptComponent here - it causes UUID duplication errors
            // when both runtime and editor modules are loaded.
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
