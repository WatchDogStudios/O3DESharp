/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

#pragma once

/**
 * O3DE_EXPORT_CSHARP Attribute
 * 
 * This attribute marks C++ declarations for export to C# via the binding generator.
 * When used with --require-attribute mode, only declarations with this attribute
 * will be exported.
 * 
 * Usage:
 *   class O3DE_EXPORT_CSHARP MyClass { ... };
 *   O3DE_EXPORT_CSHARP void MyFunction();
 * 
 * The attribute is implemented as a Clang annotation that can be detected during
 * parsing. It has no runtime overhead and is ignored by non-Clang compilers.
 * 
 * Note: As of the current version, the default mode does NOT require this attribute.
 * All public declarations are exported by default. Use this attribute only if you
 * enable the --require-attribute flag.
 */

#if defined(__clang__)
    #define O3DE_EXPORT_CSHARP __attribute__((annotate("export_csharp")))
#else
    #define O3DE_EXPORT_CSHARP
#endif
