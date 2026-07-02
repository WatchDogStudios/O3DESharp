namespace O3DE
{
    /// <summary>
    /// Minimal stand-in for O3DE.Core's Transform, used only so that Entity.cs's
    /// `Transform Transform => m_transform ??= new Transform(this);` cached
    /// property (and the `internal Transform(Entity entity)` constructor it
    /// calls) can compile in this test project. The real Transform.cs pulls in
    /// a dozen more InternalCalls.Transform_* delegate* fields plus Quaternion
    /// math not needed here - no test in this project exercises Transform
    /// behavior, only Entity.GetChildren() (see EntityGetChildrenTests.cs), so
    /// this stub only needs to satisfy the compile-time constructor call.
    /// </summary>
    public class Transform
    {
        private readonly Entity m_entity;

        internal Transform(Entity entity)
        {
            m_entity = entity;
        }

        public Entity Entity => m_entity;
    }
}
