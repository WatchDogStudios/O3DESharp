/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

#pragma once

#include <AzCore/RTTI/BehaviorContext.h>
#include <AzCore/std/containers/vector.h>
#include <AzCore/std/string/string.h>
#include <AzCore/JSON/document.h>

namespace O3DESharp::Marshaling
{
    /**
     * Stack allocator for transient string storage used while a
     * BehaviorArgument is being populated. The BehaviorContext invocation
     * machinery expects argument string buffers to outlive the call, but
     * not the surrounding scope - so we keep them in a flat arena that
     * dies with the marshaling pass.
     *
     * Modeled on EditorPythonBindings' Convert::StackVariableAllocator
     * (we don't reuse it directly because the EPB header isn't usable
     * outside that gem, and Phase 18-A.1 of the spec calls for this
     * helper to live independently so the runtime path doesn't depend
     * on EditorPythonBindings being loaded).
     */
    class StackAllocator
    {
    public:
        StackAllocator() = default;
        ~StackAllocator();
        StackAllocator(const StackAllocator&) = delete;
        StackAllocator& operator=(const StackAllocator&) = delete;

        AZStd::string* AllocString();
        AZStd::vector<AZ::u8>* AllocBlittableBuffer(size_t bytes);

    private:
        AZStd::vector<AZStd::string*> m_strings;
        AZStd::vector<AZStd::vector<AZ::u8>*> m_buffers;
    };

    /**
     * Try to convert a JSON value into a BehaviorArgument compatible with
     * the parameter description. Supports the marshaling-table types in
     * PHASE_18_EBUS.md section 4:
     *
     *   - bool, sbyte/byte, short/ushort, int/uint, long/ulong, float, double
     *   - AZStd::string  (from JSON string)
     *   - AZ::Vector3, AZ::Quaternion, AZ::Color  (from JSON array of 3/4 floats)
     *   - AZ::EntityId  (from JSON number representing the underlying u64)
     *
     * On success returns true and populates outArg with a value that lives
     * inside the StackAllocator. On unsupported types or shape mismatches
     * returns false; outArg is left in an unspecified state.
     */
    bool JsonValueToBehaviorParameter(
        const rapidjson::Value& json,
        const AZ::BehaviorParameter& param,
        AZ::BehaviorArgument& outArg,
        StackAllocator& alloc,
        AZStd::string& errorOut);

    /**
     * Inverse: pack a BehaviorArgument's value into a JSON value. Used
     * when an EBus event has a return type and we need to send it back
     * to managed code. Same type table as JsonValueToBehaviorParameter.
     */
    bool BehaviorArgumentToJsonValue(
        const AZ::BehaviorArgument& arg,
        rapidjson::Value& outJson,
        rapidjson::MemoryPoolAllocator<>& alloc,
        AZStd::string& errorOut);

    /**
     * Convenience: parse a "[arg0, arg1, ...]" JSON array into a
     * vector of BehaviorArguments shaped by the target method's
     * parameter list. Returns false on any per-arg failure; errorOut
     * carries a short human-readable description.
     */
    bool MarshalJsonArrayToArguments(
        const rapidjson::Value& jsonArray,
        const AZ::BehaviorMethod& method,
        AZStd::vector<AZ::BehaviorArgument>& outArgs,
        StackAllocator& alloc,
        AZStd::string& errorOut,
        size_t skipFirstNParams = 0);

} // namespace O3DESharp::Marshaling
