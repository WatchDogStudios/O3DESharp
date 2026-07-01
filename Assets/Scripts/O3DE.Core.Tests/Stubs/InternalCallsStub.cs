using System.Collections.Generic;

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
        }

        public static void Log_Info(string message) => InfoLogs.Add(message);
        public static void Log_Warning(string message) => WarningLogs.Add(message);
        public static void Log_Error(string message) => ErrorLogs.Add(message);
    }
}
