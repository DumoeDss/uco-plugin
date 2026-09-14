#nullable enable
using System;
using System.ComponentModel;
using System.Linq;
using com.AtelierAI.Unity.Copilot.Editor.Utils;
using com.AtelierAI.Uco.Framework;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public static partial class Tool_Tests
    {
        public const string TestsJobGetToolId = "tests-job-get";
        public const string TestsJobListToolId = "tests-job-list";
        public const string TestsJobCancelToolId = "tests-job-cancel";

        [UcoTool(TestsJobGetToolId, Title = "Tests / Get Job",
            ReadOnlyHint = true, IdempotentHint = true,
            ExecutionAffinity = ToolExecutionAffinity.Background, ThreadSafeRead = true)]
        [Description("Get one durable tests-run operation by ID.")]
        public static EditorOperationInfo GetJob(string jobId)
        {
            var operation = EditorOperationRegistry.Get(jobId);
            if (operation == null || operation.Kind != "tests-run")
                throw new ArgumentException($"Test job '{jobId}' was not found.", nameof(jobId));
            return operation;
        }

        [UcoTool(TestsJobListToolId, Title = "Tests / List Jobs",
            ReadOnlyHint = true, IdempotentHint = true,
            ExecutionAffinity = ToolExecutionAffinity.Background, ThreadSafeRead = true)]
        [Description("List durable tests-run operations.")]
        public static EditorOperationInfo[] ListJobs(bool includeTerminal = true)
            => EditorOperationRegistry.List("tests-run", includeTerminal);

        [UcoTool(TestsJobCancelToolId, Title = "Tests / Cancel Job",
            DestructiveHint = true, IdempotentHint = true)]
        [Description("Request cooperative cancellation of a tests-run operation.")]
        public static EditorOperationInfo CancelJob(string jobId)
        {
            var operation = GetJob(jobId);
            if (!operation.IsTerminal)
            {
                EditorOperationOwnerRegistry.RequestCancellation(jobId);
            }
            return GetJob(jobId);
        }
    }
}
