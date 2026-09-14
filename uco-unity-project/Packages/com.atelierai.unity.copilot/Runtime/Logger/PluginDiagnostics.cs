/*
┌──────────────────────────────────────────────────────────────────┐
│  Collector-only channel for plugin/bridge/tool diagnostics       │
│  (COCli-07): the Unity Editor Console stays reserved for product │
│  and Unity output by default; a setting can re-enable mirroring.  │
└──────────────────────────────────────────────────────────────────┘
*/

#nullable enable
using System;
using System.Threading;
using com.AtelierAI.Unity.Copilot;
using com.AtelierAI.Unity.Copilot.Runtime.Utils;
using com.IvanMurzak.ReflectorNet.Utils;

namespace com.AtelierAI.Unity.Copilot.Utils
{
    public sealed class PluginDiagnosticEntry
    {
        public string Source { get; set; } = LogEntry.SourceBridge;
        public UnityEngine.LogType LogType { get; set; } = UnityEngine.LogType.Log;
        public string Message { get; set; } = string.Empty;
        public string? StackTrace { get; set; }
        public string? CorrelationId { get; set; }
        public string? OperationId { get; set; }
    }

    /// <summary>
    /// Sink-based diagnostics channel for entries the plugin itself emits
    /// (framework routing, tool runners, tool wrappers). When a sink is
    /// installed the entries go to the plugin's log collector only — never to
    /// the Unity Console — so infrastructure noise cannot mix with product
    /// output. Without a sink (player builds, pre-initialization) the previous
    /// Debug.Log behavior is kept.
    /// </summary>
    public static class PluginDiagnostics
    {
        static Action<PluginDiagnosticEntry>? s_sink;
        static int s_dropped;

        /// <summary>
        /// Diagnosis toggle: when true, channel entries are also mirrored to
        /// the Unity Console. Default false — the Console stays clean.
        /// </summary>
        public static bool MirrorToConsole;

        public static void Install(Action<PluginDiagnosticEntry>? sink)
            => Interlocked.Exchange(ref s_sink, sink);

        /// <summary>The sink installed right now (null when the channel is uninstalled).</summary>
        public static Action<PluginDiagnosticEntry>? CurrentSink => Volatile.Read(ref s_sink);

        public static bool HasSink => Volatile.Read(ref s_sink) != null;

        /// <summary>
        /// Restore a previously captured sink, but only while <paramref name="mine"/>
        /// is still the installed one: a temporary collector (diagnostics/console
        /// tests) returns the channel to the sink that was there before it —
        /// usually the plugin's own collector — instead of clearing it, while a
        /// newer install is never clobbered by a late (e.g. finalizer) restore.
        /// </summary>
        public static void RestoreIfCurrent(
            Action<PluginDiagnosticEntry>? mine,
            Action<PluginDiagnosticEntry>? previous)
        {
            while (true)
            {
                var current = Volatile.Read(ref s_sink);
                if (!ReferenceEquals(current, mine))
                    return;
                if (Interlocked.CompareExchange(ref s_sink, previous, current) == current)
                    return;
            }
        }

        /// <summary>Entries the channel dropped (sink missing/failed), for loss reporting.</summary>
        public static int DroppedEntries => Volatile.Read(ref s_dropped);

        public static void ResetLossAccounting()
            => Interlocked.Exchange(ref s_dropped, 0);

        public static void Append(
            string source,
            UnityEngine.LogType logType,
            string message,
            string? stackTrace = null)
        {
            if (string.IsNullOrEmpty(message)) return;

            var frame = MainThread.Instance.IsMainThread ? LogCallScope.Current : null;
            var entry = new PluginDiagnosticEntry
            {
                Source = source,
                LogType = logType,
                Message = message,
                StackTrace = stackTrace,
                CorrelationId = frame?.CorrelationId,
                OperationId = frame?.OperationId,
            };

            var sink = Volatile.Read(ref s_sink);
            if (sink == null)
            {
                // Pre-initialization or player build: keep the legacy console
                // path so early diagnostics remain visible.
                EmitToConsole(entry);
                return;
            }

            try
            {
                sink(entry);
                if (MirrorToConsole)
                    EmitToConsole(entry);
            }
            catch
            {
                Interlocked.Increment(ref s_dropped);
            }
        }

        static void EmitToConsole(PluginDiagnosticEntry entry)
        {
            switch (entry.LogType)
            {
                case UnityEngine.LogType.Error:
                case UnityEngine.LogType.Exception:
                case UnityEngine.LogType.Assert:
                    UnityEngine.Debug.LogError(entry.Message);
                    break;
                case UnityEngine.LogType.Warning:
                    UnityEngine.Debug.LogWarning(entry.Message);
                    break;
                default:
                    UnityEngine.Debug.Log(entry.Message);
                    break;
            }
        }
    }
}
