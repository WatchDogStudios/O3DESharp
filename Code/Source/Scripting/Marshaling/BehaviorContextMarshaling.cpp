/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

#include "BehaviorContextMarshaling.h"

#include <AzCore/Component/EntityId.h>
#include <AzCore/Math/Vector3.h>
#include <AzCore/Math/Vector4.h>
#include <AzCore/Math/Quaternion.h>
#include <AzCore/Math/Color.h>
#include <AzCore/Math/Transform.h>
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
        template <typename T>
        void SetArgumentValueInline(AZ::BehaviorArgument& outArg, const T& value)
        {
            // BehaviorArgument has a small inline buffer for blittable
            // value types. StoreInTempData is the public API for that.
            outArg.StoreInTempData<T>(value);
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
            if constexpr (Components == 3)
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
        for (auto* s : m_strings) { delete s; }
        for (auto* b : m_buffers) { delete b; }
    }

    AZStd::string* StackAllocator::AllocString()
    {
        m_strings.push_back(new AZStd::string());
        return m_strings.back();
    }

    AZStd::vector<AZ::u8>* StackAllocator::AllocBlittableBuffer(size_t bytes)
    {
        auto* buf = new AZStd::vector<AZ::u8>(bytes);
        m_buffers.push_back(buf);
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
            if (MarshalFloatArrayToVector<AZ::Vector3, 3>(json, param, outArg, errorOut)) { return true; }
            if (errorOut.empty() == false) { return false; } // shape error
            if (MarshalFloatArrayToVector<AZ::Vector4, 4>(json, param, outArg, errorOut)) { return true; }
            if (errorOut.empty() == false) { return false; }
            if (MarshalFloatArrayToVector<AZ::Quaternion, 4>(json, param, outArg, errorOut)) { return true; }
            if (errorOut.empty() == false) { return false; }
            if (MarshalFloatArrayToVector<AZ::Color, 4>(json, param, outArg, errorOut)) { return true; }
            errorOut = "JSON array doesn't match any supported math type "
                       "(Vector3, Vector4, Quaternion, Color)";
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

        // Vec / Quat / Color marshal back to JSON arrays.
        auto packVec = [&](float x, float y, float z, float w, bool hasW)
        {
            outJson.SetArray();
            outJson.PushBack(x, alloc);
            outJson.PushBack(y, alloc);
            outJson.PushBack(z, alloc);
            if (hasW) { outJson.PushBack(w, alloc); }
        };
        if (t == azrtti_typeid<AZ::Vector3>())
        {
            const auto* v = arg.GetAsUnsafe<AZ::Vector3>();
            packVec(v->GetX(), v->GetY(), v->GetZ(), 0.f, false);
            return true;
        }
        if (t == azrtti_typeid<AZ::Vector4>())
        {
            const auto* v = arg.GetAsUnsafe<AZ::Vector4>();
            packVec(v->GetX(), v->GetY(), v->GetZ(), v->GetW(), true);
            return true;
        }
        if (t == azrtti_typeid<AZ::Quaternion>())
        {
            const auto* q = arg.GetAsUnsafe<AZ::Quaternion>();
            packVec(q->GetX(), q->GetY(), q->GetZ(), q->GetW(), true);
            return true;
        }
        if (t == azrtti_typeid<AZ::Color>())
        {
            const auto* c = arg.GetAsUnsafe<AZ::Color>();
            packVec(c->GetR(), c->GetG(), c->GetB(), c->GetA(), true);
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
