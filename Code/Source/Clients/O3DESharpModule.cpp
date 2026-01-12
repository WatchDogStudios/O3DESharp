
#include <O3DESharp/O3DESharpTypeIds.h>
#include <O3DESharpModuleInterface.h>

#include <AzCore/RTTI/RTTI.h>

#include <Components/O3DESharpComponent.h>

namespace O3DESharp
{
    class O3DESharpModule
        : public O3DESharpModuleInterface
    {
    public:
        AZ_RTTI(O3DESharpModule, O3DESharpModuleTypeId, O3DESharpModuleInterface);
        AZ_CLASS_ALLOCATOR(O3DESharpModule, AZ::SystemAllocator);

        O3DESharpModule()
        {
            // Base class (O3DESharpModuleInterface) already registers:
            // - O3DESharpSystemComponent
            // - CSharpScriptComponent
            // Only add additional runtime-specific components here.
            m_descriptors.insert(m_descriptors.end(),
                {
                    O3DESharpComponent::CreateDescriptor(),
                });
        }

        // Use base class GetRequiredSystemComponents() which returns O3DESharpSystemComponent
    };
}// namespace O3DESharp

#if defined(O3DE_GEM_NAME)
AZ_DECLARE_MODULE_CLASS(AZ_JOIN(Gem_, O3DE_GEM_NAME), O3DESharp::O3DESharpModule)
#else
AZ_DECLARE_MODULE_CLASS(Gem_O3DESharp, O3DESharp::O3DESharpModule)
#endif
