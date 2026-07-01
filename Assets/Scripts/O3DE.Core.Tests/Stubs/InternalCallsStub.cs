using System.Collections.Generic;
using Coral.Managed.Interop;

namespace O3DE
{
    /// <summary>
    /// Test-only replacement for O3DE.Core's InternalCalls. The real
    /// InternalCalls is an internal static unsafe class holding
    /// delegate* unmanaged function pointers that Coral only populates
    /// when hosting a live .NET runtime inside the O3DE process -
    /// invoking one outside that host is a hard native crash, not a
    /// catchable exception. This stub defines a same-named type with
    /// ordinary managed methods instead, so Debug.cs / Debugger.cs bind
    /// to it when compiled into this test assembly (the real
    /// InternalCalls.cs from O3DE.Core is never linked in here).
    /// </summary>
    internal static class InternalCalls
    {
        public static readonly List<string> InfoLogs = new();
        public static readonly List<string> WarningLogs = new();
        public static readonly List<string> ErrorLogs = new();

        public static void Reset()
        {
            InfoLogs.Clear();
            WarningLogs.Clear();
            ErrorLogs.Clear();
            ValidEntities.Clear();
            ChildrenByEntity.Clear();
            GetChildrenCallCount = 0;
        }

        public static void Log_Info(string message) => InfoLogs.Add(message);
        public static void Log_Warning(string message) => WarningLogs.Add(message);
        public static void Log_Error(string message) => ErrorLogs.Add(message);

        // ------------------------------------------------------------
        // Entity hierarchy fixture - lets tests set up parent/child
        // relationships and validity without a live Coral host. Keyed by
        // entity ID.
        // ------------------------------------------------------------
        internal static readonly HashSet<ulong> ValidEntities = new();
        internal static readonly Dictionary<ulong, List<ulong>> ChildrenByEntity = new();
        internal static int GetChildrenCallCount;

        internal static Bool32 Entity_IsValid(ulong entityId)
        {
            return ValidEntities.Contains(entityId);
        }

        internal static NativeString Entity_GetName(ulong entityId) => string.Empty;
        internal static void Entity_SetName(ulong entityId, NativeString name) { }
        internal static Bool32 Entity_IsActive(ulong entityId) => ValidEntities.Contains(entityId);
        internal static void Entity_Activate(ulong entityId) { }
        internal static void Entity_Deactivate(ulong entityId) { }
        internal static void Entity_Destroy(ulong entityId) { }
        internal static ulong Entity_FindByName(NativeString name) => Entity.InvalidId;
        internal static Bool32 Component_HasComponent(ulong entityId, NativeString componentTypeName) => false;

        internal static int Entity_GetChildCount(ulong entityId)
        {
            return ChildrenByEntity.TryGetValue(entityId, out var kids) ? kids.Count : 0;
        }

        internal static unsafe int Entity_GetChildren(ulong entityId, ulong* outBuffer, int bufferCapacity)
        {
            GetChildrenCallCount++;
            if (outBuffer == null || bufferCapacity <= 0)
            {
                return 0;
            }
            if (!ChildrenByEntity.TryGetValue(entityId, out var kids))
            {
                return 0;
            }

            int writeCount = System.Math.Min(kids.Count, bufferCapacity);
            for (int i = 0; i < writeCount; i++)
            {
                outBuffer[i] = kids[i];
            }
            return writeCount;
        }
    }
}
