#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using com.IvanMurzak.McpPlugin.Common.Model;
using com.IvanMurzak.ReflectorNet;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace com.IvanMurzak.McpPlugin.Tests.Mcp
{
    public sealed class ProjectPathPolicyTests
    {
        [Fact]
        public void Resolve_NormalizesContainedAssetsPathAndSafeDisplay()
        {
            using var project = TempProject.Create();
            var policy = new ProjectPathPolicy(new FileSystemProjectPathCanonicalizer(project.Root, caseSensitive: false));

            var resolved = policy.Resolve(
                @"Assets\Scenes/../Prefabs/Block.prefab",
                ProjectPathAccessIntent.Create);

            resolved.FullPath.ShouldBe(Path.Combine(project.Root, "Assets", "Prefabs", "Block.prefab"));
            resolved.RelativePath.ShouldBe("Assets/Prefabs/Block.prefab");
            resolved.RootCategory.ShouldBe(ProjectPathRootCategories.Assets);
            resolved.IsReadOnly.ShouldBeFalse();
        }

        [Fact]
        public void Resolve_TraversalCannotPopAnExplicitRootSegment()
        {
            using var project = TempProject.Create();
            var policy = project.Policy;

            Should.Throw<ProjectPathPolicyException>(() => policy.Resolve(
                "Assets/Scenes/../../outside.txt",
                ProjectPathAccessIntent.Create)).Reason.ShouldBe("traversal");
            Should.Throw<ProjectPathPolicyException>(() => policy.Resolve(
                "Scenes/../../outside.txt",
                ProjectPathAccessIntent.Create)).Reason.ShouldBe("traversal");
        }

        [Theory]
        [InlineData("/tmp/outside.txt")]
        [InlineData("\\\\server\\share\\outside.txt")]
        [InlineData("\\\\?\\C:\\outside.txt")]
        [InlineData("\\\\.\\pipe\\outside")]
        [InlineData("C:relative.txt")]
        [InlineData("https://example.invalid/file")]
        [InlineData("Assets/file\0.txt")]
        [InlineData("Assets//file.txt")]
        [InlineData("Assets\\\\file.txt")]
        public void Resolve_RejectsRootedDeviceUriNulAndMalformedInputs(string input)
        {
            using var project = TempProject.Create();

            var exception = Should.Throw<ProjectPathPolicyException>(() => project.Policy.Resolve(
                input,
                ProjectPathAccessIntent.Create));

            exception.Code.ShouldBe(ToolCallErrorCodes.PathPolicyViolation);
            exception.Message.ShouldNotContain(project.Root, Case.Sensitive);
            exception.Details!.ToJsonString().ShouldNotContain("outside", Case.Sensitive);
        }

        [Fact]
        public void Resolve_ReparseEscapeAndUnresolvedPointFailClosed()
        {
            using var project = TempProject.Create();
            var fake = new FakeCanonicalizer(project.Root, caseSensitive: false);
            fake.AddExisting(Path.Combine(project.Root, "Assets", "Generated"));
            fake.AddReparse(
                Path.Combine(project.Root, "Assets", "Generated"),
                Path.Combine(project.Root, "outside"));
            var policy = new ProjectPathPolicy(fake);

            var escaped = Should.Throw<ProjectPathPolicyException>(() => policy.Resolve(
                "Assets/Generated/new.txt",
                ProjectPathAccessIntent.Create));
            escaped.Reason.ShouldBe("containment");

            fake.SetReparseTarget(
                Path.Combine(project.Root, "Assets", "Generated"),
                null);
            var unresolved = Should.Throw<ProjectPathPolicyException>(() => policy.Resolve(
                "Assets/Generated/new.txt",
                ProjectPathAccessIntent.Create));
            unresolved.Reason.ShouldBe("reparse_unresolved");
        }

        [Fact]
        public void Resolve_ReparsePointContainedTargetIsAllowed()
        {
            using var project = TempProject.Create();
            var fake = new FakeCanonicalizer(project.Root, caseSensitive: false);
            var link = Path.Combine(project.Root, "Assets", "Generated");
            fake.AddExisting(link);
            fake.AddReparse(link, Path.Combine(project.Root, "Assets", "SafeGenerated"));
            var policy = new ProjectPathPolicy(fake);

            var resolved = policy.Resolve("Assets/Generated/new.txt", ProjectPathAccessIntent.Create);

            resolved.RelativePath.ShouldBe("Assets/Generated/new.txt");
        }

        [Fact]
        public void Resolve_TargetIdentityBindsContainedReparseTargetWithoutDisclosingPaths()
        {
            using var project = TempProject.Create();
            var fake = new FakeCanonicalizer(project.Root, caseSensitive: false);
            var link = Path.Combine(project.Root, "Assets", "Generated");
            var targetA = Path.Combine(project.Root, "Assets", "SafeA");
            var targetB = Path.Combine(project.Root, "Assets", "SafeB");
            fake.AddExisting(link);
            fake.AddReparse(link, targetA);
            var policy = new ProjectPathPolicy(fake);

            var first = policy.Resolve("Assets/Generated/new.txt", ProjectPathAccessIntent.Create);
            var equivalent = policy.Resolve(@"assets\GENERATED\new.txt", ProjectPathAccessIntent.Create);
            first.TargetIdentity.ShouldBe(equivalent.TargetIdentity);
            first.TargetIdentity.ShouldStartWith("sha256-");
            first.TargetIdentity.Length.ShouldBe(71);
            first.TargetIdentity.ShouldNotContain(project.Root, Case.Sensitive);
            first.TargetIdentity.ShouldNotContain(targetA, Case.Sensitive);

            fake.SetReparseTarget(link, targetB);
            var retargeted = policy.Resolve("Assets/Generated/new.txt", ProjectPathAccessIntent.Create);
            retargeted.TargetIdentity.ShouldNotBe(first.TargetIdentity);
        }

        [Fact]
        public void Resolve_PackagesReadOnlyAndNonAuthoringRootsCannotBeWritten()
        {
            using var project = TempProject.Create();

            var packageRead = project.Policy.Resolve(
                "Packages/com.example.local/package.json",
                ProjectPathAccessIntent.Read);
            packageRead.IsReadOnly.ShouldBeTrue();
            packageRead.RootCategory.ShouldBe(ProjectPathRootCategories.Packages);

            foreach (var root in new[]
            {
                ProjectPathRootCategories.Packages,
                ProjectPathRootCategories.Library,
                ProjectPathRootCategories.ProjectSettings,
                ProjectPathRootCategories.UserSettings,
            })
            {
                var exception = Should.Throw<ProjectPathPolicyException>(() => project.Policy.Resolve(
                    root + "/file.txt",
                    ProjectPathAccessIntent.Modify));
                exception.Code.ShouldBe(ToolCallErrorCodes.PathPolicyViolation);
            }
        }

        [Fact]
        public void Resolve_OutputRootMustBeRegisteredAndCannotBeSmuggledThroughAssets()
        {
            using var project = TempProject.Create();
            var output = Path.Combine(project.Root, "captures");
            Directory.CreateDirectory(output);
            project.Policy.RegisterOutputRoot("Capture", output);

            var resolved = project.Policy.Resolve(
                "Capture/frame.png",
                ProjectPathAccessIntent.Output);
            resolved.IsReadOnly.ShouldBeFalse();
            resolved.RelativePath.ShouldBe("Capture/frame.png");

            Should.Throw<ProjectPathPolicyException>(() => project.Policy.Resolve(
                "Assets/frame.png",
                ProjectPathAccessIntent.Output)).Reason.ShouldBe("output_root_not_registered");
        }

        [Fact]
        public void Resolve_EquivalentSeparatorsAndCaseShareOneContainedDecision()
        {
            using var project = TempProject.Create();
            var first = project.Policy.Resolve(
                "Assets/Scenes/Block.unity",
                ProjectPathAccessIntent.Read);
            var second = project.Policy.Resolve(
                @"assets\SCENES\Block.unity",
                ProjectPathAccessIntent.Read);

            first.FullPath.ShouldBe(second.FullPath, StringCompareShould.IgnoreCase);
            first.RootCategory.ShouldBe(second.RootCategory);
            first.RelativePath.ShouldBe("Assets/Scenes/Block.unity");
            second.RelativePath.ShouldBe("Assets/SCENES/Block.unity");
        }

        [Fact]
        public void ResolveForWrite_RechecksOwnershipImmediatelyBeforeUse()
        {
            using var project = TempProject.Create();
            var fake = new FakeCanonicalizer(project.Root, caseSensitive: false);
            var link = Path.Combine(project.Root, "Assets", "Generated");
            fake.AddExisting(link);
            fake.AddReparse(link, Path.Combine(project.Root, "Assets", "SafeGenerated"));
            var policy = new ProjectPathPolicy(fake);
            var first = policy.Resolve("Assets/Generated/file.txt", ProjectPathAccessIntent.Modify);

            fake.SetReparseTarget(link, Path.Combine(project.Root, "outside"));
            var exception = Should.Throw<ProjectPathPolicyException>(() => policy.ReResolveBeforeWrite(first));

            exception.Reason.ShouldBe("containment");
            fake.ReparseResolutionCount.ShouldBeGreaterThan(1);
        }

        [Fact]
        public void ApplyToInvocation_ReplacesOnlyDeclaredPathWithCanonicalProjectRelativeValue()
        {
            using var project = TempProject.Create();
            var runner = new FakeRunTool("scene-create")
            {
                ReadOnlyHint = false,
                AuthoringCapability = new AuthoringCapabilityDescriptor
                {
                    MutationKind = AuthoringMutationKind.Create,
                    UndoLevel = AuthoringUndoLevel.Partial,
                    PathBindings = new[]
                    {
                        new AuthoringPathBinding
                        {
                            ArgumentName = "path",
                            Intent = AuthoringPathAccessIntent.Create,
                            RootCategory = ProjectPathRootCategories.Assets,
                            Required = true,
                        },
                    },
                },
            };
            var context = new ToolCallContext
            {
                RequestID = "request",
                CallId = "call",
                CorrelationId = "trace",
                DryRun = "none",
            };
            var arguments = new Dictionary<string, JsonElement>
            {
                ["path"] = JsonElementOf(@"Assets\Scenes/../Scenes/Test.unity"),
                ["name"] = JsonElementOf("untouched"),
            };
            var invocation = new AuthoringInvocation(context, runner.Name, arguments, runner);

            var prepared = project.Policy.ApplyToInvocation(invocation);

            prepared.HasCanonicalArguments.ShouldBeTrue();
            prepared.Arguments["path"].GetString().ShouldBe("Assets/Scenes/Test.unity");
            prepared.Arguments["name"].GetString().ShouldBe("untouched");
            prepared.PathBindings["path"].CanonicalPath.ShouldBe(
                Path.Combine(project.Root, "Assets", "Scenes", "Test.unity"));
            prepared.RawArguments["path"].GetString().ShouldContain("..");
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        public void ApplyToInvocation_AllowsMissingOptionalPathForTrustedAdapterFallback(string? value)
        {
            using var project = TempProject.Create();
            var runner = new FakeRunTool("scene-save")
            {
                ReadOnlyHint = false,
                AuthoringCapability = new AuthoringCapabilityDescriptor
                {
                    MutationKind = AuthoringMutationKind.Modify,
                    UndoLevel = AuthoringUndoLevel.Partial,
                    PathBindings = new[]
                    {
                        new AuthoringPathBinding
                        {
                            ArgumentName = "path",
                            Intent = AuthoringPathAccessIntent.Modify,
                            RootCategory = ProjectPathRootCategories.Assets,
                            Required = false,
                        },
                    },
                },
            };
            var invocation = new AuthoringInvocation(
                new ToolCallContext { RequestID = "request", CallId = "call", CorrelationId = "trace" },
                runner.Name,
                new Dictionary<string, JsonElement>
                {
                    ["path"] = value == null ? JsonDocument.Parse("null").RootElement.Clone() : JsonElementOf(value),
                },
                runner);

            var prepared = project.Policy.ApplyToInvocation(invocation);

            prepared.HasCanonicalArguments.ShouldBeTrue();
            prepared.PathBindings.ShouldBeEmpty();
            prepared.Arguments["path"].ValueKind.ShouldBe(value == null ? JsonValueKind.Null : JsonValueKind.String);
        }

        [Fact]
        public async Task ManagerPassesCanonicalArgumentsToTheRunner()
        {
            using var project = TempProject.Create();
            var runner = new FakeRunTool("path-tool")
            {
                ReadOnlyHint = false,
                AuthoringCapability = new AuthoringCapabilityDescriptor
                {
                    MutationKind = AuthoringMutationKind.Modify,
                    UndoLevel = AuthoringUndoLevel.Full,
                    PathBindings = new[]
                    {
                        new AuthoringPathBinding
                        {
                            ArgumentName = "path",
                            Intent = AuthoringPathAccessIntent.Modify,
                            RootCategory = ProjectPathRootCategories.Assets,
                            Required = true,
                        },
                    },
                },
            };
            var reflector = new Reflector();
            var manager = new McpToolManager(
                NullLogger<McpToolManager>.Instance,
                reflector,
                new ToolRunnerCollection(reflector, null)
                    .Add(new Dictionary<string, IRunTool> { [runner.Name] = runner }),
                new ToolExecutionPipeline(new IToolExecutionMiddleware[]
                {
                    new AuthoringSafetyMiddleware(new AuthoringSafetyPolicy(project.Policy)),
                }));
            var request = new RequestCallTool(
                "request",
                runner.Name,
                new Dictionary<string, JsonElement>
                {
                    ["path"] = JsonElementOf(@"Assets\Scenes/../Scenes/Test.unity"),
                },
                new ToolCallControl
                {
                    CallId = "call",
                    CorrelationId = "trace",
                });

            var response = await manager.RunCallTool(request);

            response.Status.ShouldBe(ResponseStatus.Success);
            runner.ReceivedArguments!["path"].GetString().ShouldBe("Assets/Scenes/Test.unity");
        }

        [Fact]
        public void PathErrorsExposeOnlySafePolicyDetails()
        {
            using var project = TempProject.Create();
            var exception = Should.Throw<ProjectPathPolicyException>(() => project.Policy.Resolve(
                "Assets/../../secret.txt",
                ProjectPathAccessIntent.Create));
            var details = exception.Details!.ToJsonString();

            details.ShouldContain("traversal");
            details.ShouldContain("Assets");
            details.ShouldNotContain(project.Root, Case.Sensitive);
            details.ShouldNotContain("secret", Case.Sensitive);
            details.ShouldNotContain(Environment.UserName, Case.Sensitive);
        }

        [Fact]
        public void PathErrorsDoNotEchoUntrustedDeclaredRoot()
        {
            using var project = TempProject.Create();
            var untrustedRoot = Path.Combine(project.Root, "private-user-data");

            var exception = Should.Throw<ProjectPathPolicyException>(() => project.Policy.Resolve(
                "",
                ProjectPathAccessIntent.Create,
                untrustedRoot));

            exception.RootCategory.ShouldBe("unknown");
            exception.Details!.ToJsonString().ShouldNotContain("private-user-data", Case.Sensitive);
            exception.Details!.ToJsonString().ShouldNotContain(project.Root, Case.Sensitive);
        }

        private static JsonElement JsonElementOf(string value)
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
            return document.RootElement.Clone();
        }

        private sealed class TempProject : IDisposable
        {
            private TempProject(string root)
            {
                Root = root;
                Policy = new ProjectPathPolicy(new FileSystemProjectPathCanonicalizer(root, caseSensitive: false));
            }

            public string Root { get; }
            public ProjectPathPolicy Policy { get; }

            public static TempProject Create()
            {
                var root = Path.Combine(Path.GetTempPath(), "mcp-path-policy-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.Combine(root, "Assets"));
                Directory.CreateDirectory(Path.Combine(root, "Packages"));
                Directory.CreateDirectory(Path.Combine(root, "Library"));
                Directory.CreateDirectory(Path.Combine(root, "ProjectSettings"));
                Directory.CreateDirectory(Path.Combine(root, "UserSettings"));
                return new TempProject(root);
            }

            public void Dispose()
            {
                try
                {
                    if (Directory.Exists(Root))
                        Directory.Delete(Root, recursive: true);
                }
                catch
                {
                    // Test cleanup must not hide the assertion that ran.
                }
            }
        }

        private sealed class FakeCanonicalizer : IProjectPathCanonicalizer
        {
            private readonly StringComparer _comparer;
            private readonly HashSet<string> _existing;
            private readonly Dictionary<string, string?> _reparseTargets;

            public FakeCanonicalizer(string projectRoot, bool caseSensitive)
            {
                ProjectRoot = Path.GetFullPath(projectRoot);
                IsCaseSensitive = caseSensitive;
                _comparer = caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
                _existing = new HashSet<string>(_comparer);
                _reparseTargets = new Dictionary<string, string?>(_comparer);
            }

            public string ProjectRoot { get; }
            public bool IsCaseSensitive { get; }
            public int ReparseResolutionCount { get; private set; }

            public string Canonicalize(string path) => Path.GetFullPath(path);

            public bool Exists(string fullPath)
                => _existing.Contains(Canonicalize(fullPath))
                    || File.Exists(fullPath)
                    || Directory.Exists(fullPath);

            public bool IsDirectory(string fullPath)
                => Directory.Exists(fullPath);

            public bool IsReparsePoint(string fullPath)
                => _reparseTargets.ContainsKey(Canonicalize(fullPath));

            public bool TryResolveReparsePoint(string fullPath, out string resolvedFullPath)
            {
                ReparseResolutionCount++;
                var key = Canonicalize(fullPath);
                if (_reparseTargets.TryGetValue(key, out var target) && !string.IsNullOrWhiteSpace(target))
                {
                    resolvedFullPath = Canonicalize(target);
                    return true;
                }

                resolvedFullPath = string.Empty;
                return false;
            }

            public void AddExisting(string path) => _existing.Add(Canonicalize(path));

            public void AddReparse(string path, string? target)
            {
                AddExisting(path);
                _reparseTargets[Canonicalize(path)] = target == null ? null : Canonicalize(target);
            }

            public void SetReparseTarget(string path, string? target)
                => _reparseTargets[Canonicalize(path)] = target == null ? null : Canonicalize(target);
        }

        private sealed class FakeRunTool : IRunTool
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
            public McpToolType ToolType => McpToolType.Standard;
            public bool? ReadOnlyHint { get; set; }
            public bool? DestructiveHint { get; set; }
            public bool? IdempotentHint { get; set; }
            public bool? OpenWorldHint { get; set; }
            public AuthoringCapabilityDescriptor? AuthoringCapability { get; set; }
            public int TokenCount => 0;
            public IReadOnlyDictionary<string, JsonElement>? ReceivedArguments { get; private set; }

            public Task<ResponseCallTool> Run(
                string requestId,
                IReadOnlyDictionary<string, JsonElement>? namedParameters,
                CancellationToken cancellationToken = default)
            {
                _ = cancellationToken;
                ReceivedArguments = namedParameters;
                return Task.FromResult(ResponseCallTool.Success().SetRequestID(requestId));
            }
        }
    }
}
