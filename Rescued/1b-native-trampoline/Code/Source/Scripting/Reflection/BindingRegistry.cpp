/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

#include "BindingRegistry.h"

#include <AzCore/Console/ILogger.h>
#include <AzCore/std/containers/vector.h>

namespace O3DESharp
{
    AZStd::unordered_map<AZStd::string, BindingRegistry::TrampolineFn>& BindingRegistry::GetMap()
    {
        static AZStd::unordered_map<AZStd::string, TrampolineFn> s_map;
        return s_map;
    }

    void BindingRegistry::Register(AZStd::string_view bindingId, TrampolineFn fn)
    {
        if (bindingId.empty() || fn == nullptr)
        {
            AZLOG_ERROR("BindingRegistry::Register - invalid arguments (empty id or null fn)");
            return;
        }
        GetMap()[AZStd::string(bindingId)] = fn;
    }

    void BindingRegistry::Unregister(AZStd::string_view bindingId)
    {
        auto& map = GetMap();
        auto it = map.find(AZStd::string(bindingId));
        if (it != map.end())
        {
            map.erase(it);
        }
    }

    BindingRegistry::TrampolineFn BindingRegistry::Lookup(AZStd::string_view bindingId)
    {
        auto& map = GetMap();
        auto it = map.find(AZStd::string(bindingId));
        return it != map.end() ? it->second : nullptr;
    }

    bool BindingRegistry::IsBound(AZStd::string_view bindingId)
    {
        return Lookup(bindingId) != nullptr;
    }

    size_t BindingRegistry::Count()
    {
        return GetMap().size();
    }

    void BindingRegistry::Invoke(
        AZStd::string_view bindingId,
        AZ::BehaviorMethod* method,
        const void* self,
        const AZ::Uuid& selfTypeId,
        AZStd::span<AZ::BehaviorArgument> args,
        AZ::BehaviorArgument* result)
    {
        if (TrampolineFn fn = Lookup(bindingId))
        {
            fn(self, args, result);
            return;
        }

        // Dynamic fallback. This is the exact call shape verified by the
        // 1B P0 probe (Gems/ScriptCanvasTesting/.../Native/
        // BehaviorNativeBindingTests.cpp, commits 9a2c1b1018 + fixup
        // c7f4cd7719): for a MEMBER method, 'this' must be wrapped as an
        // AZ::BehaviorObject and passed as an EXTRA leading
        // BehaviorArgument in the span handed to
        // AZ::BehaviorMethod::Call(AZStd::span<BehaviorArgument>,
        // BehaviorArgument*) - a raw BehaviorArgument(&v) (no
        // BehaviorObject wrapper) builds a value-typed arg that the
        // member functor mis-dereferences (crash). Static/global methods
        // take 'args' as-is with no synthesized leading argument.
        if (method == nullptr)
        {
            AZLOG_ERROR(
                "BindingRegistry::Invoke - no native trampoline bound for '%.*s' and no BehaviorMethod "
                "supplied for the dynamic fallback",
                static_cast<int>(bindingId.size()), bindingId.data());
            return;
        }

        if (self != nullptr)
        {
            AZ::BehaviorObject thisObject(const_cast<void*>(self), selfTypeId);
            AZ::BehaviorArgument thisArg(&thisObject);

            AZStd::vector<AZ::BehaviorArgument> fullArgs;
            fullArgs.reserve(args.size() + 1);
            fullArgs.push_back(thisArg);
            for (auto& a : args)
            {
                fullArgs.push_back(a);
            }
            method->Call(AZStd::span<AZ::BehaviorArgument>(fullArgs), result);
        }
        else
        {
            method->Call(args, result);
        }
    }

} // namespace O3DESharp
