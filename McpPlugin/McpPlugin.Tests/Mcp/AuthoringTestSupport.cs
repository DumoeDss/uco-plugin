#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using com.AtelierAI.Uco.Framework.Common.Model;

namespace com.AtelierAI.Uco.Framework.Tests.Mcp
{
    /// <summary>
    /// Shared fakes for the g-005 authoring-safety suites. Every fake is
    /// side-effect free by construction: the fake runner only counts calls,
    /// the validator/planner only read the invocation, and the transaction
    /// callbacks only record what the middleware asked for.
    /// </summary>
    internal static class AuthoringTestSupport
    {
        public static ToolCallContext ControlledContext(string callId, string dryRun = "none")
            => new ToolCallContext
            {
                Version = ToolCallControl.CurrentVersion,
                RequestID = callId + "-request",
                CallId = callId,
                CorrelationId = callId + "-trace",
                DryRun = dryRun,
                Legacy = false,
                CancellationToken = CancellationToken.None,
            };

        public static ToolCallContext LegacyContext(string requestId)
            => new ToolCallContext
            {
                Version = ToolCallControl.CurrentVersion,
                RequestID = requestId,
                CallId = requestId,
                CorrelationId = requestId,
                DryRun = "none",
                Legacy = true,
                CancellationToken = CancellationToken.None,
            };

        public static IReadOnlyDictionary<string, JsonElement> Arguments(params (string Name, object? Value)[] values)
        {
            var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var (name, value) in values)
            {
                using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
                result[name] = document.RootElement.Clone();
            }
            return result;
        }

        public static IReadOnlyDictionary<string, JsonElement> EmptyArguments()
            => new Dictionary<string, JsonElement>();

        public static IEnumerable<object[]> CredentialNameCases()
        {
            var sensitiveNames = new[]
            {
                "accessToken", "AccessToken", "access-token", "access_token", "access.token", "access token",
                "apiKey", "ApiKey", "APIKey", "api-key", "api_key", "api.key", "api key",
                "authorization", "Authorization", "bearer", "Bearer", "token", "Token",
                "password", "Password", "secret", "Secret",
                "androidKeystoreBase64", "AndroidKeystoreBase64", "android-keystore-base64",
                "android_keystore_base64", "android.keystore.base64", "android keystore base64",
                "androidKeystorePassword", "AndroidKeystorePassword", "android-keystore-password",
                "android_keystore_password", "android.keystore.password", "android keystore password",
                "androidKeyAliasPassword", "AndroidKeyAliasPassword", "android-key-alias-password",
                "android_key_alias_password", "android.key.alias.password", "android key alias password",
            };
            foreach (var name in sensitiveNames)
            {
                yield return new object[] { name, false, true };
                yield return new object[] { name, true, true };
            }

            yield return new object[] { "tokenCount", false, false };
            yield return new object[] { "tokenCount", true, false };
            yield return new object[] { "monkey", false, false };
            yield return new object[] { "monkey", true, false };
        }

        public static ToolCallConfirmation ReadConfirmation(ResponseCallTool response)
        {
            var plan = response.StructuredContent!["result"]!["confirmationPlan"]!.AsObject();
            return new ToolCallConfirmation
            {
                PlanId = plan["planId"]!.GetValue<string>(),
                PlanHash = plan["planHash"]!.GetValue<string>(),
                ExpiresAtUnixMs = plan["expiresAtUnixMs"]!.GetValue<long>(),
            };
        }

        public static async Task<ToolCallConfirmation> PlanAsync(
            AuthoringSafetyMiddleware middleware,
            FakeRunTool runner,
            ToolCallContext executeContext,
            IReadOnlyDictionary<string, JsonElement> arguments)
        {
            var planContext = executeContext.Clone();
            planContext.DryRun = "plan";
            planContext.Confirm = null;
            planContext.Confirmation = null;
            using (ToolCallInvocationScope.Push(
                planContext,
                new AuthoringInvocation(planContext, runner.Name, arguments, runner)))
            {
                var response = await middleware.InvokeAsync(
                    planContext,
                    _ => throw new InvalidOperationException("A plan must never reach the terminal."));
                if (response.Status != ResponseStatus.Success)
                    throw new InvalidOperationException("Plan failed: " + response.StructuredError?.Code);
                return ReadConfirmation(response);
            }
        }

        public static async Task<(ToolCallConfirmation Token, AuthoringPlanSummary Plan)> IssueAsync(
            AuthoringSafetyMiddleware middleware,
            FakeRunTool runner,
            ToolCallContext context,
            IReadOnlyDictionary<string, JsonElement> arguments)
        {
            var response = await ExecuteAsync(
                middleware,
                runner,
                context,
                arguments,
                _ => throw new InvalidOperationException("Issuing confirmation must not reach the terminal."));
            if (response.StructuredError?.Code != ToolCallErrorCodes.ConfirmationRequired)
                throw new InvalidOperationException("Confirmation issue failed: " + response.StructuredError?.Code);
            var node = response.StructuredError.Details?["confirmationPlan"];
            var plan = node == null
                ? null
                : JsonSerializer.Deserialize<AuthoringPlanSummary>(node.ToJsonString());
            if (plan == null)
                throw new InvalidOperationException("Confirmation issue returned no plan.");
            return (new ToolCallConfirmation
            {
                PlanId = plan.PlanId,
                PlanHash = plan.PlanHash,
                ExpiresAtUnixMs = plan.ExpiresAtUnixMs,
            }, plan);
        }

        public static async Task<ResponseCallTool> ExecuteAsync(
            AuthoringSafetyMiddleware middleware,
            FakeRunTool runner,
            ToolCallContext context,
            IReadOnlyDictionary<string, JsonElement> arguments,
            ToolCallNext? terminal = null)
        {
            using (ToolCallInvocationScope.Push(
                context,
                new AuthoringInvocation(context, runner.Name, arguments, runner)))
            {
                return await middleware.InvokeAsync(
                    context,
                    terminal ?? (_ =>
                    {
                        runner.Calls++;
                        return Task.FromResult(ResponseCallTool.Success("executed"));
                    }));
            }
        }

        public sealed class FakeRunTool : IRunTool
        {
            public FakeRunTool(string name) => Name = name;
            public string Name { get; }
            public bool Enabled { get; set; } = true;
            public string? Title => Name;
            public string? Description => null;
            public MethodInfo? Method => null;
            public string? SkillDescription => null;
            public string? SkillBody => null;
            public JsonNode? InputSchema => new JsonObject();
            public JsonNode? OutputSchema => null;
            public UcoToolType ToolType => UcoToolType.Standard;
            public bool? ReadOnlyHint { get; set; }
            public bool? DestructiveHint { get; set; }
            public bool? IdempotentHint { get; set; }
            public bool? OpenWorldHint { get; set; }
            public AuthoringCapabilityDescriptor? AuthoringCapability { get; set; }
            public int TokenCount => 0;
            public int Calls { get; set; }

            public Task<ResponseCallTool> Run(
                string requestId,
                IReadOnlyDictionary<string, JsonElement>? namedParameters,
                CancellationToken cancellationToken = default)
            {
                _ = namedParameters;
                _ = cancellationToken;
                Calls++;
                return Task.FromResult(ResponseCallTool.Success().SetRequestID(requestId));
            }
        }

        /// <summary>
        /// Validator/planner pair whose reported target fingerprint is
        /// controlled by the test, so "the target changed" can be simulated
        /// without a Unity object.
        /// </summary>
        public sealed class FakeInspector : IAuthoringValidator, IAuthoringPlanner
        {
            public string Fingerprint { get; set; } = "target-1";
            public string TargetName { get; set; } = "Block";
            public bool Valid { get; set; } = true;
            public string? FailureCode { get; set; }
            public Exception? ValidatorException { get; set; }
            public Exception? PlannerException { get; set; }
            public JsonObject? ValidationDetails { get; set; }
            public JsonObject? BeforeSummary { get; set; }
            public JsonObject? AfterSummary { get; set; }
            public Action? OnPlan { get; set; }
            public int ValidateCalls { get; private set; }
            public int PlanCalls { get; private set; }

            public AuthoringValidationResult Validate(AuthoringInvocation invocation)
            {
                ValidateCalls++;
                if (ValidatorException != null)
                    throw ValidatorException;
                return new AuthoringValidationResult
                {
                    Valid = Valid,
                    FailureCode = Valid ? null : FailureCode ?? "validation_failed",
                    Details = ValidationDetails ?? (Valid ? null : new JsonObject { ["reason"] = "fake_invalid" }),
                    Targets = new[] { Target() },
                };
            }

            public AuthoringPlanSummary Plan(AuthoringInvocation invocation)
            {
                PlanCalls++;
                OnPlan?.Invoke();
                if (PlannerException != null)
                    throw PlannerException;
                return new AuthoringPlanSummary
                {
                    Targets = new[] { Target() },
                    PredictedEffects = new[] { "modify " + TargetName },
                    BeforeSummary = BeforeSummary,
                    AfterSummary = AfterSummary,
                };
            }

            private AuthoringTargetSummary Target()
                => new AuthoringTargetSummary
                {
                    Kind = "GameObject",
                    Name = TargetName,
                    Fingerprint = Fingerprint,
                };
        }

        /// <summary>Transaction factory that records lifecycle callbacks.</summary>
        public sealed class RecordingTransactionFactory : IAuthoringTransactionFactory
        {
            public int BeginCalls { get; private set; }
            public int CompleteCalls { get; private set; }
            public int AbortCalls { get; private set; }
            public Exception? CompleteException { get; set; }
            public Exception? AbortException { get; set; }
            public AuthoringUndoLevel AdvertisedUndo { get; set; } = AuthoringUndoLevel.Full;
            public bool ProvideAbort { get; set; } = true;
            public AuthoringTransaction? Last { get; private set; }

            public IAuthoringTransaction Begin(AuthoringInvocation invocation)
            {
                BeginCalls++;
                Last = new AuthoringTransaction(
                    "test: " + invocation.Name,
                    AdvertisedUndo,
                    groupId: BeginCalls,
                    complete: () =>
                    {
                        CompleteCalls++;
                        if (CompleteException != null) throw CompleteException;
                    },
                    abort: ProvideAbort
                        ? () =>
                        {
                            AbortCalls++;
                            if (AbortException != null) throw AbortException;
                        }
                        : null);
                return Last;
            }
        }

        public static AuthoringCapabilityDescriptor Capability(
            AuthoringMutationKind kind,
            AuthoringUndoLevel undo,
            FakeInspector? inspector,
            IAuthoringTransactionFactory? transactions = null,
            bool opaque = false)
            => new AuthoringCapabilityDescriptor
            {
                MutationKind = kind,
                UndoLevel = undo,
                IsOpaque = opaque,
                SupportsValidation = inspector != null,
                SupportsPlanning = inspector != null,
                Validator = inspector,
                Planner = inspector,
                TransactionFactory = transactions,
            };
    }
}
