/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

#include "BehaviorContextMarshaling.h"

#include <new>

#include <AzCore/Component/EntityId.h>
#include <AzCore/Math/Vector2.h>
#include <AzCore/Math/Vector3.h>
#include <AzCore/Math/Vector4.h>
#include <AzCore/Math/Quaternion.h>
#include <AzCore/Math/Color.h>
#include <AzCore/Math/Transform.h>
#include <AzCore/Math/Matrix3x3.h>
#include <AzCore/Math/Matrix4x4.h>
#include <AzCore/Math/Aabb.h>
#include <AzCore/Math/Crc.h>
#include <AzCore/Math/Uuid.h>
#include <AzCore/std/string/string.h>

namespace O3DESharp::Marshaling
{
    namespace
    {
        // Helper: does the parameter want this exact C++ TypeId?
        bool MatchesType(const AZ::BehaviorParameter& param, const AZ::TypeId& expected)
        {
            return param.m_typeId == expected;
        }

        // Helpers that pack a value into the BehaviorArgument. The
        // BehaviorArgument doesn't own its memory - we point its m_value
        // at storage we manage via the StackAllocator. The marshal helpers
        // below all follow this pattern.
        //
        // Takes by value + moves into StoreInTempData because that
        // method's signature is `template<T> void StoreInTempData(T&&)`
        // - a forwarding reference. Passing a const T& would fail to
        // bind to T&&, and an explicit `<T>` template arg combined with
        // an lvalue would fail to deduce. Pass-by-value + move sidesteps
        // both: T deduces here, value is local, AZStd::move makes it an
        // rvalue ready for StoreInTempData to take ownership of.
        template <typename T>
        void SetArgumentValueInline(AZ::BehaviorArgument& outArg, T value)
        {
            outArg.StoreInTempData(AZStd::move(value));
        }

        bool MarshalIntegerJson(
            const rapidjson::Value& json,
            const AZ::BehaviorParameter& param,
            AZ::BehaviorArgument& outArg,
            AZStd::string& errorOut)
        {
            // BehaviorArgument supports each integer type with a distinct
            // TypeId; we have to match them precisely.
            const bool isInt = json.IsInt() || json.IsInt64() || json.IsUint() || json.IsUint64();
            if (!isInt && !json.IsBool())
            {
                errorOut = "expected integer/bool JSON";
                return false;
            }
            const AZ::s64 sval = json.IsBool()
                ? (json.GetBool() ? 1 : 0)
                : (json.IsInt64() ? json.GetInt64() : static_cast<AZ::s64>(json.GetUint64()));

            if (MatchesType(param, azrtti_typeid<bool>()))            { SetArgumentValueInline(outArg, sval != 0); return true; }
            if (MatchesType(param, azrtti_typeid<AZ::s8>()))          { SetArgumentValueInline(outArg, static_cast<AZ::s8>(sval)); return true; }
            if (MatchesType(param, azrtti_typeid<AZ::u8>()))          { SetArgumentValueInline(outArg, static_cast<AZ::u8>(sval)); return true; }
            if (MatchesType(param, azrtti_typeid<AZ::s16>()))         { SetArgumentValueInline(outArg, static_cast<AZ::s16>(sval)); return true; }
            if (MatchesType(param, azrtti_typeid<AZ::u16>()))         { SetArgumentValueInline(outArg, static_cast<AZ::u16>(sval)); return true; }
            if (MatchesType(param, azrtti_typeid<AZ::s32>()))         { SetArgumentValueInline(outArg, static_cast<AZ::s32>(sval)); return true; }
            if (MatchesType(param, azrtti_typeid<AZ::u32>()))         { SetArgumentValueInline(outArg, static_cast<AZ::u32>(sval)); return true; }
            if (MatchesType(param, azrtti_typeid<AZ::s64>()))         { SetArgumentValueInline(outArg, sval); return true; }
            if (MatchesType(param, azrtti_typeid<AZ::u64>()))         { SetArgumentValueInline(outArg, static_cast<AZ::u64>(sval)); return true; }

            // EntityId is a u64 wrapper - JSON integer maps directly.
            if (MatchesType(param, azrtti_typeid<AZ::EntityId>()))
            {
                SetArgumentValueInline(outArg, AZ::EntityId(static_cast<AZ::u64>(sval)));
                return true;
            }
            // Crc32 is a u32 hash - same direct integer map.
            if (MatchesType(param, azrtti_typeid<AZ::Crc32>()))
            {
                SetArgumentValueInline(outArg, AZ::Crc32(static_cast<AZ::u32>(sval)));
                return true;
            }

            errorOut = AZStd::string::format(
                "JSON integer cannot bind to parameter of type 0x%s",
                param.m_typeId.ToString<AZStd::string>().c_str());
            return false;
        }

        bool MarshalFloatJson(
            const rapidjson::Value& json,
            const AZ::BehaviorParameter& param,
            AZ::BehaviorArgument& outArg,
            AZStd::string& errorOut)
        {
            if (!json.IsNumber())
            {
                errorOut = "expected JSON number";
                return false;
            }
            const double dval = json.GetDouble();

            if (MatchesType(param, azrtti_typeid<float>()))  { SetArgumentValueInline(outArg, static_cast<float>(dval)); return true; }
            if (MatchesType(param, azrtti_typeid<double>())) { SetArgumentValueInline(outArg, dval); return true; }
            // Numeric JSON may also bind to integer params; defer to integer marshaler.
            return MarshalIntegerJson(json, param, outArg, errorOut);
        }

        bool MarshalStringJson(
            const rapidjson::Value& json,
            const AZ::BehaviorParameter& param,
            AZ::BehaviorArgument& outArg,
            StackAllocator& alloc,
            AZStd::string& errorOut)
        {
            if (!json.IsString())
            {
                errorOut = "expected JSON string";
                return false;
            }
            // Uuid - parse the canonical "{XXXX-...}" form. Mirrors the
            // outgoing direction in BehaviorArgumentToJsonValue which
            // serializes Uuids as their string form.
            if (MatchesType(param, azrtti_typeid<AZ::Uuid>()))
            {
                AZStd::string_view sv(json.GetString(), json.GetStringLength());
                AZ::Uuid u = AZ::Uuid::CreateString(sv.data(), sv.size());
                SetArgumentValueInline(outArg, u);
                return true;
            }
            if (!MatchesType(param, azrtti_typeid<AZStd::string>()))
            {
                errorOut = "JSON string cannot bind to non-string parameter";
                return false;
            }
            // BehaviorArgument needs the string to outlive the call. Stack-
            // allocate, copy, point at it.
            AZStd::string* storage = alloc.AllocString();
            storage->assign(json.GetString(), json.GetStringLength());
            outArg.m_typeId = azrtti_typeid<AZStd::string>();
            outArg.m_value = storage;
            outArg.m_traits = AZ::BehaviorParameter::TR_POINTER;
            return true;
        }

        // Math types arrive as JSON arrays: [x, y, z] for Vector3,
        // [x, y, z, w] for Vector4 / Quaternion / Color.
        template <typename TVec, int Components>
        bool MarshalFloatArrayToVector(
            const rapidjson::Value& json,
            const AZ::BehaviorParameter& param,
            AZ::BehaviorArgument& outArg,
            AZStd::string& errorOut)
        {
            if (!MatchesType(param, azrtti_typeid<TVec>()))
            {
                return false; // caller will try the next type
            }
            if (!json.IsArray() || json.Size() != Components)
            {
                errorOut = AZStd::string::format(
                    "expected JSON array of %d numbers", Components);
                return false;
            }
            float vals[4] = { 0, 0, 0, 0 };
            for (rapidjson::SizeType i = 0; i < json.Size(); ++i)
            {
                if (!json[i].IsNumber())
                {
                    errorOut = "expected number in float array";
                    return false;
                }
                vals[i] = static_cast<float>(json[i].GetDouble());
            }
            TVec v;
            if constexpr (Components == 2)
            {
                v = TVec(vals[0], vals[1]);
            }
            else if constexpr (Components == 3)
            {
                v = TVec(vals[0], vals[1], vals[2]);
            }
            else
            {
                v = TVec(vals[0], vals[1], vals[2], vals[3]);
            }
            SetArgumentValueInline(outArg, v);
            return true;
        }
    } // namespace

    StackAllocator::~StackAllocator()
    {
        // Arena-backed objects were placement-new'd into m_arena's raw
        // bytes, so they need an explicit destructor call; their storage
        // itself is reclaimed for free when m_arena (a member array)
        // goes out of scope.
        for (auto* s : m_arenaStrings) { s->~basic_string(); }
        for (auto* b : m_arenaBuffers) { b->~vector(); }

        // Heap-fallback objects own their storage via plain `new` and
        // need both destruction and deallocation.
        for (auto* s : m_heapStrings) { delete s; }
        for (auto* b : m_heapBuffers) { delete b; }
    }

    void* StackAllocator::BumpAlloc(size_t bytes, size_t alignment)
    {
        // Round the current offset up to the requested alignment.
        const size_t misalignment = m_arenaOffset % alignment;
        const size_t alignedOffset = misalignment == 0
            ? m_arenaOffset
            : m_arenaOffset + (alignment - misalignment);

        if (alignedOffset + bytes > kInlineArenaBytes)
        {
            return nullptr; // exhausted - caller falls back to heap
        }

        void* ptr = m_arena + alignedOffset;
        m_arenaOffset = alignedOffset + bytes;
        return ptr;
    }

    AZStd::string* StackAllocator::AllocString()
    {
        if (void* mem = BumpAlloc(sizeof(AZStd::string), alignof(AZStd::string)))
        {
            auto* s = new (mem) AZStd::string();
            m_arenaStrings.push_back(s);
            return s;
        }
        // Arena exhausted for this marshaling pass - fall back to heap,
        // same as the original always-heap implementation.
        m_heapStrings.push_back(new AZStd::string());
        return m_heapStrings.back();
    }

    AZStd::vector<AZ::u8>* StackAllocator::AllocBlittableBuffer(size_t bytes)
    {
        if (void* mem = BumpAlloc(sizeof(AZStd::vector<AZ::u8>), alignof(AZStd::vector<AZ::u8>)))
        {
            // The AZStd::vector<AZ::u8> control block is placed in the
            // arena, but its `bytes`-sized backing storage is still a
            // separate heap allocation performed by the vector itself
            // (AZStd::vector always owns heap storage for its elements -
            // there is no small-buffer optimization to preserve here).
            // What we've saved versus the old implementation is the
            // `new AZStd::vector<AZ::u8>` control-block allocation
            // itself, which is the per-call overhead this task targets.
            auto* buf = new (mem) AZStd::vector<AZ::u8>(bytes);
            m_arenaBuffers.push_back(buf);
            return buf;
        }
        auto* buf = new AZStd::vector<AZ::u8>(bytes);
        m_heapBuffers.push_back(buf);
        return buf;
    }

    bool JsonValueToBehaviorParameter(
        const rapidjson::Value& json,
        const AZ::BehaviorParameter& param,
        AZ::BehaviorArgument& outArg,
        StackAllocator& alloc,
        AZStd::string& errorOut)
    {
        // Reset arg to a clean slate. m_typeId comes from the param spec.
        outArg.m_typeId = param.m_typeId;
        outArg.m_traits = param.m_traits;
        outArg.m_name = param.m_name;

        if (json.IsString())
        {
            return MarshalStringJson(json, param, outArg, alloc, errorOut);
        }
        if (json.IsBool() || json.IsInt() || json.IsInt64() || json.IsUint() || json.IsUint64())
        {
            return MarshalIntegerJson(json, param, outArg, errorOut);
        }
        if (json.IsNumber())
        {
            return MarshalFloatJson(json, param, outArg, errorOut);
        }
        if (json.IsArray())
        {
            // Dispatch on the parameter's reflected type. We try each
            // math-type marshaler in turn; whichever one matches the
            // param's TypeId takes the array. An empty errorOut on a
            // false return means "type didn't match, try the next one";
            // a non-empty errorOut is a shape error (wrong element
            // count, non-number element) that should abort the chain.
            if (MarshalFloatArrayToVector<AZ::Vector2, 2>(json, param, outArg, errorOut)) { return true; }
            if (!errorOut.empty()) { return false; }
            if (MarshalFloatArrayToVector<AZ::Vector3, 3>(json, param, outArg, errorOut)) { return true; }
            if (!errorOut.empty()) { return false; }
            if (MarshalFloatArrayToVector<AZ::Vector4, 4>(json, param, outArg, errorOut)) { return true; }
            if (!errorOut.empty()) { return false; }
            if (MarshalFloatArrayToVector<AZ::Quaternion, 4>(json, param, outArg, errorOut)) { return true; }
            if (!errorOut.empty()) { return false; }
            if (MarshalFloatArrayToVector<AZ::Color, 4>(json, param, outArg, errorOut)) { return true; }
            if (!errorOut.empty()) { return false; }

            // Multi-component types that don't fit the 2/3/4-element
            // pattern: Aabb is 6 floats (min+max), Matrix3x3 is 9,
            // Matrix4x4 is 16, Transform is 10 (position + quaternion +
            // uniform-scale + 2 reserved). Each handler inspects the
            // param's TypeId and the array length; misses fall through
            // with empty errorOut so the chain can keep trying.
            if (MatchesType(param, azrtti_typeid<AZ::Aabb>()) && json.Size() == 6)
            {
                float v[6];
                for (rapidjson::SizeType i = 0; i < 6; ++i)
                {
                    if (!json[i].IsNumber()) { errorOut = "expected number in Aabb array"; return false; }
                    v[i] = static_cast<float>(json[i].GetDouble());
                }
                AZ::Aabb aabb = AZ::Aabb::CreateFromMinMax(
                    AZ::Vector3(v[0], v[1], v[2]),
                    AZ::Vector3(v[3], v[4], v[5]));
                SetArgumentValueInline(outArg, aabb);
                return true;
            }
            if (MatchesType(param, azrtti_typeid<AZ::Matrix3x3>()) && json.Size() == 9)
            {
                float v[9];
                for (rapidjson::SizeType i = 0; i < 9; ++i)
                {
                    if (!json[i].IsNumber()) { errorOut = "expected number in Matrix3x3 array"; return false; }
                    v[i] = static_cast<float>(json[i].GetDouble());
                }
                AZ::Matrix3x3 mat = AZ::Matrix3x3::CreateFromValue(0.f);
                for (int r = 0; r < 3; ++r)
                {
                    for (int c = 0; c < 3; ++c)
                    {
                        mat.SetElement(r, c, v[r * 3 + c]);
                    }
                }
                SetArgumentValueInline(outArg, mat);
                return true;
            }
            if (MatchesType(param, azrtti_typeid<AZ::Matrix4x4>()) && json.Size() == 16)
            {
                float v[16];
                for (rapidjson::SizeType i = 0; i < 16; ++i)
                {
                    if (!json[i].IsNumber()) { errorOut = "expected number in Matrix4x4 array"; return false; }
                    v[i] = static_cast<float>(json[i].GetDouble());
                }
                AZ::Matrix4x4 mat = AZ::Matrix4x4::CreateZero();
                for (int r = 0; r < 4; ++r)
                {
                    for (int c = 0; c < 4; ++c)
                    {
                        mat.SetElement(r, c, v[r * 4 + c]);
                    }
                }
                SetArgumentValueInline(outArg, mat);
                return true;
            }
            if (MatchesType(param, azrtti_typeid<AZ::Transform>()) && json.Size() == 10)
            {
                float v[10];
                for (rapidjson::SizeType i = 0; i < 10; ++i)
                {
                    if (!json[i].IsNumber()) { errorOut = "expected number in Transform array"; return false; }
                    v[i] = static_cast<float>(json[i].GetDouble());
                }
                AZ::Transform tr = AZ::Transform::CreateFromQuaternionAndTranslation(
                    AZ::Quaternion(v[3], v[4], v[5], v[6]),
                    AZ::Vector3(v[0], v[1], v[2]));
                tr.SetUniformScale(v[7]);
                SetArgumentValueInline(outArg, tr);
                return true;
            }

            errorOut = "JSON array doesn't match any supported math type "
                       "(Vector2, Vector3, Vector4, Quaternion, Color, Aabb, "
                       "Matrix3x3, Matrix4x4, Transform)";
            return false;
        }
        if (json.IsNull())
        {
            // Treat as default-construct of the param type. Limited to
            // pointer-typed parameters where null is meaningful.
            if ((param.m_traits & AZ::BehaviorParameter::TR_POINTER) != 0)
            {
                outArg.m_value = nullptr;
                return true;
            }
            errorOut = "JSON null can only bind to pointer parameters";
            return false;
        }

        errorOut = "unsupported JSON value type";
        return false;
    }

    bool BehaviorArgumentToJsonValue(
        const AZ::BehaviorArgument& arg,
        rapidjson::Value& outJson,
        rapidjson::MemoryPoolAllocator<>& alloc,
        AZStd::string& errorOut)
    {
        const AZ::TypeId& t = arg.m_typeId;

        if (t == azrtti_typeid<bool>())   { outJson.SetBool(*arg.GetAsUnsafe<bool>()); return true; }
        if (t == azrtti_typeid<AZ::s8>()) { outJson.SetInt(*arg.GetAsUnsafe<AZ::s8>()); return true; }
        if (t == azrtti_typeid<AZ::u8>()) { outJson.SetInt(*arg.GetAsUnsafe<AZ::u8>()); return true; }
        if (t == azrtti_typeid<AZ::s16>()){ outJson.SetInt(*arg.GetAsUnsafe<AZ::s16>()); return true; }
        if (t == azrtti_typeid<AZ::u16>()){ outJson.SetInt(*arg.GetAsUnsafe<AZ::u16>()); return true; }
        if (t == azrtti_typeid<AZ::s32>()){ outJson.SetInt(*arg.GetAsUnsafe<AZ::s32>()); return true; }
        if (t == azrtti_typeid<AZ::u32>()){ outJson.SetUint(*arg.GetAsUnsafe<AZ::u32>()); return true; }
        if (t == azrtti_typeid<AZ::s64>()){ outJson.SetInt64(*arg.GetAsUnsafe<AZ::s64>()); return true; }
        if (t == azrtti_typeid<AZ::u64>()){ outJson.SetUint64(*arg.GetAsUnsafe<AZ::u64>()); return true; }
        if (t == azrtti_typeid<float>())  { outJson.SetFloat(*arg.GetAsUnsafe<float>()); return true; }
        if (t == azrtti_typeid<double>()) { outJson.SetDouble(*arg.GetAsUnsafe<double>()); return true; }

        if (t == azrtti_typeid<AZStd::string>())
        {
            const auto* s = arg.GetAsUnsafe<AZStd::string>();
            outJson.SetString(s->c_str(), static_cast<rapidjson::SizeType>(s->size()), alloc);
            return true;
        }
        if (t == azrtti_typeid<AZ::EntityId>())
        {
            const auto* id = arg.GetAsUnsafe<AZ::EntityId>();
            outJson.SetUint64(static_cast<AZ::u64>(*id));
            return true;
        }

        // Vec / Quat / Color / Aabb / Matrix all marshal back to JSON
        // arrays. The C# UnpackBareJsonValue / UnpackJsonArray in
        // NativeReflection reconstructs the typed struct from element
        // count (2/3/4 → Vector2/3/4 or Quat depending on the requested
        // T, 6 → Aabb min+max, 9 → Matrix3x3, 16 → Matrix4x4, etc.).
        auto packArr = [&](std::initializer_list<float> values)
        {
            outJson.SetArray();
            for (float v : values)
            {
                outJson.PushBack(v, alloc);
            }
        };

        if (t == azrtti_typeid<AZ::Vector2>())
        {
            const auto* v = arg.GetAsUnsafe<AZ::Vector2>();
            packArr({ v->GetX(), v->GetY() });
            return true;
        }
        if (t == azrtti_typeid<AZ::Vector3>())
        {
            const auto* v = arg.GetAsUnsafe<AZ::Vector3>();
            packArr({ v->GetX(), v->GetY(), v->GetZ() });
            return true;
        }
        if (t == azrtti_typeid<AZ::Vector4>())
        {
            const auto* v = arg.GetAsUnsafe<AZ::Vector4>();
            packArr({ v->GetX(), v->GetY(), v->GetZ(), v->GetW() });
            return true;
        }
        if (t == azrtti_typeid<AZ::Quaternion>())
        {
            const auto* q = arg.GetAsUnsafe<AZ::Quaternion>();
            packArr({ q->GetX(), q->GetY(), q->GetZ(), q->GetW() });
            return true;
        }
        if (t == azrtti_typeid<AZ::Color>())
        {
            const auto* c = arg.GetAsUnsafe<AZ::Color>();
            packArr({ c->GetR(), c->GetG(), c->GetB(), c->GetA() });
            return true;
        }
        if (t == azrtti_typeid<AZ::Aabb>())
        {
            // 6 floats: [minX, minY, minZ, maxX, maxY, maxZ].
            const auto* aabb = arg.GetAsUnsafe<AZ::Aabb>();
            const auto mn = aabb->GetMin();
            const auto mx = aabb->GetMax();
            packArr({ mn.GetX(), mn.GetY(), mn.GetZ(), mx.GetX(), mx.GetY(), mx.GetZ() });
            return true;
        }
        if (t == azrtti_typeid<AZ::Matrix3x3>())
        {
            // 9 floats, row-major flatten.
            const auto* m = arg.GetAsUnsafe<AZ::Matrix3x3>();
            outJson.SetArray();
            for (int r = 0; r < 3; ++r)
            {
                for (int c = 0; c < 3; ++c)
                {
                    outJson.PushBack(m->GetElement(r, c), alloc);
                }
            }
            return true;
        }
        if (t == azrtti_typeid<AZ::Matrix4x4>())
        {
            // 16 floats, row-major flatten.
            const auto* m = arg.GetAsUnsafe<AZ::Matrix4x4>();
            outJson.SetArray();
            for (int r = 0; r < 4; ++r)
            {
                for (int c = 0; c < 4; ++c)
                {
                    outJson.PushBack(m->GetElement(r, c), alloc);
                }
            }
            return true;
        }
        if (t == azrtti_typeid<AZ::Transform>())
        {
            // 10 floats: translation (3) + rotation quaternion (4) + uniform scale (1) + reserved (2).
            // Storing as a flat array lets the C# side reconstruct via
            // Transform.CreateFromQuaternionAndTranslation + SetUniformScale.
            // Slots 8-9 are reserved zeros for forward compat (skew, etc.).
            const auto* tr = arg.GetAsUnsafe<AZ::Transform>();
            const auto pos = tr->GetTranslation();
            const auto rot = tr->GetRotation();
            const float scl = tr->GetUniformScale();
            packArr({
                pos.GetX(), pos.GetY(), pos.GetZ(),
                rot.GetX(), rot.GetY(), rot.GetZ(), rot.GetW(),
                scl, 0.f, 0.f,
            });
            return true;
        }

        // Identifier / hash types - marshal as their underlying numeric
        // representation. C# wrappers can treat these as opaque IDs.
        if (t == azrtti_typeid<AZ::Crc32>())
        {
            const auto* crc = arg.GetAsUnsafe<AZ::Crc32>();
            outJson.SetUint(static_cast<AZ::u32>(*crc));
            return true;
        }
        if (t == azrtti_typeid<AZ::Uuid>())
        {
            // Uuids serialize as their string form ("{XXXX-...}") since
            // there's no efficient numeric carrier for 128 bits in JSON.
            // C# side parses with Guid.Parse.
            const auto* uuid = arg.GetAsUnsafe<AZ::Uuid>();
            const auto uuidStr = uuid->ToString<AZStd::string>();
            outJson.SetString(uuidStr.c_str(),
                static_cast<rapidjson::SizeType>(uuidStr.size()), alloc);
            return true;
        }

        errorOut = AZStd::string::format(
            "unsupported return type 0x%s",
            t.ToString<AZStd::string>().c_str());
        return false;
    }

    bool MarshalJsonArrayToArguments(
        const rapidjson::Value& jsonArray,
        const AZ::BehaviorMethod& method,
        AZStd::vector<AZ::BehaviorArgument>& outArgs,
        StackAllocator& alloc,
        AZStd::string& errorOut,
        size_t skipFirstNParams)
    {
        if (!jsonArray.IsArray())
        {
            errorOut = "args JSON must be an array";
            return false;
        }

        const size_t expected = method.GetNumArguments();
        if (expected < skipFirstNParams)
        {
            errorOut = "skipFirstNParams larger than method arity";
            return false;
        }
        const size_t needed = expected - skipFirstNParams;
        if (jsonArray.Size() != needed)
        {
            errorOut = AZStd::string::format(
                "expected %zu args, got %u", needed, jsonArray.Size());
            return false;
        }

        outArgs.resize(needed);
        for (size_t i = 0; i < needed; ++i)
        {
            const AZ::BehaviorParameter* paramPtr = method.GetArgument(i + skipFirstNParams);
            if (paramPtr == nullptr)
            {
                errorOut = AZStd::string::format("arg %zu has null parameter description", i);
                return false;
            }
            AZStd::string perArgError;
            if (!JsonValueToBehaviorParameter(
                    jsonArray[static_cast<rapidjson::SizeType>(i)],
                    *paramPtr, outArgs[i], alloc, perArgError))
            {
                errorOut = AZStd::string::format(
                    "arg %zu (%s): %s", i,
                    paramPtr->m_name ? paramPtr->m_name : "<unnamed>",
                    perArgError.c_str());
                return false;
            }
        }
        return true;
    }

} // namespace O3DESharp::Marshaling
