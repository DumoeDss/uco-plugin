#nullable enable
using System;
using System.ComponentModel;
using com.AtelierAI.Unity.Copilot.Editor.Utils;
using com.IvanMurzak.McpPlugin;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    [McpPluginToolType]
    public sealed class Tool_Operations
    {
        [McpPluginTool("editor-operation-get", Title = "Editor Operation / Get",
            ReadOnlyHint = true, IdempotentHint = true,
            ExecutionAffinity = ToolExecutionAffinity.Background, ThreadSafeRead = true)]
        [Description("Get one durable Editor operation by ID. Returns null when it is unknown.")]
        public EditorOperationInfo? Get(string operationId)
            => EditorOperationRegistry.Get(operationId);

        [McpPluginTool("editor-operation-list", Title = "Editor Operation / List",
            ReadOnlyHint = true, IdempotentHint = true,
            ExecutionAffinity = ToolExecutionAffinity.Background, ThreadSafeRead = true)]
        [Description("List durable Editor operations, optionally filtered by kind and terminal state.")]
        public EditorOperationInfo[] List(
            string? kind = null,
            bool includeTerminal = true,
            int limit = EditorOperationRegistry.MaxListResults)
            => EditorOperationRegistry.List(kind, includeTerminal, limit);

        [McpPluginTool("editor-operation-cancel", Title = "Editor Operation / Cancel",
            DestructiveHint = true, IdempotentHint = true)]
        [Description("Request cooperative cancellation for an Editor operation.")]
        public EditorOperationInfo Cancel(string operationId)
        {
            return EditorOperationOwnerRegistry.RequestCancellation(operationId);
        }
    }
}
