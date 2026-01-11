#pragma once

#include <O3DESharp/O3DESharpTypeIds.h>

#include <AzCore/EBus/EBus.h>
#include <AzCore/Interface/Interface.h>
#include <AzCore/std/string/string.h>

namespace O3DESharp
{
    /**
     * O3DESharpRequests - Interface for C# scripting system operations
     * 
     * This interface provides methods for:
     * - Managing the Coral .NET host lifecycle
     * - Loading and reloading C# assemblies
     * - Querying the state of the scripting system
     */
    class O3DESharpRequests
    {
    public:
        AZ_RTTI(O3DESharpRequests, O3DESharpRequestsTypeId);
        virtual ~O3DESharpRequests() = default;

        // ============================================================
        // Host Management
        // ============================================================

        /**
         * Check if the Coral .NET host is initialized and ready
         * @return true if the host is initialized
         */
        virtual bool IsCoralHostInitialized() const { return false; }

        /**
         * Get the status message of the Coral host
         * @return A status string describing the current state
         */
        virtual AZStd::string GetCoralHostStatus() const { return "Not implemented"; }

        // ============================================================
        // Assembly Management
        // ============================================================

        /**
         * Load a C# assembly from the specified path
         * @param assemblyPath Full path to the .dll file
         * @return true if the assembly was loaded successfully
         */
        virtual bool LoadAssembly([[maybe_unused]] const AZStd::string& assemblyPath) { return false; }

        /**
         * Reload all user assemblies (triggers hot-reload)
         * @return true if the reload was successful
         */
        virtual bool ReloadUserAssemblies() { return false; }

        /**
         * Check if hot-reload is enabled
         * @return true if hot-reload is enabled
         */
        virtual bool IsHotReloadEnabled() const { return false; }

        // ============================================================
        // Type Queries
        // ============================================================

        /**
         * Check if a C# type exists in the loaded assemblies
         * @param fullTypeName The fully qualified type name (e.g., "MyNamespace.MyClass")
         * @return true if the type exists
         */
        virtual bool TypeExists([[maybe_unused]] const AZStd::string& fullTypeName) const { return false; }

        /**
         * Get a list of all script types (classes inheriting from ScriptComponent)
         * @return A vector of fully qualified type names
         */
        virtual AZStd::vector<AZStd::string> GetAvailableScriptTypes() const { return {}; }

        // ============================================================
        // Configuration
        // ============================================================

        /**
         * Get the path to the Coral directory
         * @return The path to the Coral.Managed.dll directory
         */
        virtual AZStd::string GetCoralDirectory() const { return ""; }

        /**
         * Get the path to the core API assembly (O3DE.Core.dll)
         * @return The path to O3DE.Core.dll
         */
        virtual AZStd::string GetCoreAssemblyPath() const { return ""; }

        /**
         * Get the path to the user game assembly
         * @return The path to the user's game scripts DLL
         */
        virtual AZStd::string GetUserAssemblyPath() const { return ""; }
    };

    /**
     * EBus traits for O3DESharpRequests
     */
    class O3DESharpBusTraits
        : public AZ::EBusTraits
    {
    public:
        //////////////////////////////////////////////////////////////////////////
        // EBusTraits overrides
        
        // Only one handler (the O3DESharpSystemComponent)
        static constexpr AZ::EBusHandlerPolicy HandlerPolicy = AZ::EBusHandlerPolicy::Single;
        
        // Single address (global system)
        static constexpr AZ::EBusAddressPolicy AddressPolicy = AZ::EBusAddressPolicy::Single;
        //////////////////////////////////////////////////////////////////////////
    };

    using O3DESharpRequestBus = AZ::EBus<O3DESharpRequests, O3DESharpBusTraits>;
    using O3DESharpInterface = AZ::Interface<O3DESharpRequests>;

} // namespace O3DESharp