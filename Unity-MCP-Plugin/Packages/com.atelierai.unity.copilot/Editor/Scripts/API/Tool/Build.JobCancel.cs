#nullable enable
using System;
using System.ComponentModel;
using com.AtelierAI.Unity.Copilot.Editor.Utils;
using com.IvanMurzak.McpPlugin;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_Build
    {
        public const string BuildJobCancelToolId = "build-job-cancel";

        [McpPluginTool(BuildJobCancelToolId, Title = "Build / Cancel Job",
            DestructiveHint = true, IdempotentHint = true)]
        [Description("Request cancellation of a queued/running player build. Running BuildPipeline work is non-interruptible and reports cancellation pending until it returns.")]
        public BuildJobInfo CancelJob(string jobId)
        {
            var operation = EditorOperationRegistry.Get(jobId);
            if (operation == null || operation.Kind != "build-player")
                throw new ArgumentException(Error.JobNotFound(jobId), nameof(jobId));
            EditorOperationOwnerRegistry.RequestCancellation(jobId);
            return BuildJobRegistry.Get(jobId)!;
        }
    }
}
