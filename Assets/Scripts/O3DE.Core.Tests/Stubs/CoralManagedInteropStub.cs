namespace Coral.Managed.Interop
{
    // Test-only placeholder namespace. Debug.cs has an unused
    // `using Coral.Managed.Interop;` directive (nothing in Debug.cs
    // itself names a type from that namespace - NativeString is only
    // referenced by InternalCalls.cs, which we don't compile into this
    // project). Even so, a `using` for a namespace that doesn't exist
    // among referenced assemblies fails to resolve with CS0246 - without
    // the real Coral.Managed.dll reference (see the csproj comment on
    // why O3DE.Core.dll isn't linked directly), that's exactly our
    // situation. This empty namespace declaration exists purely to give
    // the directive something to resolve to; it is never otherwise used.
}
