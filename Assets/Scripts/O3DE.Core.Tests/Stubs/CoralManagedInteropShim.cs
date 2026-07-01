namespace Coral.Managed.Interop
{
    /// <summary>
    /// Minimal stand-in for Coral.Managed.Interop.NativeString, used only so that
    /// NativeReflection.cs (which declares delegate* unmanaged&lt;NativeString, ...&gt;
    /// fields on ReflectionInternalCalls) can compile in this test project without the
    /// real Coral.Managed.dll, which is a native-build artifact not present on a bare
    /// checkout. Those delegate* fields are never populated or invoked by these tests -
    /// only the pure-managed SerializeArgumentToObject method is under test - so this
    /// shim only needs to satisfy the implicit string conversions NativeReflection.cs
    /// actually uses. It carries no other behavior.
    /// </summary>
    internal readonly struct NativeString
    {
        private readonly string? _value;

        private NativeString(string? value) => _value = value;

        public static implicit operator NativeString(string? value) => new(value);
        public static implicit operator string?(NativeString value) => value._value;

        public override string ToString() => _value ?? string.Empty;
    }

    /// <summary>
    /// Minimal stand-in for Coral.Managed.Interop.Bool32, used only so that
    /// NativeReflection.cs's ReflectionInternalCalls delegate* fields (e.g.
    /// one returning Bool32, used directly as a C# bool) can compile in this
    /// test project. See NativeString above for why this exists.
    /// </summary>
    internal readonly struct Bool32
    {
        private readonly int _value;

        private Bool32(int value) => _value = value;

        public static implicit operator Bool32(bool value) => new(value ? 1 : 0);
        public static implicit operator bool(Bool32 value) => value._value != 0;
    }
}
