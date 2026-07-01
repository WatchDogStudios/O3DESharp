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
     * Implementation: a fixed-size inline byte arena (bump allocator) is
     * used first for both AZStd::string and AZStd::vector<AZ::u8>
     * objects; this services the common case of a handful of small
     * marshaled arguments per call with zero heap traffic. Each
     * StackAllocator instance's lifetime spans exactly one marshaling
     * pass (see the call sites in GenericDispatcher.cpp, each of which
     * constructs one on the stack immediately before a single
     * MarshalJsonArrayToArguments / JsonValueToBehaviorParameter call and
     * lets it fall out of scope right after). Once the inline arena is
     * exhausted, subsequent allocations fall back to individual heap
     * allocations tracked in m_heapStrings/m_heapBuffers, exactly like
     * the previous always-heap implementation, so correctness never
     * depends on staying under the inline budget.
     */
    class StackAllocator
    {
    public:
        // Inline arena size in bytes. Sized for the common case: most
        // marshaled calls pass a handful of short strings (entity names,
        // ids-as-strings, small labels) or small blittable buffers. 512
        // bytes covers e.g. 8 AZStd::string objects (avg ~40 bytes each
        // incl. SSO payload and alignment) or a couple of small binary
        // buffers, per marshaling pass, before falling back to heap.
        static constexpr size_t kInlineArenaBytes = 512;

        StackAllocator() = default;
        ~StackAllocator();
        StackAllocator(const StackAllocator&) = delete;
        StackAllocator& operator=(const StackAllocator&) = delete;

        AZStd::string* AllocString();
        AZStd::vector<AZ::u8>* AllocBlittableBuffer(size_t bytes);

    private:
        // Bump-allocates `bytes` (aligned to `alignment`) out of the
        // inline arena. Returns nullptr if the arena doesn't have enough
        // room left, in which case the caller falls back to heap.
        void* BumpAlloc(size_t bytes, size_t alignment);

        // Plain fixed-size byte array, not AZStd::array - kept to types
        // already used elsewhere in this file to avoid depending on an
        // unverified container/include. 16-byte alignment comfortably
        // covers AZStd::string / AZStd::vector<AZ::u8>'s actual alignment
        // needs (both are pointer/size_t-based control blocks, not SIMD
        // types) on every platform this gem targets.
        alignas(16) AZ::u8 m_arena[kInlineArenaBytes];
        size_t m_arenaOffset = 0;

        // Objects placement-new'd into m_arena. Tracked so the
        // destructor can call their destructors explicitly (placement
        // new means the arena's raw bytes don't run destructors on
        // their own).
        AZStd::vector<AZStd::string*> m_arenaStrings;
        AZStd::vector<AZStd::vector<AZ::u8>*> m_arenaBuffers;

        // Heap fallback for allocations that don't fit in the inline
        // arena. Same ownership model as the original implementation:
        // plain `new`, `delete`d in the destructor.
        AZStd::vector<AZStd::string*> m_heapStrings;
        AZStd::vector<AZStd::vector<AZ::u8>*> m_heapBuffers;
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
