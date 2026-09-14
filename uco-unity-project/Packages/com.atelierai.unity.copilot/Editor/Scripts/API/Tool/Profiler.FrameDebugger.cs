/*
┌──────────────────────────────────────────────────────────────────┐
│  Adapted from MCP for Unity (https://github.com/CoplayDev/unity-mcp)
│  Copyright (c) 2025 Coplay Inc.                                  │
│  Licensed under the MIT License.                                 │
│  See the LICENSE file in the project root for more information.  │
└──────────────────────────────────────────────────────────────────┘
┌──────────────────────────────────────────────────────────────────┐
│  Author: Ivan Murzak (https://github.com/IvanMurzak)             │
│  Repository: GitHub (https://github.com/IvanMurzak/Unity-MCP)    │
│  Copyright (c) 2025 Ivan Murzak                                  │
│  Licensed under the Apache License, Version 2.0.                 │
│  See the LICENSE file in the project root for more information.  │
└──────────────────────────────────────────────────────────────────┘
*/

#nullable enable
using AIGD;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using com.AtelierAI.Uco.Framework;
using com.IvanMurzak.ReflectorNet.Utils;
using UnityEditor;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_Profiler
    {
        public const string FrameDebuggerEnableToolId = "frame-debugger-enable";
        [UcoTool
        (
            FrameDebuggerEnableToolId,
            Title = "Frame Debugger / Enable",
            DestructiveHint = true,
            Enabled = false
        )]
        [UcoSkillDescription("Open the Frame Debugger window and enable capture. The whole tool is implemented " +
            "through reflection on `UnityEditorInternal.FrameDebuggerUtility` (and its Unity 6+ namespaced variant) " +
            "because the API is internal.")]
        [UcoSkillBody("Open `Window/Analysis/Frame Debugger` (capture only works while it is open), then " +
            "invoke `FrameDebuggerUtility.SetEnabled(true)` via reflection. When the Editor is in Play mode, the " +
            "game must be paused first — otherwise this tool throws with a clear instruction. Returns the current " +
            "event count.")]
        [Description("Enable the Unity Frame Debugger via reflection on FrameDebuggerUtility.")]
        public FrameDebuggerStatusResult EnableFrameDebugger(string? nothing = null)
        {
            return MainThread.Instance.Run(() =>
            {
                EnsureFrameDebuggerReflectionAvailable();

                EditorApplication.ExecuteMenuItem("Window/Analysis/Frame Debugger");

                if (EditorApplication.isPlaying && !EditorApplication.isPaused)
                    throw new InvalidOperationException(
                        "Game must be paused before enabling Frame Debugger. " +
                        "Call 'editor-application-set-state' with isPaused=true first, then retry 'frame-debugger-enable'.");

                InvokeSetEnabled(true);

                return new FrameDebuggerStatusResult
                {
                    Enabled = true,
                    EventCount = ReadEventCount()
                };
            });
        }

        public const string FrameDebuggerDisableToolId = "frame-debugger-disable";
        [UcoTool
        (
            FrameDebuggerDisableToolId,
            Title = "Frame Debugger / Disable",
            DestructiveHint = true,
            IdempotentHint = true,
            Enabled = false
        )]
        [UcoSkillDescription("Disable the Unity Frame Debugger via reflection on `FrameDebuggerUtility`. " +
            "Idempotent — safe to call when already disabled.")]
        [UcoSkillBody("Call `FrameDebuggerUtility.SetEnabled(false)` via reflection. Returns the post-call " +
            "enabled state (always false on success).")]
        [Description("Disable the Unity Frame Debugger via reflection.")]
        public FrameDebuggerStatusResult DisableFrameDebugger(string? nothing = null)
        {
            return MainThread.Instance.Run(() =>
            {
                EnsureFrameDebuggerReflectionAvailable();
                InvokeSetEnabled(false);
                return new FrameDebuggerStatusResult { Enabled = false, EventCount = 0 };
            });
        }

        public const string FrameDebuggerGetEventsToolId = "frame-debugger-get-events";
        [UcoTool
        (
            FrameDebuggerGetEventsToolId,
            Title = "Frame Debugger / Get Events",
            ReadOnlyHint = true,
            Enabled = false
        )]
        [UcoSkillDescription("Page through draw-call events captured by the Frame Debugger. " +
            "Implemented entirely through reflection on `FrameDebuggerUtility` — best-effort across Unity versions.")]
        [UcoSkillBody("Walk `FrameDebuggerUtility.count` events with paging (`pageSize` + `cursor`). " +
            "Each entry is populated best-effort from `GetFrameEventInfoName(int)`, `GetFrameEvents()` descriptors, " +
            "and `GetFrameEventData(int)` / Unity-6 `GetFrameEventData(int, FrameDebuggerEventData)`.\n\n" +
            "## Output fields (when reflection finds them)\n\n" +
            "- `name` — event name (e.g. `Draw Mesh`, `SetRenderTarget`).\n" +
            "- `event_type`, `game_object_instance_id` — from the descriptor.\n" +
            "- `shader_name`, `pass_name`, `rt_name`, `rt_width`, `rt_height`, `vertex_count`, `index_count`, " +
            "`instance_count`, `mesh_name` — from the per-event detailed data.\n\n" +
            "## Paging\n\n" +
            "`pageSize` defaults to 50. The response sets `NextCursor` when more events remain.")]
        [Description("Page through draw-call events captured by the Frame Debugger.")]
        public FrameDebuggerEventsResult GetFrameDebuggerEvents
        (
            [Description("Number of events per page. Default: 50.")]
            int pageSize = 50,
            [Description("Cursor offset into the event list. Default: 0.")]
            int cursor = 0
        )
        {
            if (pageSize < 1) pageSize = 1;
            if (pageSize > 500) pageSize = 500;
            if (cursor < 0) cursor = 0;

            return MainThread.Instance.Run(() =>
            {
                EnsureFrameDebuggerReflectionAvailable();

                var total = ReadEventCount();
                var result = new FrameDebuggerEventsResult
                {
                    PageSize = pageSize,
                    Cursor = cursor,
                    TotalEvents = total,
                    Events = new List<FrameDebuggerEvent>()
                };

                if (total == 0)
                    return result;

                // Bulk descriptor array — used to pull event_type and gameObjectInstanceID.
                object[]? frameEvents = null;
                if (s_GetFrameEventsMethod != null)
                {
                    try
                    {
                        var raw = s_GetFrameEventsMethod.Invoke(null, null);
                        if (raw is Array arr)
                        {
                            frameEvents = new object[arr.Length];
                            arr.CopyTo(frameEvents, 0);
                        }
                    }
                    catch
                    {
                        /* Fall through — descriptor array is best-effort. */
                    }
                }

                int end = Math.Min(cursor + pageSize, total);
                for (int i = cursor; i < end; i++)
                {
                    var evt = new FrameDebuggerEvent { Index = i };

                    if (s_GetFrameEventInfoNameMethod != null)
                    {
                        try { evt.Name = s_GetFrameEventInfoNameMethod.Invoke(null, new object[] { i }) as string; }
                        catch { /* leave Name null */ }
                    }

                    if (frameEvents != null && i < frameEvents.Length && frameEvents[i] != null)
                    {
                        var desc = frameEvents[i]!;
                        var descType = desc.GetType();
                        evt.EventType = TryReadString(descType, desc, "type");
                        var goId = TryReadValue(descType, desc, "gameObjectInstanceID");
                        if (goId is int goIdInt) evt.GameObjectInstanceId = goIdInt;
                    }

                    if (s_GetFrameEventDataMethod != null)
                    {
                        try
                        {
                            var paramInfos = s_GetFrameEventDataMethod.GetParameters();
                            object? eventData;

                            if (paramInfos.Length == 2 && s_FrameDebuggerEventDataType != null)
                            {
                                // Unity 6 signature: bool GetFrameEventData(int, FrameDebuggerEventData)
                                var instance = Activator.CreateInstance(s_FrameDebuggerEventDataType);
                                var args = new object?[] { i, instance };
                                var ok = s_GetFrameEventDataMethod.Invoke(null, args);
                                eventData = (ok is true) ? args[1] : null;
                            }
                            else
                            {
                                // Older signature: FrameDebuggerEventData GetFrameEventData(int)
                                eventData = s_GetFrameEventDataMethod.Invoke(null, new object[] { i });
                            }

                            if (eventData != null)
                            {
                                var edType = eventData.GetType();
                                evt.ShaderName = TryReadString(edType, eventData, "shaderName");
                                evt.PassName = TryReadString(edType, eventData, "passName");
                                evt.RtName = TryReadString(edType, eventData, "rtName");
                                evt.RtWidth = TryReadInt(edType, eventData, "rtWidth");
                                evt.RtHeight = TryReadInt(edType, eventData, "rtHeight");
                                evt.VertexCount = TryReadInt(edType, eventData, "vertexCount");
                                evt.IndexCount = TryReadInt(edType, eventData, "indexCount");
                                evt.InstanceCount = TryReadInt(edType, eventData, "instanceCount");
                                evt.MeshName = TryReadString(edType, eventData, "meshName");
                            }
                        }
                        catch
                        {
                            /* Per-event reflection failure is non-fatal — skip data fields for this index. */
                        }
                    }

                    result.Events.Add(evt);
                }

                if (end < total)
                    result.NextCursor = end;

                return result;
            });
        }

        // ----- reflection cache ------------------------------------------------------

        private static readonly Type? s_FrameDebuggerUtilityType;
        private static readonly PropertyInfo? s_EventCountProp;
        private static readonly MethodInfo? s_SetEnabledMethod;
        private static readonly MethodInfo? s_GetFrameEventsMethod;
        private static readonly MethodInfo? s_GetFrameEventInfoNameMethod;
        private static readonly MethodInfo? s_GetFrameEventDataMethod;
        private static readonly Type? s_FrameDebuggerEventDataType;
        private static readonly bool s_FrameDebuggerAvailable;

        static Tool_Profiler()
        {
            try
            {
                // Unity 6+: relocated into the FrameDebuggerInternal sub-namespace.
                s_FrameDebuggerUtilityType =
                    Type.GetType("UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility, UnityEditor", throwOnError: false)
                    ?? Type.GetType("UnityEditorInternal.FrameDebuggerUtility, UnityEditor", throwOnError: false);

                if (s_FrameDebuggerUtilityType != null)
                {
                    s_EventCountProp = s_FrameDebuggerUtilityType.GetProperty("count", BindingFlags.Public | BindingFlags.Static)
                                    ?? s_FrameDebuggerUtilityType.GetProperty("eventsCount", BindingFlags.Public | BindingFlags.Static);

                    s_SetEnabledMethod =
                        s_FrameDebuggerUtilityType.GetMethod("SetEnabled",
                            BindingFlags.Public | BindingFlags.Static,
                            binder: null,
                            types: new[] { typeof(bool), typeof(int) },
                            modifiers: null)
                        ?? s_FrameDebuggerUtilityType.GetMethod("SetEnabled",
                            BindingFlags.Public | BindingFlags.Static,
                            binder: null,
                            types: new[] { typeof(bool) },
                            modifiers: null);

                    s_GetFrameEventsMethod = s_FrameDebuggerUtilityType.GetMethod("GetFrameEvents", BindingFlags.Public | BindingFlags.Static);
                    s_GetFrameEventInfoNameMethod = s_FrameDebuggerUtilityType.GetMethod("GetFrameEventInfoName", BindingFlags.Public | BindingFlags.Static);

                    s_FrameDebuggerEventDataType =
                        Type.GetType("UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerEventData, UnityEditor", throwOnError: false)
                        ?? Type.GetType("UnityEditorInternal.FrameDebuggerEventData, UnityEditor", throwOnError: false);

                    // Prefer the Unity 6 two-arg signature.
                    if (s_FrameDebuggerEventDataType != null)
                    {
                        s_GetFrameEventDataMethod = s_FrameDebuggerUtilityType.GetMethod("GetFrameEventData",
                            BindingFlags.Public | BindingFlags.Static,
                            binder: null,
                            types: new[] { typeof(int), s_FrameDebuggerEventDataType },
                            modifiers: null);
                    }
                    s_GetFrameEventDataMethod ??= s_FrameDebuggerUtilityType.GetMethod("GetFrameEventData",
                        BindingFlags.Public | BindingFlags.Static,
                        binder: null,
                        types: new[] { typeof(int) },
                        modifiers: null);
                }

                s_FrameDebuggerAvailable = s_FrameDebuggerUtilityType != null
                                           && s_EventCountProp != null
                                           && s_SetEnabledMethod != null;
            }
            catch
            {
                s_FrameDebuggerAvailable = false;
            }
        }

        private static void EnsureFrameDebuggerReflectionAvailable()
        {
            if (!s_FrameDebuggerAvailable)
                throw new InvalidOperationException(Error.FrameDebuggerUtilityUnavailable);
        }

        private static void InvokeSetEnabled(bool value)
        {
            var paramCount = s_SetEnabledMethod!.GetParameters().Length;
            if (paramCount == 2)
                s_SetEnabledMethod.Invoke(null, new object[] { value, 0 });
            else if (paramCount == 1)
                s_SetEnabledMethod.Invoke(null, new object[] { value });
            else
                throw new InvalidOperationException($"FrameDebuggerUtility.SetEnabled has unexpected {paramCount} parameters.");
        }

        private static int ReadEventCount()
        {
            try { return (int)(s_EventCountProp!.GetValue(null) ?? 0); }
            catch { return 0; }
        }

        private static string? TryReadString(Type type, object obj, string memberName)
            => TryReadValue(type, obj, memberName)?.ToString();

        private static int? TryReadInt(Type type, object obj, string memberName)
        {
            var value = TryReadValue(type, obj, memberName);
            return value switch
            {
                int i => i,
                long l => (int)l,
                short s => s,
                uint ui => (int)ui,
                _ => null
            };
        }

        private static object? TryReadValue(Type type, object obj, string memberName)
        {
            try
            {
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var field = type.GetField(memberName, flags);
                if (field != null) return field.GetValue(obj);

                var prop = type.GetProperty(memberName, flags);
                if (prop != null) return prop.GetValue(obj);
            }
            catch
            {
                /* fall through */
            }
            return null;
        }
    }
}

namespace AIGD
{
    [System.ComponentModel.Description("Status snapshot for the Unity Frame Debugger.")]
    public class FrameDebuggerStatusResult
    {
        [System.ComponentModel.Description("Whether the Frame Debugger is currently enabled.")]
        public bool Enabled { get; set; }

        [System.ComponentModel.Description("Number of events the Frame Debugger has captured.")]
        public int EventCount { get; set; }
    }

    [System.ComponentModel.Description("Single Frame Debugger event entry. All fields are best-effort via reflection.")]
    public class FrameDebuggerEvent
    {
        [System.ComponentModel.Description("0-based event index.")]
        public int Index { get; set; }

        [System.ComponentModel.Description("Event name (e.g. 'Draw Mesh', 'SetRenderTarget'). May be null.")]
        public string? Name { get; set; }

        [System.ComponentModel.Description("Event type as reported by the descriptor. May be null.")]
        public string? EventType { get; set; }

        [System.ComponentModel.Description("Associated GameObject instance id (when known).")]
        public int? GameObjectInstanceId { get; set; }

        [System.ComponentModel.Description("Shader used for the draw. May be null.")]
        public string? ShaderName { get; set; }

        [System.ComponentModel.Description("Shader pass name. May be null.")]
        public string? PassName { get; set; }

        [System.ComponentModel.Description("Render target name. May be null.")]
        public string? RtName { get; set; }

        [System.ComponentModel.Description("Render target width.")]
        public int? RtWidth { get; set; }

        [System.ComponentModel.Description("Render target height.")]
        public int? RtHeight { get; set; }

        [System.ComponentModel.Description("Vertex count for the draw.")]
        public int? VertexCount { get; set; }

        [System.ComponentModel.Description("Index count for the draw.")]
        public int? IndexCount { get; set; }

        [System.ComponentModel.Description("Instance count for the draw.")]
        public int? InstanceCount { get; set; }

        [System.ComponentModel.Description("Mesh name for the draw. May be null.")]
        public string? MeshName { get; set; }
    }

    [System.ComponentModel.Description("Paged result for 'frame-debugger-get-events'.")]
    public class FrameDebuggerEventsResult
    {
        [System.ComponentModel.Description("Events on the current page.")]
        public System.Collections.Generic.List<FrameDebuggerEvent> Events { get; set; }
            = new System.Collections.Generic.List<FrameDebuggerEvent>();

        [System.ComponentModel.Description("Total events captured by the Frame Debugger.")]
        public int TotalEvents { get; set; }

        [System.ComponentModel.Description("Requested page size.")]
        public int PageSize { get; set; }

        [System.ComponentModel.Description("Cursor offset for this page.")]
        public int Cursor { get; set; }

        [System.ComponentModel.Description("Cursor for the next page; null when this is the last page.")]
        public int? NextCursor { get; set; }
    }
}
