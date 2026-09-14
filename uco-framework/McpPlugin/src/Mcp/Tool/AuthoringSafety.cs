/*
┌────────────────────────────────────────────────────────────────────────┐
│  Author: Ivan Murzak (https://github.com/IvanMurzak)                   │
│  Copyright (c) 2025 Ivan Murzak                                        │
│  Licensed under the MIT License.                                      │
└────────────────────────────────────────────────────────────────────────┘
*/
#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using com.AtelierAI.Uco.Framework.Common.Model;

namespace com.AtelierAI.Uco.Framework
{
    /// <summary>Describes what a registered runner can do to authoring state.</summary>
    public enum AuthoringMutationKind
    {
        Unknown,
        Read,
        Create,
        Modify,
        Add,
        Duplicate,
        Parent,
        Delete,
        Overwrite,
        ExternalWrite,
    }

    /// <summary>Conservative risk classes used by the authoring policy.</summary>
    public enum AuthoringRiskLevel
    {
        Unknown,
        Read,
        Mutating,
        Destructive,
    }

    /// <summary>Truthful Unity Undo support advertised by a capability.</summary>
    public enum AuthoringUndoLevel
    {
        None,
        Partial,
        Full,
    }

    public enum AuthoringPlanLevel
    {
        None,
        Validate,
        Plan,
    }

    /// <summary>Access intent for a declared path binding.</summary>
    public enum AuthoringPathAccessIntent
    {
        Read,
        Create,
        Modify,
        Delete,
        Output,
    }

    /// <summary>
    /// A path argument declaration owned by the runner adapter. Resolution is
    /// intentionally deferred to the project path policy milestone.
    /// </summary>
    public sealed class AuthoringPathBinding
    {
        public string ArgumentName { get; set; } = string.Empty;
        public AuthoringPathAccessIntent Intent { get; set; } = AuthoringPathAccessIntent.Read;
        public string? RootCategory { get; set; }
        /// <summary>
        /// Whether the bound argument must be present for the invocation to
        /// be considered safe. Optional bindings are useful for tools such as
        /// scene-save, which can fall back to an already-open scene path.
        /// </summary>
        public bool Required { get; set; }

        public AuthoringPathBinding Clone()
            => new AuthoringPathBinding
            {
                ArgumentName = ArgumentName,
                Intent = Intent,
                RootCategory = RootCategory,
                Required = Required,
            };
    }

    /// <summary>
    /// Declares the authoring capability on the existing reflected runner
    /// registration. This is deliberately method-scoped: it is not a second
    /// name-keyed safety registry.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class AuthoringCapabilityAttribute : Attribute
    {
        public AuthoringMutationKind MutationKind { get; set; } = AuthoringMutationKind.Unknown;
        public AuthoringUndoLevel UndoLevel { get; set; } = AuthoringUndoLevel.None;
        public bool SupportsValidation { get; set; }
        public bool SupportsPlanning { get; set; }
        public bool IsOpaque { get; set; }

        /// <summary>
        /// Optional parameterless adapter types.  Keeping the type reference
        /// on the method attribute lets the existing runner factory attach a
        /// validator/planner without introducing a second name-keyed safety
        /// registry.
        /// </summary>
        public Type? ValidatorType { get; set; }
        public Type? PlannerType { get; set; }
        public Type? TransactionFactoryType { get; set; }
    }

    /// <summary>
    /// Declares one string argument as a project path. Multiple bindings may
    /// be attached to a method, while the resulting canonical bindings stay
    /// attached to that method's runner descriptor.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
    public sealed class AuthoringPathBindingAttribute : Attribute
    {
        public string ArgumentName { get; }
        public AuthoringPathAccessIntent Intent { get; set; } = AuthoringPathAccessIntent.Read;
        public string? RootCategory { get; set; }
        public bool Required { get; set; }

        public AuthoringPathBindingAttribute(string argumentName)
        {
            ArgumentName = argumentName ?? throw new ArgumentNullException(nameof(argumentName));
        }

        internal AuthoringPathBinding ToBinding()
            => new AuthoringPathBinding
            {
                ArgumentName = ArgumentName,
                Intent = Intent,
                RootCategory = RootCategory,
                Required = Required,
            };
    }

    /// <summary>
    /// Pure validation seam reserved for a future read-only validator. It is
    /// deliberately separate from an executable runner.
    /// </summary>
    public interface IAuthoringValidator
    {
        AuthoringValidationResult Validate(AuthoringInvocation invocation);
    }

    /// <summary>Pure planning seam reserved for the dry-run milestone.</summary>
    public interface IAuthoringPlanner
    {
        AuthoringPlanSummary Plan(AuthoringInvocation invocation);
    }

    /// <summary>
    /// Existing runner adapter seam. Implementations attach their descriptor
    /// to <see cref="IRunTool.AuthoringCapability"/>; no second name registry
    /// is needed or permitted.
    /// </summary>
    public interface IAuthoringCapabilityAdapter
    {
        AuthoringCapabilityDescriptor Descriptor { get; }
    }

    /// <summary>
    /// Capability metadata attached to one existing <see cref="IRunTool"/>.
    /// Descriptor data can only make a catalog hint more restrictive.
    /// </summary>
    public sealed class AuthoringCapabilityDescriptor
    {
        public AuthoringMutationKind MutationKind { get; set; } = AuthoringMutationKind.Unknown;
        public AuthoringUndoLevel UndoLevel { get; set; } = AuthoringUndoLevel.None;
        public bool SupportsValidation { get; set; }
        public bool SupportsPlanning { get; set; }
        public bool IsOpaque { get; set; }
        public IReadOnlyList<AuthoringPathBinding> PathBindings { get; set; }
            = Array.Empty<AuthoringPathBinding>();

        /// <summary>
        /// Optional pure adapters. They are not invoked by the 1.x safety
        /// foundation; later dry-run work supplies the execution contract.
        /// </summary>
        [JsonIgnore]
        public IAuthoringValidator? Validator { get; set; }

        [JsonIgnore]
        public IAuthoringPlanner? Planner { get; set; }

        [JsonIgnore]
        public IAuthoringCapabilityAdapter? Adapter { get; set; }

        [JsonIgnore]
        public IAuthoringTransactionFactory? TransactionFactory { get; set; }

        public bool IsKnownMutation
            => MutationKind != AuthoringMutationKind.Unknown && !IsOpaque;

        public AuthoringCapabilityDescriptor Clone()
            => new AuthoringCapabilityDescriptor
            {
                MutationKind = MutationKind,
                UndoLevel = UndoLevel,
                SupportsValidation = SupportsValidation,
                SupportsPlanning = SupportsPlanning,
                IsOpaque = IsOpaque,
                PathBindings = PathBindings == null
                    ? Array.Empty<AuthoringPathBinding>()
                    : PathBindings.Where(binding => binding != null)
                        .Select(binding => binding.Clone()).ToArray(),
                Validator = Validator,
                Planner = Planner,
                Adapter = Adapter,
                TransactionFactory = TransactionFactory,
            };
    }

    /// <summary>Builds a descriptor from the existing reflected registration.</summary>
    internal static class AuthoringCapabilityDescriptorFactory
    {
        public static AuthoringCapabilityDescriptor? FromMethod(MethodInfo method)
        {
            if (method == null) throw new ArgumentNullException(nameof(method));

            var capability = method.GetCustomAttribute<AuthoringCapabilityAttribute>();
            var pathBindings = method
                .GetCustomAttributes<AuthoringPathBindingAttribute>()
                .Select(attribute => attribute.ToBinding())
                .ToArray();
            if (capability == null && pathBindings.Length == 0)
                return null;

            return new AuthoringCapabilityDescriptor
            {
                MutationKind = capability?.MutationKind ?? AuthoringMutationKind.Unknown,
                UndoLevel = capability?.UndoLevel ?? AuthoringUndoLevel.None,
                SupportsValidation = capability?.SupportsValidation ?? false,
                SupportsPlanning = capability?.SupportsPlanning ?? false,
                IsOpaque = capability?.IsOpaque ?? false,
                PathBindings = pathBindings,
                Validator = CreateAdapter<IAuthoringValidator>(capability?.ValidatorType),
                Planner = CreateAdapter<IAuthoringPlanner>(capability?.PlannerType),
                TransactionFactory = CreateAdapter<IAuthoringTransactionFactory>(capability?.TransactionFactoryType),
            };
        }

        private static T? CreateAdapter<T>(Type? type) where T : class
        {
            if (type == null || !typeof(T).IsAssignableFrom(type))
                return null;
            try
            {
                return Activator.CreateInstance(type) as T;
            }
            catch
            {
                // A malformed optional adapter is treated as absent; policy
                // then returns dry_run_unsupported instead of executing a
                // runner whose safety contract could not be constructed.
                return null;
            }
        }
    }

    /// <summary>Internal immutable invocation metadata carried by the scope.</summary>
    public sealed class AuthoringInvocation
    {
        public ToolCallContext Context { get; }
        public string Name { get; }
        public IReadOnlyDictionary<string, JsonElement> Arguments { get; private set; }
        /// <summary>Arguments as received before canonical path rewriting.</summary>
        public IReadOnlyDictionary<string, JsonElement> RawArguments { get; }
        public IRunTool Runner { get; }
        public AuthoringCapabilityDescriptor? Descriptor { get; }
        public IReadOnlyDictionary<string, CanonicalProjectPathBinding> PathBindings { get; private set; }
            = new Dictionary<string, CanonicalProjectPathBinding>(StringComparer.OrdinalIgnoreCase);
        public bool HasCanonicalArguments { get; private set; }
        public AuthoringUndoLevel? EffectiveUndoLevel { get; internal set; }
        public AuthoringPlanSummary? ApprovedPlan { get; internal set; }
        [JsonIgnore]
        public AuthoringPlanSummary? PlanBeingVerified { get; internal set; }
        internal ConfirmationPlanRecord? PendingConfirmation { get; set; }
        internal bool PolicyApproved { get; private set; }
        internal bool SuppressTransaction { get; set; }

        public AuthoringInvocation(
            ToolCallContext context,
            string name,
            IReadOnlyDictionary<string, JsonElement> arguments,
            IRunTool runner)
        {
            Context = context ?? throw new ArgumentNullException(nameof(context));
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Tool name must be a non-empty string.", nameof(name));
            Name = name;
            if (arguments == null) throw new ArgumentNullException(nameof(arguments));
            var argumentCopy = CloneArguments(arguments);
            Arguments = argumentCopy;
            RawArguments = argumentCopy;
            Runner = runner ?? throw new ArgumentNullException(nameof(runner));
            // Snapshot the registration metadata at call entry. A mutable
            // descriptor must not be able to change after path preparation or
            // risk classification and thereby turn a safe invocation into a
            // different capability.
            Descriptor = runner.AuthoringCapability?.Clone();
        }

        /// <summary>
        /// Replaces the argument view with policy-owned canonical arguments.
        /// The method is internal so a tool cannot manufacture an authority
        /// binding from generated input data.
        /// </summary>
        internal void SetCanonicalArguments(
            IReadOnlyDictionary<string, JsonElement> arguments,
            IReadOnlyDictionary<string, CanonicalProjectPathBinding> pathBindings)
        {
            if (arguments == null) throw new ArgumentNullException(nameof(arguments));
            if (pathBindings == null) throw new ArgumentNullException(nameof(pathBindings));

            // Keep a private copy so a caller cannot mutate the invocation
            // after the policy has approved its canonical representation.
            var argumentCopy = CloneArguments(arguments);
            var bindingCopy = new Dictionary<string, CanonicalProjectPathBinding>(
                pathBindings,
                StringComparer.OrdinalIgnoreCase);
            Arguments = argumentCopy;
            PathBindings = bindingCopy;
            HasCanonicalArguments = true;
        }

        internal void MarkPolicyApproved() => PolicyApproved = true;

        private static Dictionary<string, JsonElement> CloneArguments(
            IReadOnlyDictionary<string, JsonElement> arguments)
        {
            var clone = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var argument in arguments)
                clone[argument.Key] = argument.Value.Clone();
            return clone;
        }

        public AuthoringInvocation WithContext(ToolCallContext context)
            => Clone(context, Runner);

        internal AuthoringInvocation WithRunner(IRunTool runner)
            => Clone(Context, runner);

        private AuthoringInvocation Clone(ToolCallContext context, IRunTool runner)
        {
            var clone = new AuthoringInvocation(context, Name, RawArguments, runner);
            if (HasCanonicalArguments)
                clone.SetCanonicalArguments(Arguments, PathBindings);
            if (PolicyApproved)
                clone.MarkPolicyApproved();
            clone.SuppressTransaction = SuppressTransaction;
            clone.EffectiveUndoLevel = EffectiveUndoLevel;
            clone.ApprovedPlan = ApprovedPlan?.CloneBounded();
            clone.PlanBeingVerified = PlanBeingVerified?.CloneBounded();
            clone.PendingConfirmation = PendingConfirmation;
            return clone;
        }
    }

    /// <summary>
    /// Async-flow-local access to the middleware policy that owns confirmation
    /// records. Composite planners use this handle so child records are issued
    /// by the same bounded process-local store as their parent.
    /// </summary>
    public static class AuthoringSafetyPolicyContext
    {
        private static readonly AsyncLocal<AuthoringSafetyPolicy?> CurrentValue =
            new AsyncLocal<AuthoringSafetyPolicy?>();

        public static AuthoringSafetyPolicy? Current => CurrentValue.Value;

        public static IDisposable Push(AuthoringSafetyPolicy policy)
        {
            if (policy == null) throw new ArgumentNullException(nameof(policy));
            var previous = CurrentValue.Value;
            CurrentValue.Value = policy;
            return new Scope(previous);
        }

        private sealed class Scope : IDisposable
        {
            private readonly AuthoringSafetyPolicy? _previous;
            private int _disposed;

            public Scope(AuthoringSafetyPolicy? previous) => _previous = previous;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                    CurrentValue.Value = _previous;
            }
        }
    }

    /// <summary>
    /// Read-only view of whether the current async flow is executing an
    /// invocation that passed the authoring safety policy. Pilot tool bodies
    /// consult it before their first mutation so a direct runner call outside
    /// the manager/pipeline fails closed instead of mutating.
    /// </summary>
    public static class AuthoringPolicyApproval
    {
        public static bool IsCurrentInvocationApproved
            => ToolCallInvocationScope.CurrentInvocation?.PolicyApproved == true;

        public static void Require()
        {
            if (IsCurrentInvocationApproved)
                return;

            throw new ToolCallControlException(
                ToolCallErrorCodes.SafetyUnsupported,
                "This authoring tool must be invoked through the tool manager so the safety policy can approve it.",
                details: new JsonObject { ["reason"] = "policy_scope_missing" });
        }
    }

    /// <summary>Read-only facts returned by a validator adapter.</summary>
    public sealed class AuthoringValidationResult
    {
        [JsonPropertyName("valid")]
        public bool Valid { get; set; }

        [JsonPropertyName("code")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? FailureCode { get; set; }

        // FailureReason is intentionally not emitted. Adapter exception text
        // is not part of the safety wire contract and may contain paths or
        // credentials. The policy exposes only the stable code above.
        [JsonIgnore]
        public string? FailureReason { get; set; }

        [JsonPropertyName("targets")]
        public IReadOnlyList<AuthoringTargetSummary> Targets { get; set; }
            = Array.Empty<AuthoringTargetSummary>();

        /// <summary>
        /// Optional bounded facts supplied by a validator.  Validators must
        /// keep this object free of host paths, credentials, and arbitrary
        /// exception text; the policy copies only safe JSON values to the
        /// dry-run response.
        /// </summary>
        public JsonObject? Details { get; set; }
    }

    /// <summary>Bounded safe target data suitable for a plan summary.</summary>
    public sealed class AuthoringTargetSummary
    {
        private const int MaxTextLength = 160;

        [JsonPropertyName("kind")]
        public string? Kind { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("relativePath")]
        public string? RelativePath { get; set; }

        [JsonPropertyName("fingerprint")]
        public string? Fingerprint { get; set; }

        public AuthoringTargetSummary CloneSafe()
            => new AuthoringTargetSummary
            {
                Kind = Bound(Kind),
                Name = Bound(Name),
                RelativePath = BoundRelativePath(RelativePath),
                Fingerprint = Bound(Fingerprint),
            };

        private static string? Bound(string? value)
        {
            if (value == null) return null;
            return value.Length <= MaxTextLength ? value : value.Substring(0, MaxTextLength);
        }

        private static string? BoundRelativePath(string? value)
        {
            var bounded = Bound(value);
            if (bounded == null) return null;
            // Absolute/host paths are not safe plan details. Keep only a
            // project-relative display value when one is supplied.
            if (bounded.StartsWith("/", StringComparison.Ordinal)
                || bounded.StartsWith("\\", StringComparison.Ordinal)
                || (bounded.Length > 1 && bounded[1] == ':'))
                return null;
            return bounded.Replace('\\', '/');
        }
    }

    public sealed class AuthoringChildPlanSummary
    {
        [JsonPropertyName("index")]
        public int Index { get; set; }

        [JsonPropertyName("tool")]
        public string? ToolName { get; set; }

        [JsonPropertyName("confirmationRequired")]
        public bool ConfirmationRequired { get; set; }

        [JsonPropertyName("planId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? PlanId { get; set; }

        [JsonPropertyName("planHash")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? PlanHash { get; set; }

        [JsonPropertyName("expiresAtUnixMs")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? ExpiresAtUnixMs { get; set; }

        [JsonPropertyName("policyVersion")]
        public int PolicyVersion { get; set; } = AuthoringSafetyPolicy.PolicyVersion;

        [JsonPropertyName("requestID")]
        public string? RequestID { get; set; }

        [JsonPropertyName("callId")]
        public string? CallId { get; set; }

        [JsonPropertyName("correlationId")]
        public string? CorrelationId { get; set; }

        [JsonPropertyName("parentCallId")]
        public string? ParentCallId { get; set; }

        [JsonPropertyName("deadlineUnixMs")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? DeadlineUnixMs { get; set; }

        [JsonPropertyName("cancellationId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? CancellationId { get; set; }

        [JsonPropertyName("idempotencyKey")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? IdempotencyKey { get; set; }

        [JsonPropertyName("argumentsHash")]
        public string? ArgumentsHash { get; set; }

        [JsonIgnore]
        public AuthoringRiskLevel RiskLevel { get; set; }

        [JsonPropertyName("risk")]
        public string Risk
        {
            get => RiskLevel.ToWireValue();
            set => RiskLevel = AuthoringRiskLevelExtensions.Parse(value);
        }

        [JsonIgnore]
        public AuthoringUndoLevel UndoLevel { get; set; }

        [JsonPropertyName("undo")]
        public string Undo
        {
            get => UndoLevel.ToWireValue();
            set => UndoLevel = AuthoringUndoLevelExtensions.Parse(value);
        }

        [JsonIgnore]
        public AuthoringPlanLevel PlanLevelValue { get; set; }

        [JsonPropertyName("planLevel")]
        public string PlanLevel
        {
            get => PlanLevelValue.ToWireValue();
            set => PlanLevelValue = AuthoringPlanLevelExtensions.Parse(value);
        }

        [JsonPropertyName("targets")]
        public IReadOnlyList<AuthoringTargetSummary> Targets { get; set; }
            = Array.Empty<AuthoringTargetSummary>();

        [JsonPropertyName("predictedEffects")]
        public IReadOnlyList<string> PredictedEffects { get; set; }
            = Array.Empty<string>();

        [JsonPropertyName("supportsSharedTransaction")]
        public bool SupportsSharedTransaction { get; set; } = true;

        public AuthoringChildPlanSummary CloneBounded()
            => new AuthoringChildPlanSummary
            {
                Index = Math.Max(0, Index),
                ToolName = Bound(ToolName),
                ConfirmationRequired = ConfirmationRequired,
                PlanId = ConfirmationRequired ? Bound(PlanId) : null,
                PlanHash = ConfirmationRequired ? Bound(PlanHash) : null,
                ExpiresAtUnixMs = ConfirmationRequired ? ExpiresAtUnixMs : null,
                PolicyVersion = PolicyVersion,
                RequestID = Bound(RequestID),
                CallId = Bound(CallId),
                CorrelationId = Bound(CorrelationId),
                ParentCallId = Bound(ParentCallId),
                DeadlineUnixMs = DeadlineUnixMs,
                CancellationId = Bound(CancellationId),
                IdempotencyKey = Bound(IdempotencyKey),
                ArgumentsHash = Bound(ArgumentsHash),
                RiskLevel = RiskLevel,
                UndoLevel = UndoLevel,
                PlanLevelValue = PlanLevelValue,
                Targets = Targets == null
                    ? Array.Empty<AuthoringTargetSummary>()
                    : Targets.Where(target => target != null).Take(8)
                        .Select(target => target.CloneSafe()).ToArray(),
                PredictedEffects = PredictedEffects == null
                    ? Array.Empty<string>()
                    : PredictedEffects.Where(effect => effect != null).Take(8)
                        .Select(Bound).Where(effect => effect != null).Cast<string>().ToArray(),
                SupportsSharedTransaction = SupportsSharedTransaction,
            };

        private static string? Bound(string? value)
            => value == null ? null : value.Length <= 160 ? value : value.Substring(0, 160);
    }

    /// <summary>
    /// Bounded summary returned by a plan adapter and used as the public
    /// confirmation-plan contract.
    /// </summary>
    public sealed class AuthoringPlanSummary
    {
        [JsonPropertyName("planId")]
        public string? PlanId { get; set; }

        [JsonPropertyName("planHash")]
        public string? PlanHash { get; set; }

        [JsonPropertyName("expiresAtUnixMs")]
        public long? ExpiresAtUnixMs { get; set; }

        [JsonPropertyName("policyVersion")]
        public int PolicyVersion { get; set; } = AuthoringSafetyPolicy.PolicyVersion;

        /// <summary>Digest of canonical arguments, never the raw argument object.</summary>
        [JsonPropertyName("argumentsHash")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ArgumentsHash { get; set; }

        [JsonPropertyName("tool")]
        public string? ToolName { get; set; }

        [JsonPropertyName("requestID")]
        public string? RequestID { get; set; }

        [JsonPropertyName("callId")]
        public string? CallId { get; set; }

        [JsonPropertyName("correlationId")]
        public string? CorrelationId { get; set; }

        [JsonIgnore]
        public AuthoringRiskLevel RiskLevel { get; set; }

        [JsonPropertyName("risk")]
        public string Risk
        {
            get => RiskLevel.ToWireValue();
            set => RiskLevel = AuthoringRiskLevelExtensions.Parse(value);
        }

        [JsonIgnore]
        public AuthoringUndoLevel UndoLevel { get; set; }

        [JsonPropertyName("undo")]
        public string Undo
        {
            get => UndoLevel.ToWireValue();
            set => UndoLevel = AuthoringUndoLevelExtensions.Parse(value);
        }

        [JsonIgnore]
        public bool HasUndoOverride { get; set; }

        [JsonIgnore]
        public bool SupportsSharedTransaction { get; set; } = true;

        [JsonIgnore]
        public AuthoringPlanLevel PlanLevelValue { get; set; }

        [JsonPropertyName("planLevel")]
        public string PlanLevel
        {
            get => PlanLevelValue.ToWireValue();
            set => PlanLevelValue = AuthoringPlanLevelExtensions.Parse(value);
        }

        [JsonPropertyName("targets")]
        public IReadOnlyList<AuthoringTargetSummary> Targets { get; set; }
            = Array.Empty<AuthoringTargetSummary>();

        [JsonPropertyName("predictedEffects")]
        public IReadOnlyList<string> PredictedEffects { get; set; }
            = Array.Empty<string>();

        [JsonPropertyName("childRecords")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public IReadOnlyList<AuthoringChildPlanSummary>? ChildRecords { get; set; }

        [JsonPropertyName("before")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public JsonObject? BeforeSummary { get; set; }

        [JsonPropertyName("after")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public JsonObject? AfterSummary { get; set; }

        public AuthoringPlanSummary CloneBounded()
            => new AuthoringPlanSummary
            {
                PlanId = Bound(PlanId),
                PlanHash = Bound(PlanHash),
                ExpiresAtUnixMs = ExpiresAtUnixMs,
                PolicyVersion = PolicyVersion,
                ArgumentsHash = Bound(ArgumentsHash),
                ToolName = Bound(ToolName),
                RequestID = Bound(RequestID),
                CallId = Bound(CallId),
                CorrelationId = Bound(CorrelationId),
                RiskLevel = RiskLevel,
                UndoLevel = UndoLevel,
                HasUndoOverride = HasUndoOverride,
                SupportsSharedTransaction = SupportsSharedTransaction,
                PlanLevelValue = PlanLevelValue,
                Targets = Targets == null
                    ? Array.Empty<AuthoringTargetSummary>()
                    : Targets.Where(target => target != null).Take(32)
                        .Select(target => target.CloneSafe()).ToArray(),
                PredictedEffects = PredictedEffects == null
                    ? Array.Empty<string>()
                    : PredictedEffects.Where(effect => effect != null).Take(32)
                        .Select(Bound).Where(effect => effect != null).Cast<string>().ToArray(),
                ChildRecords = ChildRecords == null
                    ? null
                    : ChildRecords.Where(child => child != null).Take(100)
                        .Select(child => child.CloneBounded()).ToArray(),
                BeforeSummary = AuthoringSafeJson.CloneObject(BeforeSummary),
                AfterSummary = AuthoringSafeJson.CloneObject(AfterSummary),
            };

        private static string? Bound(string? value)
        {
            if (value == null) return null;
            return value.Length <= 160 ? value : value.Substring(0, 160);
        }

    }

    /// <summary>Result of one policy decision, safe to expose to callers.</summary>
    public sealed class AuthoringPolicyResult
    {
        public bool Allowed { get; set; }
        public bool RequiresConfirmation { get; set; }
        public bool Mutated { get; set; }
        public string? Code { get; set; }
        public string? Message { get; set; }
        public bool Retryable { get; set; }
        public string? RequestID { get; set; }
        public string? CallId { get; set; }
        public string? CorrelationId { get; set; }

        /// <summary>
        /// True when the decision was made for a legacy (uncontrolled) call.
        /// Legacy callers only see the text envelope, so a rejection must
        /// carry a copyable code and retry instruction in its message.
        /// </summary>
        [JsonIgnore]
        public bool Legacy { get; set; }

        [JsonPropertyName("dryRun")]
        public string DryRun { get; set; } = "none";

        [JsonPropertyName("validation")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public AuthoringValidationResult? Validation { get; set; }

        [JsonPropertyName("confirmationPlan")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public AuthoringPlanSummary? ConfirmationPlan { get; set; }

        [JsonIgnore]
        public AuthoringRiskLevel RiskLevel { get; set; }

        [JsonPropertyName("risk")]
        public string Risk
        {
            get => RiskLevel.ToWireValue();
            set => RiskLevel = AuthoringRiskLevelExtensions.Parse(value);
        }

        [JsonIgnore]
        public AuthoringUndoLevel UndoLevel { get; set; }

        [JsonPropertyName("undo")]
        public string Undo
        {
            get => UndoLevel.ToWireValue();
            set => UndoLevel = AuthoringUndoLevelExtensions.Parse(value);
        }

        public IReadOnlyList<AuthoringTargetSummary> Targets { get; set; }
            = Array.Empty<AuthoringTargetSummary>();
        public IReadOnlyList<string> PredictedEffects { get; set; }
            = Array.Empty<string>();

        /// <summary>Bounded, category-only details; never raw exception/path text.</summary>
        public JsonObject? Details { get; set; }

        public ToolCallError ToError()
        {
            var details = Details == null ? null : JsonNode.Parse(Details.ToJsonString());
            return new ToolCallError(
                Code ?? ToolCallErrorCodes.SafetyUnsupported,
                Message ?? "The authoring call was rejected by policy.",
                Retryable,
                CallId,
                CorrelationId,
                details);
        }

        public static AuthoringPolicyResult Allow(
            AuthoringRiskLevel risk,
            AuthoringUndoLevel undo,
            ToolCallContext context)
            => Create(true, null, null, risk, undo, false, context);

        public static AuthoringPolicyResult Reject(
            string code,
            string message,
            AuthoringRiskLevel risk,
            AuthoringUndoLevel undo,
            ToolCallContext context,
            bool requiresConfirmation = false,
            JsonObject? details = null)
        {
            var result = Create(false, code, message, risk, undo, requiresConfirmation, context);
            result.Details = details;
            return result;
        }

        private static AuthoringPolicyResult Create(
            bool allowed,
            string? code,
            string? message,
            AuthoringRiskLevel risk,
            AuthoringUndoLevel undo,
            bool requiresConfirmation,
            ToolCallContext context)
            => new AuthoringPolicyResult
            {
                Allowed = allowed,
                RequiresConfirmation = requiresConfirmation,
                Mutated = false,
                Code = code,
                Message = message,
                Retryable = false,
                RequestID = context.RequestID,
                CallId = context.CallId,
                CorrelationId = context.CorrelationId,
                Legacy = context.Legacy,
                RiskLevel = risk,
                UndoLevel = undo,
                DryRun = context.DryRun,
            };
    }

    /// <summary>Classifies hints and descriptor metadata without executing tools.</summary>
    public static class AuthoringSafetyPolicyClassifier
    {
        public static AuthoringRiskLevel Classify(IRunTool? runner)
            => runner == null
                ? AuthoringRiskLevel.Unknown
                : Classify(runner.ReadOnlyHint, runner.DestructiveHint, runner.AuthoringCapability);

        public static AuthoringRiskLevel Classify(
            bool? readOnlyHint,
            bool? destructiveHint,
            AuthoringCapabilityDescriptor? descriptor = null)
            => ClassifyDetailed(readOnlyHint, destructiveHint, descriptor).Risk;

        public static AuthoringClassification ClassifyDetailed(
            bool? readOnlyHint,
            bool? destructiveHint,
            AuthoringCapabilityDescriptor? descriptor = null)
        {
            var descriptorKind = descriptor?.IsOpaque == true
                ? AuthoringMutationKind.Unknown
                : descriptor?.MutationKind ?? AuthoringMutationKind.Unknown;
            var descriptorDestructive = IsDestructive(descriptorKind);
            var descriptorMutating = IsMutating(descriptorKind);
            var descriptorRead = descriptorKind == AuthoringMutationKind.Read;

            // Destructive signals always win. idempotent/open-world hints are
            // deliberately absent: they never lower risk or grant authority.
            if (destructiveHint == true || descriptorDestructive)
                return new AuthoringClassification(AuthoringRiskLevel.Destructive, false, true);

            // An opaque registration cannot establish read authority, even
            // when a stale catalog hint says read-only. Keep the explicit
            // destructive precedence above so an opaque destructive call is
            // still reported with its stronger risk class.
            if (descriptor?.IsOpaque == true)
                return new AuthoringClassification(AuthoringRiskLevel.Unknown, false, false);

            var contradiction = (readOnlyHint == true && descriptorMutating)
                || (readOnlyHint == false && descriptorRead);
            if (contradiction)
                return new AuthoringClassification(AuthoringRiskLevel.Unknown, true, descriptor != null);

            if (readOnlyHint == false || descriptorMutating)
                return new AuthoringClassification(AuthoringRiskLevel.Mutating, false, descriptor != null);

            if (readOnlyHint == true || descriptorRead)
                return new AuthoringClassification(AuthoringRiskLevel.Read, false, true);

            return new AuthoringClassification(AuthoringRiskLevel.Unknown, false, false);
        }

        private static bool IsDestructive(AuthoringMutationKind kind)
            => kind == AuthoringMutationKind.Delete
                || kind == AuthoringMutationKind.Overwrite
                || kind == AuthoringMutationKind.ExternalWrite;

        private static bool IsMutating(AuthoringMutationKind kind)
            => kind == AuthoringMutationKind.Create
                || kind == AuthoringMutationKind.Modify
                || kind == AuthoringMutationKind.Add
                || kind == AuthoringMutationKind.Duplicate
                || kind == AuthoringMutationKind.Parent;
    }

    /// <summary>Detailed classifier output for matrix and diagnostics tests.</summary>
    public sealed class AuthoringClassification
    {
        public AuthoringRiskLevel Risk { get; }
        public bool Contradictory { get; }
        public bool HasKnownCapability { get; }

        public AuthoringClassification(
            AuthoringRiskLevel risk,
            bool contradictory,
            bool hasKnownCapability)
        {
            Risk = risk;
            Contradictory = contradictory;
            HasKnownCapability = hasKnownCapability;
        }
    }

    /// <summary>
    /// Central g-005 policy decision. Path bindings are prepared here, before
    /// risk evaluation and before any continuation can reach a runner. Dry-run
    /// and transaction adapters remain separate milestones, so unsupported
    /// modes still fail closed.
    /// </summary>
    public sealed partial class AuthoringSafetyPolicy
    {
        public const int PolicyVersion = 2;

        public ProjectPathPolicy? PathPolicy { get; }
        public ConfirmationPlanStore ConfirmationPlans { get; }

        public AuthoringSafetyPolicy(
            ProjectPathPolicy? pathPolicy = null,
            ConfirmationPlanStore? confirmationPlans = null)
        {
            PathPolicy = pathPolicy;
            ConfirmationPlans = confirmationPlans ?? new ConfirmationPlanStore();
        }

        /// <summary>
        /// Applies all descriptor-declared path bindings. A missing policy is
        /// itself a safety failure; raw path arguments are never accepted as a
        /// fallback.
        /// </summary>
        public AuthoringInvocation PrepareInvocation(AuthoringInvocation invocation)
        {
            if (invocation == null)
                throw new ArgumentNullException(nameof(invocation));

            var bindings = invocation.Descriptor?.PathBindings;
            if (bindings == null || bindings.Count == 0)
                return invocation;

            if (PathPolicy == null)
            {
                throw new ToolCallControlException(
                    ToolCallErrorCodes.SafetyUnsupported,
                    "This path-sensitive tool is not configured with a project path policy.",
                    details: new JsonObject { ["reason"] = "path_policy_unconfigured" });
            }

            return PathPolicy.ApplyToInvocation(invocation);
        }

        /// <summary>
        /// Re-resolves every write binding immediately before a runner is
        /// entered. The returned invocation owns the fresh canonical paths;
        /// previously approved full paths are not reused as authority.
        /// </summary>
        public AuthoringInvocation ReResolveWrites(AuthoringInvocation invocation)
        {
            if (invocation == null)
                throw new ArgumentNullException(nameof(invocation));

            var bindings = invocation.Descriptor?.PathBindings;
            if (bindings == null || bindings.Count == 0)
                return invocation;

            if (PathPolicy == null)
            {
                throw new ToolCallControlException(
                    ToolCallErrorCodes.SafetyUnsupported,
                    "This path-sensitive tool is not configured with a project path policy.",
                    details: new JsonObject { ["reason"] = "path_policy_unconfigured" });
            }

            return PathPolicy.ReResolveInvocationBeforeWrite(invocation);
        }

        public AuthoringPolicyResult Evaluate(AuthoringInvocation invocation)
        {
            if (invocation == null)
                throw new ArgumentNullException(nameof(invocation));

            AuthoringInvocation prepared;
            try
            {
                prepared = PrepareInvocation(invocation);
            }
            catch (ToolCallControlException exception)
            {
                var classificationForError = AuthoringSafetyPolicyClassifier.ClassifyDetailed(
                    invocation.Runner.ReadOnlyHint,
                    invocation.Runner.DestructiveHint,
                    invocation.Descriptor);
                var result = AuthoringPolicyResult.Reject(
                    exception.Code,
                    exception.Message,
                    classificationForError.Risk,
                    invocation.Descriptor?.UndoLevel ?? AuthoringUndoLevel.None,
                    invocation.Context,
                    requiresConfirmation: classificationForError.Risk != AuthoringRiskLevel.Read,
                    details: exception.Details as JsonObject);
                result.Retryable = exception.Retryable;
                return result;
            }

            return EvaluatePrepared(prepared);
        }

        private AuthoringPolicyResult EvaluatePrepared(AuthoringInvocation invocation)
        {
            var context = invocation.Context;
            var descriptor = invocation.Descriptor;
            var classification = AuthoringSafetyPolicyClassifier.ClassifyDetailed(
                invocation.Runner.ReadOnlyHint,
                invocation.Runner.DestructiveHint,
                descriptor);
            var undo = GetEffectiveUndo(classification.Risk, descriptor);
            var planLevel = GetPlanLevel(descriptor);

            if (!IsValidDryRun(context.DryRun))
            {
                return AuthoringPolicyResult.Reject(
                    ToolCallErrorCodes.InvalidControl,
                    "dryRun must be one of: none, validate, plan.",
                    classification.Risk,
                    undo,
                    context);
            }

            if (!string.Equals(context.DryRun, "none", StringComparison.Ordinal))
            {
                return EvaluateDryRun(invocation, classification, undo);
            }

            if (classification.Risk == AuthoringRiskLevel.Read)
                return AuthoringPolicyResult.Allow(classification.Risk, undo, context);

            var requiresConfirmation = RequiresConfirmation(classification.Risk, undo);
            if (!requiresConfirmation)
                return AuthoringPolicyResult.Allow(classification.Risk, undo, context);

            if (context.Confirm != true || context.Confirmation == null)
            {
                if (context.Legacy)
                {
                    return AuthoringPolicyResult.Reject(
                        ToolCallErrorCodes.ConfirmationRequired,
                        "This tool call requires controlled confirmation before it can execute.",
                        classification.Risk,
                        undo,
                        context,
                        requiresConfirmation: true,
                        details: new JsonObject
                        {
                            ["retryWith"] = new JsonObject
                            {
                                ["arguments"] = new JsonObject(),
                                ["control"] = new JsonObject(),
                            },
                        });
                }

                return IssueConfirmation(invocation, classification, undo);
            }

            return VerifyConfirmation(invocation, classification, undo);
        }

        internal AuthoringPolicyResult EvaluatePreparedForMiddleware(AuthoringInvocation invocation)
            => EvaluatePrepared(invocation);

        public static AuthoringRiskLevel Classify(IRunTool? runner)
            => AuthoringSafetyPolicyClassifier.Classify(runner);

        public static AuthoringRiskLevel Classify(
            bool? readOnlyHint,
            bool? destructiveHint,
            AuthoringCapabilityDescriptor? descriptor = null)
            => AuthoringSafetyPolicyClassifier.Classify(readOnlyHint, destructiveHint, descriptor);

        public static AuthoringUndoLevel GetEffectiveUndo(
            AuthoringRiskLevel risk,
            AuthoringCapabilityDescriptor? descriptor)
        {
            if (risk == AuthoringRiskLevel.Unknown || descriptor == null || descriptor.IsOpaque)
                return AuthoringUndoLevel.None;
            return descriptor.UndoLevel;
        }

        internal static AuthoringPlanLevel GetPlanLevel(AuthoringCapabilityDescriptor? descriptor)
        {
            if (descriptor == null || descriptor.IsOpaque)
                return AuthoringPlanLevel.None;
            if (descriptor.Planner != null)
                return AuthoringPlanLevel.Plan;
            if (descriptor.Validator != null)
                return AuthoringPlanLevel.Validate;
            return AuthoringPlanLevel.None;
        }

        internal static bool RequiresConfirmation(AuthoringRiskLevel risk, AuthoringUndoLevel undo)
            => risk != AuthoringRiskLevel.Read
                && (risk == AuthoringRiskLevel.Destructive
                    || risk == AuthoringRiskLevel.Unknown
                    || undo == AuthoringUndoLevel.None);

        private static bool IsValidDryRun(string? value)
            => value == "none" || value == "validate" || value == "plan";
    }

    public static class AuthoringRiskLevelExtensions
    {
        public static string ToWireValue(this AuthoringRiskLevel value)
            => value switch
            {
                AuthoringRiskLevel.Read => "read",
                AuthoringRiskLevel.Mutating => "mutating",
                AuthoringRiskLevel.Destructive => "destructive",
                _ => "unknown",
            };

        public static AuthoringRiskLevel Parse(string? value)
            => value == "read" ? AuthoringRiskLevel.Read
                : value == "mutating" ? AuthoringRiskLevel.Mutating
                : value == "destructive" ? AuthoringRiskLevel.Destructive
                : AuthoringRiskLevel.Unknown;
    }

    public static class AuthoringUndoLevelExtensions
    {
        public static string ToWireValue(this AuthoringUndoLevel value)
            => value switch
            {
                AuthoringUndoLevel.Full => "full",
                AuthoringUndoLevel.Partial => "partial",
                _ => "none",
            };

        public static AuthoringUndoLevel Parse(string? value)
            => value == "full" ? AuthoringUndoLevel.Full
                : value == "partial" ? AuthoringUndoLevel.Partial
                : AuthoringUndoLevel.None;
    }

    public static class AuthoringPlanLevelExtensions
    {
        public static string ToWireValue(this AuthoringPlanLevel value)
            => value switch
            {
                AuthoringPlanLevel.Plan => "plan",
                AuthoringPlanLevel.Validate => "validate",
                _ => "none",
            };

        public static AuthoringPlanLevel Parse(string? value)
            => value == "plan" ? AuthoringPlanLevel.Plan
                : value == "validate" ? AuthoringPlanLevel.Validate
                : AuthoringPlanLevel.None;
    }

    /// <summary>
    /// Outermost g-004 middleware. It only calls its continuation after a
    /// policy decision has allowed the operation; no dry-run path invokes the
    /// terminal in this milestone.
    /// </summary>
    public sealed class AuthoringSafetyMiddleware : IToolExecutionMiddleware
    {
        public AuthoringSafetyPolicy Policy { get; }
        public IToolExecutionScheduler Scheduler { get; }

        public AuthoringSafetyMiddleware(
            AuthoringSafetyPolicy? policy = null,
            IToolExecutionScheduler? scheduler = null)
        {
            Policy = policy ?? new AuthoringSafetyPolicy();
            Scheduler = scheduler ?? new InlineToolExecutionScheduler();
        }

        public AuthoringSafetyMiddleware(ProjectPathPolicy pathPolicy)
            : this(new AuthoringSafetyPolicy(
                pathPolicy ?? throw new ArgumentNullException(nameof(pathPolicy))))
        {
        }

        public async Task<ResponseCallTool> InvokeAsync(ToolCallContext context, ToolCallNext next)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (next == null) throw new ArgumentNullException(nameof(next));

            var invocation = ToolCallInvocationScope.CurrentInvocation;
            if (invocation == null)
            {
                var missingScope = new ToolCallError(
                    ToolCallErrorCodes.SafetyUnsupported,
                    "Tool invocation must enter the authoring safety pipeline.",
                    callId: context.CallId,
                    correlationId: context.CorrelationId,
                    details: new JsonObject { ["reason"] = "missing_invocation_scope" });
                return ResponseCallTool.Error(missingScope).SetRequestID(context.RequestID);
            }

            // Install the policy handle before validation/planning as well as
            // execution. Read-only pilot planners may need to canonicalize a
            // derived target (for example a batch child or an active scene)
            // while producing a confirmation plan.
            var pathPolicy = Policy.PathPolicy;
            using var policyScope = AuthoringSafetyPolicyContext.Push(Policy);
            using var pathPolicyScope = pathPolicy == null
                ? null
                : ProjectPathPolicyContext.Push(pathPolicy);

            // Deadline/cancellation are re-checked at every stage that could
            // otherwise turn an expired call into new authoring state: before
            // plan generation, before confirmation acceptance, and before the
            // transaction opens. g-004's guarded terminal repeats the check
            // immediately before the runner.
            var stopped = StoppedResponse(context, "before_planning");
            if (stopped != null)
                return stopped;

            AuthoringInvocation prepared;
            try
            {
                prepared = Policy.PrepareInvocation(invocation.WithContext(context));
            }
            catch (ToolCallControlException exception)
            {
                var error = exception.ToError();
                error.CallId ??= context.CallId;
                error.CorrelationId ??= context.CorrelationId;
                return ResponseCallTool.Error(error).SetRequestID(context.RequestID);
            }

            stopped = StoppedResponse(context, "before_confirmation");
            if (stopped != null)
                return stopped;

            var decision = Policy.EvaluatePreparedForMiddleware(prepared);
            if (!decision.Allowed)
                return decision.ToErrorResponse();

            if (context.IssueConfirmationOnly)
            {
                return ResponseCallTool.SuccessStructured(new JsonObject
                {
                    ["confirmationRequired"] = false,
                    ["risk"] = decision.Risk,
                    ["undo"] = decision.Undo,
                }).SetRequestID(context.RequestID);
            }

            // Dry-run responses are policy-owned snapshots.  They must never
            // enter the terminal continuation, because arbitrary runners may
            // create objects, dirty scenes, write files, or start deferred
            // Unity work even when a caller asks for a preview.
            if (!string.Equals(context.DryRun, "none", StringComparison.Ordinal))
                return decision.ToDryRunResponse();

            // Preserve the existing final acceptance check before queue admission. A
            // cancellation raised during target re-inspection must not be reclassified by
            // the scheduler and must leave the one-shot confirmation unconsumed.
            stopped = StoppedResponse(context, "before_transaction");
            if (stopped != null)
                return stopped;

            // Scheduling is policy-adjacent rather than ordinary downstream middleware:
            // wait before consuming one-shot confirmation authority or opening an authoring
            // transaction. Missing metadata is conservatively main-thread serialized.
            IToolExecutionLease executionLease;
            try
            {
                executionLease = await Scheduler.AcquireAsync(
                    new ToolExecutionSchedulingRequest(
                        context,
                        prepared.Name,
                        prepared.Runner,
                        decision.RiskLevel,
                        decision.UndoLevel),
                    context.CancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                var stoppedInQueue = StoppedResponse(context, "scheduler_queue");
                if (stoppedInQueue != null) return stoppedInQueue;
                throw;
            }
            using (executionLease)
            {
                stopped = StoppedResponse(context, "after_scheduler_admission");
                if (stopped != null)
                    return stopped;

                // A real queue wait may outlive a plan fingerprint, target state, or
                // token. Re-evaluate only then; immediate inline admission preserves the
                // existing g-005 validation count and authority semantics.
                if (executionLease.Waited)
                {
                    decision = Policy.EvaluatePreparedForMiddleware(prepared);
                    if (!decision.Allowed)
                        return decision.ToErrorResponse();

                    stopped = StoppedResponse(context, "after_scheduler_validation");
                    if (stopped != null)
                        return stopped;
                }

            // Re-resolve write paths after admission and immediately before final
            // confirmation/transaction authority. This is the last managed check
            // before the existing g-004 terminal guard.
            if (prepared.Descriptor?.PathBindings != null
                && prepared.Descriptor.PathBindings.Any(binding => binding != null && binding.Intent != AuthoringPathAccessIntent.Read))
            {
                try
                {
                    prepared = Policy.ReResolveWrites(prepared);
                }
                catch (ToolCallControlException exception)
                {
                    var error = exception.ToError();
                    error.CallId ??= context.CallId;
                    error.CorrelationId ??= context.CorrelationId;
                    return ResponseCallTool.Error(error).SetRequestID(context.RequestID);
                }
            }

            // The confirmation may have been accepted moments before the
            // deadline; never consume its one execution or open an Undo group
            // for a call that is no longer executable.
            stopped = StoppedResponse(context, "before_transaction");
            if (stopped != null)
                return stopped;

            var pendingConfirmation = prepared.PendingConfirmation;
            if (pendingConfirmation != null)
            {
                if (!Policy.ConfirmationPlans.TryConsume(
                    pendingConfirmation.PlanId,
                    pendingConfirmation.PlanHash,
                    pendingConfirmation.ExpiresAtUnixMs,
                    out var consumedRecord)
                    || consumedRecord == null)
                {
                    return AuthoringPolicyResult.Reject(
                        ToolCallErrorCodes.ConfirmationInvalid,
                        "The confirmation was already consumed or is no longer valid.",
                        decision.RiskLevel,
                        decision.UndoLevel,
                        context,
                        requiresConfirmation: true,
                        details: new JsonObject { ["reason"] = "confirmation_already_consumed" })
                        .ToErrorResponse();
                }

                prepared.ApprovedPlan = consumedRecord.Summary.CloneBounded();
                prepared.PendingConfirmation = null;
            }

            prepared.MarkPolicyApproved();
            using var invocationScope = ToolCallInvocationScope.Push(context, prepared);

            IAuthoringTransaction? transaction = null;
            IDisposable? transactionScope = null;
            AuthoringTransactionCheckpoint? sharedCheckpoint = null;
            IAuthoringSharedTransactionReporter? sharedReporter = null;
            var descriptor = prepared.Descriptor;
            // A production batch owns one parent transaction. Child calls are
            // still policy-checked independently, but when they flow through
            // an ambient parent scope they attach records to that group
            // instead of creating nested Undo steps.
            var ambientTransaction = AuthoringTransactionScope.Current;
            var useAmbientTransaction = ambientTransaction != null
                && !string.IsNullOrWhiteSpace(context.ParentCallId)
                && descriptor?.TransactionFactory != null
                && !prepared.SuppressTransaction
                && decision.RiskLevel != AuthoringRiskLevel.Read
                && decision.UndoLevel != AuthoringUndoLevel.None;
            if (useAmbientTransaction)
            {
                sharedReporter = ambientTransaction as IAuthoringSharedTransactionReporter;
                if (sharedReporter == null)
                {
                    return ResponseCallTool.Error(new ToolCallError(
                        ToolCallErrorCodes.AuthoringTransactionFailed,
                        "The shared authoring transaction cannot report child outcomes safely.",
                        callId: context.CallId,
                        correlationId: context.CorrelationId,
                        details: new JsonObject { ["stage"] = "child_begin" }))
                        .SetRequestID(context.RequestID);
                }
                sharedCheckpoint = sharedReporter.CreateCheckpoint();
            }
            if (descriptor?.TransactionFactory != null
                && !prepared.SuppressTransaction
                && decision.RiskLevel != AuthoringRiskLevel.Read
                && decision.UndoLevel != AuthoringUndoLevel.None
                && !useAmbientTransaction)
            {
                try
                {
                    transaction = descriptor.TransactionFactory.Begin(prepared);
                    if (transaction == null)
                        throw new InvalidOperationException("Transaction factory returned no transaction.");
                    transactionScope = AuthoringTransactionScope.Push(transaction);
                }
                catch (ToolCallControlException exception)
                {
                    var error = exception.ToError();
                    error.CallId ??= context.CallId;
                    error.CorrelationId ??= context.CorrelationId;
                    return ResponseCallTool.Error(error).SetRequestID(context.RequestID);
                }
                catch
                {
                    return ResponseCallTool.Error(new ToolCallError(
                        ToolCallErrorCodes.AuthoringTransactionFailed,
                        "The authoring transaction could not be started.",
                        callId: context.CallId,
                        correlationId: context.CorrelationId,
                        details: new JsonObject { ["stage"] = "begin" }))
                        .SetRequestID(context.RequestID);
                }
            }

            try
            {
                ResponseCallTool response = await next(context).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Tool execution continuation returned no response.");

                if (transaction != null)
                {
                    try
                    {
                        if (response.Status == ResponseStatus.Error)
                        {
                            transaction.Abort();
                            // A tool error before any mutation leaves the
                            // Editor exactly as it was; the tool's own error
                            // is the useful one. Only a failure after a
                            // mutation whose rollback cannot be proven is a
                            // transaction-integrity failure.
                            if (response != null
                                && !context.Legacy
                                && RollbackUnproven(transaction.Report))
                            {
                                response.StructuredError = CreateTransactionFailure(
                                    context,
                                    "abort",
                                    transaction.Report).StructuredError;
                            }
                        }
                        else
                        {
                            transaction.Complete();
                        }
                    }
                    catch (Exception exception)
                    {
                        // Complete can fail after Unity has accepted the
                        // mutation (for example when collapsing an Undo
                        // group). Abort this same transaction and surface a
                        // truthful controlled error rather than a successful
                        // response with an unknown Undo state.
                        transaction.Abort();
                        if (context.Legacy)
                        {
                            response = ResponseCallTool
                                .Error("[" + ToolCallErrorCodes.AuthoringTransactionFailed
                                    + "] The authoring transaction could not be completed safely.")
                                .SetRequestID(context.RequestID);
                        }
                        else
                        {
                            response = CreateTransactionFailure(
                                context,
                                "complete",
                                transaction.Report,
                                exception);
                        }
                    }

                    if (response != null)
                        response.Transaction = transaction.Report.ToJson();
                }
                else if (response != null
                    && !context.Legacy
                    && useAmbientTransaction
                    && sharedCheckpoint.HasValue)
                {
                    response.Transaction = sharedReporter!.ReportSince(
                        sharedCheckpoint.Value,
                        decision.UndoLevel).ToJson();
                }
                else if (response != null
                    && response.Status != ResponseStatus.Error
                    && !context.Legacy
                    && decision.RiskLevel != AuthoringRiskLevel.Read
                    && decision.UndoLevel == AuthoringUndoLevel.None)
                {
                    response.Transaction = new JsonObject
                    {
                        ["undo"] = "none",
                        ["mutated"] = false,
                        ["groupId"] = null,
                        ["groupLabel"] = null,
                        ["rollback"] = "none",
                        ["completed"] = true,
                        ["aborted"] = false,
                        ["affectedObjects"] = new JsonArray(),
                    };
                }
                return response
                    ?? throw new InvalidOperationException("Tool execution completed without a response.");
            }
            catch (OperationCanceledException)
            {
                transaction?.Abort();
                if (transaction != null
                    && !context.Legacy
                    && RollbackUnproven(transaction.Report))
                    return CreateTransactionFailure(context, "cancel", transaction.Report);
                if (useAmbientTransaction && !context.Legacy)
                {
                    var cancelled = StoppedResponse(context, "execution")
                        ?? ResponseCallTool.Error(new ToolCallError(
                            ToolCallErrorCodes.Cancelled,
                            "Tool call was cancelled.",
                            callId: context.CallId,
                            correlationId: context.CorrelationId))
                            .SetRequestID(context.RequestID);
                    cancelled.Transaction = sharedReporter!.ReportSince(
                        sharedCheckpoint!.Value,
                        decision.UndoLevel).ToJson();
                    return cancelled;
                }
                // The pipeline classifies a local-transaction cancellation as
                // `cancelled` or `deadline_exceeded` after its own group abort.
                throw;
            }
            catch (ToolCallControlException exception)
            {
                if (transaction == null && !useAmbientTransaction)
                    throw;
                if (transaction != null)
                    transaction.Abort();
                if (context.Legacy)
                    throw;
                if (transaction != null && RollbackUnproven(transaction.Report))
                    return CreateTransactionFailure(context, "error", transaction.Report, exception);
                var controlled = exception.ToError();
                controlled.CallId ??= context.CallId;
                controlled.CorrelationId ??= context.CorrelationId;
                var controlledResponse = ResponseCallTool.Error(controlled).SetRequestID(context.RequestID);
                controlledResponse.Transaction = transaction != null
                    ? transaction.Report.ToJson()
                    : sharedReporter!.ReportSince(sharedCheckpoint!.Value, decision.UndoLevel).ToJson();
                return controlledResponse;
            }
            catch (Exception exception)
            {
                if (transaction == null && !useAmbientTransaction)
                    throw;
                var transactionWasAlreadyAborted = transaction?.Report.Aborted == true;
                if (transaction != null)
                    transaction.Abort();
                if (context.Legacy)
                    throw;
                if (transaction != null && transactionWasAlreadyAborted)
                    return CreateTransactionFailure(context, "complete", transaction.Report, exception);
                if (transaction != null && RollbackUnproven(transaction.Report))
                    return CreateTransactionFailure(context, "exception", transaction.Report, exception);
                // Keep raw exception text out of the controlled contract. An
                // ambient child reports its pending contribution; only the
                // batch owner may abort the shared group.
                var failed = ResponseCallTool.Error(new ToolCallError(
                    ToolCallErrorCodes.ToolExecutionFailed,
                    "Tool execution failed.",
                    callId: context.CallId,
                    correlationId: context.CorrelationId)).SetRequestID(context.RequestID);
                failed.Transaction = transaction != null
                    ? transaction.Report.ToJson()
                    : sharedReporter!.ReportSince(sharedCheckpoint!.Value, decision.UndoLevel).ToJson();
                return failed;
            }
            finally
            {
                transactionScope?.Dispose();
                transaction?.Dispose();
            }
            }
        }

        /// <summary>
        /// True when authoring state changed and the abort could not prove a
        /// complete restoration. An unmutated transaction has nothing to
        /// restore, so its abort never becomes an integrity failure.
        /// </summary>
        private static bool RollbackUnproven(AuthoringTransactionReport report)
            => report.Mutated && report.Rollback != AuthoringRollbackStatus.Complete;

        /// <summary>
        /// Returns the controlled `deadline_exceeded`/`cancelled` response
        /// when the call is no longer executable, or null when it may
        /// proceed. Legacy calls keep g-004's exception-based behavior.
        /// </summary>
        private static ResponseCallTool? StoppedResponse(ToolCallContext context, string stage)
        {
            var deadlinePassed = context.DeadlineUnixMs.HasValue
                && context.DeadlineUnixMs.Value <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var cancelled = context.CancellationToken.IsCancellationRequested;
            if (!deadlinePassed && !cancelled)
                return null;

            if (context.Legacy)
                throw new OperationCanceledException(context.CancellationToken);

            return ResponseCallTool.Error(new ToolCallError(
                deadlinePassed ? ToolCallErrorCodes.DeadlineExceeded : ToolCallErrorCodes.Cancelled,
                deadlinePassed ? "Tool call deadline expired." : "Tool call was cancelled.",
                callId: context.CallId,
                correlationId: context.CorrelationId,
                details: new JsonObject { ["stage"] = stage })).SetRequestID(context.RequestID);
        }

        private static ResponseCallTool CreateTransactionFailure(
            ToolCallContext context,
            string stage,
            AuthoringTransactionReport report,
            Exception? innerException = null)
        {
            _ = innerException;
            var error = new ToolCallError(
                ToolCallErrorCodes.AuthoringTransactionFailed,
                "The authoring transaction could not be completed safely.",
                callId: context.CallId,
                correlationId: context.CorrelationId,
                details: new JsonObject
                {
                    ["stage"] = stage,
                    ["rollback"] = report.Rollback.ToString().ToLowerInvariant(),
                });
            var response = ResponseCallTool.Error(error).SetRequestID(context.RequestID);
            response.Transaction = report.ToJson();
            return response;
        }
    }

    internal static class AuthoringPolicyResultExtensions
    {
        /// <summary>
        /// Copyable retry instruction for callers that only read the legacy
        /// text envelope. It names the controlled wrapper shape without
        /// echoing any argument, path, or credential value.
        /// </summary>
        public const string LegacyRetryInstruction =
            "Retry the same arguments in the controlled form: POST { \"arguments\": { ... }, \"control\": {} }. "
            + "The confirmation_required response contains confirmationPlan and retryWith; resend exactly with "
            + "{ \"control\": { \"confirm\": true, \"confirmation\": { \"planId\", \"planHash\", \"expiresAtUnixMs\" } } }.";

        public static ResponseCallTool ToErrorResponse(this AuthoringPolicyResult result)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            var error = result.ToError();
            var response = ResponseCallTool.Error(error).SetRequestID(result.RequestID ?? string.Empty);
            if (!result.Legacy)
                return response;

            // Legacy callers keep their existing error/status envelope. The
            // structured member is additive; the text block is what they
            // will actually read, so make the stable code and the controlled
            // retry shape visible there.
            var legacyMessage = "[" + error.Code + "] " + error.Message
                + (result.RequiresConfirmation ? " " + LegacyRetryInstruction : string.Empty);
            var legacy = ResponseCallTool.Error(legacyMessage).SetRequestID(result.RequestID ?? string.Empty);
            legacy.StructuredError = error;
            return legacy;
        }
    }
}
