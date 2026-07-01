namespace Coral.Managed.Interop
{
    // Test-only placeholder namespace. Debug.cs carries a
    // `using Coral.Managed.Interop;` left over from when its logging calls
    // took NativeString parameters; the current implementation only ever
    // passes plain strings, so no type from this namespace is actually
    // referenced. Without the real Coral.Managed.dll reference (see the
    // csproj comment on why O3DE.Core.dll isn't linked directly), the
    // `using` would otherwise fail to resolve with CS0246. This empty
    // namespace declaration is enough to satisfy the compiler.
}
