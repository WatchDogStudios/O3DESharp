namespace O3DE
{
    /// <summary>
    /// Minimal stand-in for O3DE.Entity, used only so that
    /// NativeReflection.cs's `Entity e => e.Id` switch arm (in
    /// SerializeArgumentToObject) and its `SendEBusEvent(..., Entity entity, ...)`
    /// parameter type can compile in this test project. The real Entity.cs pulls
    /// in Transform.cs and a dozen InternalCalls.Entity_*/Transform_*/Component_*
    /// delegate* fields not stubbed here - none of that is needed since no test
    /// in this project constructs a real Entity; only NativeReflection.cs's
    /// compile-time type reference needs satisfying.
    /// </summary>
    /// <remarks>
    /// Deliberately has no parameterless constructor: the real Entity.Id is
    /// backed by a live native entity handle, so a silent default of 0 here
    /// would look like a valid entity to SerializeArgumentToObject's
    /// `Entity e => e.Id` arm. Any test that needs an Entity-shaped value
    /// must construct one with an explicit Id via <see cref="Entity(ulong)"/>.
    /// </remarks>
    public class Entity
    {
        public ulong Id { get; }

        public Entity(ulong id) => Id = id;
    }
}
