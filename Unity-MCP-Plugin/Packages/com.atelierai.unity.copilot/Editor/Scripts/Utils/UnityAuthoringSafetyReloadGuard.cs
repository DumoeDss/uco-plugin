/*
┌──────────────────────────────────────────────────────────────────┐
│  Author: AtelierAI                                                 │
│  Copyright (c) 2025 AtelierAI                                     │
│  Licensed under the MIT License.                                  │
└──────────────────────────────────────────────────────────────────┘
*/
#nullable enable

using System.Linq;
using com.AtelierAI.Uco.Framework;
using UnityEditor;

namespace com.AtelierAI.Unity.Copilot.Editor.Utils
{
    /// <summary>
    /// Invalidates every in-memory confirmation plan before a domain reload
    /// or Editor quit. The plan store is process-local by design; scripts,
    /// objects, and policy code may change across a reload, so a token that
    /// was issued by the previous AppDomain must not authorise an execution
    /// afterwards. It is deliberately not a durable operation registry hook.
    /// </summary>
    [InitializeOnLoad]
    public static class UnityAuthoringSafetyReloadGuard
    {
        static UnityAuthoringSafetyReloadGuard()
        {
            AssemblyReloadEvents.beforeAssemblyReload += InvalidateConfirmationPlansHandler;
            EditorApplication.quitting += InvalidateConfirmationPlansHandler;
        }

        /// <summary>
        /// Forgets every issued plan on the live pipeline. Returns the number
        /// of policy stores that were invalidated (tests assert on it).
        /// </summary>
        public static int InvalidateConfirmationPlans()
        {
            if (!UnityCopilotPluginEditor.HasInstance || !UnityCopilotPluginEditor.Instance.HasMcpPluginInstance)
                return 0;

            var invalidated = 0;
            foreach (var pipeline in Pipelines())
            {
                foreach (var middleware in pipeline.Middleware.OfType<AuthoringSafetyMiddleware>())
                {
                    middleware.Policy.ConfirmationPlans.Invalidate();
                    invalidated++;
                }
            }
            return invalidated;
        }

        private static System.Collections.Generic.IEnumerable<ToolExecutionPipeline> Pipelines()
        {
            var plugin = UnityCopilotPluginEditor.Instance;
            if (plugin.Tools is UcoToolManager tools)
                yield return tools.ExecutionPipeline;
            if (plugin.UcoPluginInstance?.UcoManager.SystemToolManager is UcoSystemToolManager systemTools
                && !(plugin.Tools is UcoToolManager regular && ReferenceEquals(regular.ExecutionPipeline, systemTools.ExecutionPipeline)))
                yield return systemTools.ExecutionPipeline;
        }

        private static void InvalidateConfirmationPlansHandler() => InvalidateConfirmationPlans();
    }
}
