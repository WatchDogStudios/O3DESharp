/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

#include "NativeBindingManifest.h"

#include <AzCore/Console/ILogger.h>
#include <AzCore/IO/FileIO.h>
#include <AzCore/IO/SystemFile.h>

// -----------------------------------------------------------------------
// JSON schema emitted by NativeBindingManifestExporter::ExportToString.
// This is the input contract for
// Code/Tools/BindingGenerator/O3DESharp.BindingGenerator/Configuration/
// NativeBindingManifestSchema.cs (the C# POCO mirror) - keep the two in
// lockstep the same way ReflectionDataSchema.cs mirrors
// ReflectionDataExporter's output.
//
// {
//   "methods": [
//     {
//       "reflected_name": "GetLength",
//       "owning_class_name": "Vector3",
//       "owning_class_type_id": "{...}",
//       "owning_class_size_bytes": 16,
//       "owning_class_align_bytes": 16,
//       "is_static": false,
//       "is_const": true,
//       "native_qualified_symbol": "",       // always empty here; filled by the offline clang join
//       "return": { "cpp_type_name": "float", "storage_class": "Value", "size_bytes": 4, "align_bytes": 4 },
//       "arguments": [ { "name": "arg0", "cpp_type_name": "...", "storage_class": "ConstReference", ... } ],
//       "bindable": false,
//       "non_bindable_reason": "NoNativeSideCounterpart",
//       "binding_id": "Vector3::GetLength"
//     }
//   ]
// }
// -----------------------------------------------------------------------

namespace O3DESharp
{
    AZStd::string_view ToString(ArgStorageClass storageClass)
    {
        switch (storageClass)
        {
        case ArgStorageClass::Value:          return "Value";
        case ArgStorageClass::Pointer:        return "Pointer";
        case ArgStorageClass::Reference:      return "Reference";
        case ArgStorageClass::ConstReference: return "ConstReference";
        case ArgStorageClass::Unknown:
        default:
            return "Unknown";
        }
    }

    AZStd::string_view ToString(NonBindableReason reason)
    {
        switch (reason)
        {
        case NonBindableReason::None:                  return "None";
        case NonBindableReason::Overloaded:             return "Overloaded";
        case NonBindableReason::ReflectedViaLambda:      return "ReflectedViaLambda";
        case NonBindableReason::OnDemandTemplateType:    return "OnDemandTemplateType";
        case NonBindableReason::EBusAddressedById:       return "EBusAddressedById";
        case NonBindableReason::UnresolvedNativeSymbol:  return "UnresolvedNativeSymbol";
        case NonBindableReason::UnsupportedArgStorage:   return "UnsupportedArgStorage";
        case NonBindableReason::NoNativeSideCounterpart:
        default:
            return "NoNativeSideCounterpart";
        }
    }

    AZStd::string ManifestMethodEntry::BindingId() const
    {
        if (owningClassName.empty())
        {
            return AZStd::string::format("::%s", reflectedName.c_str());
        }
        return AZStd::string::format("%s::%s", owningClassName.c_str(), reflectedName.c_str());
    }

    size_t NativeBindingManifest::BindableCount() const
    {
        size_t count = 0;
        for (const auto& m : methods)
        {
            if (m.bindable)
            {
                ++count;
            }
        }
        return count;
    }

    // ============================================================
    // NativeBindingManifestExporter
    // ============================================================

    ManifestArgument NativeBindingManifestExporter::BuildManifestArgument(const ReflectedParameter& param) const
    {
        ManifestArgument arg;
        arg.name = param.name;
        arg.cppTypeName = param.typeName;
        arg.typeId = param.typeId;

        // Storage-class inference from the same traits BehaviorContext
        // already exposes (BehaviorParameter::TR_POINTER / TR_REFERENCE /
        // TR_CONST - see BehaviorContextReflector::ReflectParameter, which
        // is where ReflectedParameter's isPointer/isReference/isConst
        // booleans come from). No new information is needed here; this is
        // purely re-projecting three booleans into one enum so the
        // trampoline emitter has a single switch to write instead of a
        // three-way if/else at every call site.
        if (param.isPointer)
        {
            arg.storageClass = ArgStorageClass::Pointer;
        }
        else if (param.isReference)
        {
            arg.storageClass = param.isConst ? ArgStorageClass::ConstReference : ArgStorageClass::Reference;
        }
        else
        {
            arg.storageClass = ArgStorageClass::Value;
        }

        // Size/align: BehaviorContext's AZ::BehaviorParameter does not
        // itself carry size/align (that lives on AZ::BehaviorClass for
        // reflected classes, or is implicit for built-in scalar types).
        // Rather than guess for builtins here (float/int/etc. sizes are
        // obvious but duplicating a type table is exactly the kind of
        // thing that silently drifts), size/align for arguments is left
        // at 0/0 in this pass and is filled in by the offline clang join
        // for any argument type the parser also visited as a class. This
        // matches the class-level precedent below (owningClassSizeBytes),
        // which DOES have a size because AZ::BehaviorClass::m_size exists
        // for the *owning* class specifically.
        return arg;
    }

    ManifestMethodEntry NativeBindingManifestExporter::BuildManifestMethod(
        const ReflectedMethod& method,
        const ReflectedClass* owningClass) const
    {
        ManifestMethodEntry entry;
        entry.reflectedName = method.name;
        entry.owningClassName = method.className;
        entry.isStatic = method.isStatic;
        entry.isConst = method.isConst;

        if (owningClass != nullptr)
        {
            entry.owningClassTypeId = owningClass->typeId;
            if (owningClass->behaviorClass != nullptr)
            {
                entry.owningClassSizeBytes = owningClass->behaviorClass->m_size;
                entry.owningClassAlignBytes = owningClass->behaviorClass->m_alignment;
            }
        }

        // Return type.
        entry.returnValue = BuildManifestArgument(method.returnType);
        if (method.returnType.marshalType == ReflectedParameter::MarshalType::Void)
        {
            // Void return isn't a "value" to unpack - the trampoline
            // emitter checks cppTypeName=="void" (see class doc) rather
            // than a separate hasResult bool, mirroring how
            // AZ::BehaviorMethod::HasResult() already works.
            entry.returnValue.storageClass = ArgStorageClass::Value;
        }

        // Arguments (BehaviorContextReflector::ReflectMethod already
        // strips the synthetic 'this' parameter for member methods - see
        // its startIndex = method->IsMember() ? 1 : 0 - so
        // method.parameters here is exactly the trampoline's real
        // argument list; the 'this' pointer is handled separately by the
        // emitter using owningClassTypeId).
        entry.arguments.reserve(method.parameters.size());
        for (const auto& param : method.parameters)
        {
            entry.arguments.push_back(BuildManifestArgument(param));
        }

        // Bindability: this pass never sets bindable=true. Doing so
        // requires knowing (a) whether the reflected name is unique
        // (overload detection - needs sibling methods, which the offline
        // generator has full visibility into per-class), (b) whether a
        // clang-recovered native symbol exists at all, and (c) whether
        // every argument's storage class actually resolved
        // (ArgStorageClass::Unknown anywhere forces non-bindable). All
        // three require the join; see NativeBindingGenerator.cs
        // (BindingClassifier) for where bindable actually gets flipped.
        entry.bindable = false;
        entry.nonBindableReason = NonBindableReason::NoNativeSideCounterpart;

        return entry;
    }

    NativeBindingManifest NativeBindingManifestExporter::BuildManifest(const BehaviorContextReflector& reflector) const
    {
        NativeBindingManifest manifest;

        // Member methods (both instance and static) of every reflected class.
        for (const AZStd::string& className : reflector.GetClassNames())
        {
            const ReflectedClass* cls = reflector.GetClass(className);
            if (cls == nullptr)
            {
                continue;
            }
            manifest.methods.reserve(manifest.methods.size() + cls->methods.size());
            for (const ReflectedMethod& method : cls->methods)
            {
                manifest.methods.push_back(BuildManifestMethod(method, cls));
            }
            // Constructors are intentionally excluded from the v1 manifest:
            // a constructor trampoline needs placement-new-into-caller-
            // storage semantics (there's no BehaviorObject 'this' to bind
            // against yet, it's what's being created) which is a different
            // emitter shape than "call a member/static function". Deferred
            // to a later phase per the spec's "grow incrementally" pattern.
        }

        // Global methods.
        for (const ReflectedMethod& method : reflector.GetGlobalMethods())
        {
            manifest.methods.push_back(BuildManifestMethod(method, nullptr));
        }

        return manifest;
    }

    NativeBindingManifest NativeBindingManifestExporter::BuildManifestFromContext(AZ::BehaviorContext* context) const
    {
        if (context == nullptr)
        {
            AZLOG_ERROR("NativeBindingManifestExporter: Cannot build from null BehaviorContext");
            return NativeBindingManifest();
        }

        BehaviorContextReflector reflector;
        reflector.ReflectFromContext(context);
        return BuildManifest(reflector);
    }

    AZStd::string NativeBindingManifestExporter::Indent(int level, int indentSize) const
    {
        if (indentSize <= 0 || level <= 0)
        {
            return "";
        }
        return AZStd::string(static_cast<size_t>(level * indentSize), ' ');
    }

    AZStd::string NativeBindingManifestExporter::EscapeJsonString(const AZStd::string& input) const
    {
        AZStd::string result;
        result.reserve(input.size() + 16);
        for (char c : input)
        {
            switch (c)
            {
            case '"':  result += "\\\""; break;
            case '\\': result += "\\\\"; break;
            case '\b': result += "\\b"; break;
            case '\f': result += "\\f"; break;
            case '\n': result += "\\n"; break;
            case '\r': result += "\\r"; break;
            case '\t': result += "\\t"; break;
            default:
                if (static_cast<unsigned char>(c) < 0x20)
                {
                    char buf[8];
                    azsnprintf(buf, sizeof(buf), "\\u%04x", static_cast<unsigned int>(c));
                    result += buf;
                }
                else
                {
                    result += c;
                }
                break;
            }
        }
        return result;
    }

    AZStd::string NativeBindingManifestExporter::GenerateArgumentJson(
        const ManifestArgument& arg, int indentSize, int indentLevel) const
    {
        const AZStd::string ind = Indent(indentLevel, indentSize);
        AZStd::string json = ind + "{ ";
        json += "\"name\": \"" + EscapeJsonString(arg.name) + "\", ";
        json += "\"cpp_type_name\": \"" + EscapeJsonString(arg.cppTypeName) + "\", ";
        json += "\"type_id\": \"" + AZStd::string(arg.typeId.ToFixedString().c_str()) + "\", ";
        json += "\"storage_class\": \"" + AZStd::string(ToString(arg.storageClass)) + "\", ";
        json += "\"size_bytes\": " + AZStd::string::format("%zu", arg.sizeBytes) + ", ";
        json += "\"align_bytes\": " + AZStd::string::format("%zu", arg.alignBytes);
        json += " }";
        return json;
    }

    AZStd::string NativeBindingManifestExporter::GenerateMethodJson(
        const ManifestMethodEntry& method, int indentSize, int indentLevel) const
    {
        const AZStd::string nl = indentSize > 0 ? "\n" : "";
        const AZStd::string ind = Indent(indentLevel, indentSize);
        const AZStd::string ind1 = Indent(indentLevel + 1, indentSize);

        AZStd::string json;
        json += ind + "{" + nl;
        json += ind1 + "\"reflected_name\": \"" + EscapeJsonString(method.reflectedName) + "\"," + nl;
        json += ind1 + "\"owning_class_name\": \"" + EscapeJsonString(method.owningClassName) + "\"," + nl;
        json += ind1 + "\"owning_class_type_id\": \"" + AZStd::string(method.owningClassTypeId.ToFixedString().c_str()) + "\"," + nl;
        json += ind1 + "\"owning_class_size_bytes\": " + AZStd::string::format("%zu", method.owningClassSizeBytes) + "," + nl;
        json += ind1 + "\"owning_class_align_bytes\": " + AZStd::string::format("%zu", method.owningClassAlignBytes) + "," + nl;
        json += ind1 + "\"is_static\": " + (method.isStatic ? "true" : "false") + "," + nl;
        json += ind1 + "\"is_const\": " + (method.isConst ? "true" : "false") + "," + nl;
        json += ind1 + "\"native_qualified_symbol\": \"" + EscapeJsonString(method.nativeQualifiedSymbol) + "\"," + nl;
        json += ind1 + "\"return\": " + GenerateArgumentJson(method.returnValue, 0, 0) + "," + nl;

        json += ind1 + "\"arguments\": [" + nl;
        for (size_t i = 0; i < method.arguments.size(); ++i)
        {
            if (i > 0) json += "," + nl;
            json += GenerateArgumentJson(method.arguments[i], indentSize, indentLevel + 2);
        }
        json += nl + ind1 + "]," + nl;

        json += ind1 + "\"bindable\": " + (method.bindable ? "true" : "false") + "," + nl;
        json += ind1 + "\"non_bindable_reason\": \"" + AZStd::string(ToString(method.nonBindableReason)) + "\"," + nl;
        json += ind1 + "\"binding_id\": \"" + EscapeJsonString(method.BindingId()) + "\"" + nl;
        json += ind + "}";
        return json;
    }

    AZStd::string NativeBindingManifestExporter::ExportToString(const NativeBindingManifest& manifest, bool prettyPrint) const
    {
        const int indentSize = prettyPrint ? 2 : 0;
        const AZStd::string nl = prettyPrint ? "\n" : "";

        AZStd::string json;
        json.reserve(64 * 1024);
        json += "{" + nl;
        json += Indent(1, indentSize) + "\"methods\": [" + nl;
        for (size_t i = 0; i < manifest.methods.size(); ++i)
        {
            if (i > 0) json += "," + nl;
            json += GenerateMethodJson(manifest.methods[i], indentSize, 2);
        }
        json += nl + Indent(1, indentSize) + "]" + nl;
        json += "}" + nl;
        return json;
    }

    bool NativeBindingManifestExporter::ExportToFile(const NativeBindingManifest& manifest, const AZ::IO::Path& outputPath) const
    {
        const AZStd::string json = ExportToString(manifest, true);

        AZ::IO::Path parentPath = outputPath.ParentPath();
        if (!parentPath.empty())
        {
            AZ::IO::FileIOBase* fileIO = AZ::IO::FileIOBase::GetInstance();
            if (fileIO && !fileIO->Exists(parentPath.c_str()) && !fileIO->CreatePath(parentPath.c_str()))
            {
                AZLOG_ERROR("NativeBindingManifestExporter: Failed to create directory: %s", parentPath.c_str());
                return false;
            }
        }

        AZ::IO::SystemFile file;
        if (!file.Open(outputPath.c_str(),
            AZ::IO::SystemFile::SF_OPEN_CREATE |
            AZ::IO::SystemFile::SF_OPEN_WRITE_ONLY |
            AZ::IO::SystemFile::SF_OPEN_CREATE_PATH))
        {
            AZLOG_ERROR("NativeBindingManifestExporter: Failed to open file for writing: %s", outputPath.c_str());
            return false;
        }

        const AZ::IO::SizeType written = file.Write(json.data(), json.size());
        file.Close();

        if (written != json.size())
        {
            AZLOG_ERROR("NativeBindingManifestExporter: Failed to write all data to file: %s", outputPath.c_str());
            return false;
        }

        AZLOG_INFO("NativeBindingManifestExporter: Wrote %zu bytes (%zu methods, %zu bindable) to %s",
            written, manifest.TotalCount(), manifest.BindableCount(), outputPath.c_str());
        return true;
    }

} // namespace O3DESharp
