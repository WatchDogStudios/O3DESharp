
#pragma once

namespace O3DESharp
{
    // System Component TypeIds
    inline constexpr const char* O3DESharpSystemComponentTypeId = "{7662ECF5-6C8B-4461-9C9B-F73D45CA299A}";
    inline constexpr const char* O3DESharpEditorSystemComponentTypeId = "{CE5E4945-1073-4903-8962-009DDE69411F}";

    // Module derived classes TypeIds
    inline constexpr const char* O3DESharpModuleInterfaceTypeId = "{A8594A8E-A745-4108-93E5-B2BB531678D6}";
    inline constexpr const char* O3DESharpModuleTypeId = "{50025E4E-21E5-4434-8007-22DC8150CE4D}";
    // The Editor Module by default is mutually exclusive with the Client Module
    // so they use the Same TypeId
    inline constexpr const char* O3DESharpEditorModuleTypeId = O3DESharpModuleTypeId;

    // Interface TypeIds
    inline constexpr const char* O3DESharpRequestsTypeId = "{4F3D8BA4-4881-49D9-96BD-A66AEF8009FA}";
} // namespace O3DESharp
