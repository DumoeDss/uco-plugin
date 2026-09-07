#nullable enable
using System.Collections.Generic;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using com.AtelierAI.Unity.Copilot.Editor.Utils;
using com.IvanMurzak.McpPlugin;
using com.IvanMurzak.McpPlugin.Common.Model;
using NUnit.Framework;
using UnityEngine;

namespace com.AtelierAI.Unity.Copilot.Editor.Tests
{
    [TestFixture]
    public class ToolsManifestGeneratorTests
    {
        [Test]
        public void BuildToolsArray_PreservesHintsSchemasAndOrdinalOrdering()
        {
            var schema = JsonNode.Parse(
                "{\"type\":\"object\",\"additionalProperties\":false," +
                "\"$defs\":{\"Value\":{\"customKeyword\":\"retained\"}}," +
                "\"$ref\":\"#/$defs/Value\"}")!;
            var metadata = new StubRunTool(
                "ä-tool",
                schema,
                readOnlyHint: true,
                destructiveHint: false,
                idempotentHint: null,
                openWorldHint: false);
            var legacy = new StubRunTool("a-tool", JsonNode.Parse("true")!);
            var upper = new StubRunTool("Z-tool", JsonNode.Parse("{}")!);

            var first = ToolsManifestGenerator.BuildToolsArray(new IRunTool[] { metadata, legacy, upper });
            var second = ToolsManifestGenerator.BuildToolsArray(new IRunTool[] { upper, metadata, legacy });

            Assert.AreEqual("Z-tool", first[0]!["name"]!.GetValue<string>());
            Assert.AreEqual("a-tool", first[1]!["name"]!.GetValue<string>());
            Assert.AreEqual("ä-tool", first[2]!["name"]!.GetValue<string>());
            Assert.AreEqual(first.ToJsonString(), second.ToJsonString());

            var entry = first[2]!.AsObject();
            Assert.IsTrue(entry["readOnlyHint"]!.GetValue<bool>());
            Assert.IsFalse(entry["destructiveHint"]!.GetValue<bool>());
            Assert.IsTrue(entry.ContainsKey("idempotentHint"));
            Assert.IsNull(entry["idempotentHint"]);
            Assert.IsFalse(entry["openWorldHint"]!.GetValue<bool>());
            var inputSchema = entry["inputSchema"]!.AsObject();
            Assert.IsFalse(inputSchema["additionalProperties"]!.GetValue<bool>());
            Assert.AreEqual(
                "retained",
                inputSchema["$defs"]!["Value"]!["customKeyword"]!.GetValue<string>());
            Assert.AreEqual("#/$defs/Value", inputSchema["$ref"]!.GetValue<string>());
            var outputSchema = entry["outputSchema"]!.AsObject();
            Assert.IsTrue(
                outputSchema["additionalProperties"]!["customKeyword"]!.GetValue<bool>());
        }

        [Test]
        public void GenerateManifest_WritesExactUtf8FileWithoutBomAndWithOneFinalNewline()
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "unity-tools-manifest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var path = Path.Combine(directory, ToolsManifestGenerator.ManifestFileName);
                var tools = new IRunTool[]
                {
                    new StubRunTool("z-tool", JsonNode.Parse("true")!, destructiveHint: false),
                    new StubRunTool("a-tool", JsonNode.Parse("{}")!, readOnlyHint: true),
                };

                Assert.AreEqual(2, ToolsManifestGenerator.GenerateManifest(tools, path));
                var bytes = File.ReadAllBytes(path);
                Assert.Greater(bytes.Length, 3);
                Assert.IsFalse(bytes.Take(3).SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf }));
                Assert.AreEqual((byte)'\n', bytes[bytes.Length - 1]);
                Assert.AreNotEqual((byte)'\n', bytes[bytes.Length - 2]);

                var actual = JsonNode.Parse(File.ReadAllText(path))!.AsArray();
                var expected = ToolsManifestGenerator.BuildToolsArray(tools);
                Assert.AreEqual(expected.ToJsonString(), actual.ToJsonString());
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
        }

        [Test]
        public void ShippedManifest_RetainsControlledG006ReleaseView()
        {
            var plugin = UnityCopilotPluginEditor.CurrentPlugin;
            Assert.IsNotNull(plugin, "The editor plugin must expose the authoritative live registry.");
            var tools = plugin!.McpManager!.ToolManager!.GetAllTools().ToArray();
            var live = ToolsManifestGenerator.BuildToolsArray(tools);
            var excludedLiveNames = tools.Select(tool => tool.Name)
                .Where(name => name == "editor-application-request-close"
                    || name == "type-list-members")
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            Assert.AreEqual(163 + excludedLiveNames.Length, live.Count,
                "Only explicitly named sibling tools may sit outside the controlled release view.");

            var projectRoot = Path.GetDirectoryName(Application.dataPath)!;
            var manifestPath = Path.Combine(
                projectRoot,
                "Packages",
                "com.atelierai.unity.copilot",
                ToolsManifestGenerator.ManifestFileName);
            var bytes = File.ReadAllBytes(manifestPath);
            Assert.IsFalse(bytes.Take(3).SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf }));
            Assert.AreEqual((byte)'\n', bytes[bytes.Length - 1]);
            Assert.AreNotEqual((byte)'\n', bytes[bytes.Length - 2]);

            var actual = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsArray();
            Assert.AreEqual(163, actual.Count,
                "The g-006 release adds seven operation tools to the 156-tool parent.");
            var names = actual.Select(entry => entry!["name"]!.GetValue<string>()).ToArray();
            var expectedNames = tools.Select(tool => tool.Name)
                .Where(name => name != "editor-application-request-close"
                    && name != "type-list-members")
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            CollectionAssert.AreEqual(expectedNames, names);

            var toolsByName = tools.ToDictionary(tool => tool.Name, StringComparer.Ordinal);
            foreach (var node in actual)
            {
                var entry = node!.AsObject();
                var name = entry["name"]!.GetValue<string>();
                var tool = toolsByName[name];
                Assert.AreEqual(tool.ReadOnlyHint, NullableBoolean(entry["readOnlyHint"]), name);
                Assert.AreEqual(tool.DestructiveHint, NullableBoolean(entry["destructiveHint"]), name);
                Assert.AreEqual(tool.IdempotentHint, NullableBoolean(entry["idempotentHint"]), name);
                Assert.AreEqual(tool.OpenWorldHint, NullableBoolean(entry["openWorldHint"]), name);
                Assert.AreEqual(
                    (tool.ExecutionScheduling?.ExecutionAffinity
                        ?? ToolExecutionAffinity.MainThread).ToWireValue(),
                    entry["executionAffinity"]!.GetValue<string>(), name);
                Assert.AreEqual(tool.ExecutionScheduling?.ThreadSafeRead == true,
                    entry["threadSafeRead"]!.GetValue<bool>(), name);
            }
        }

        static bool? NullableBoolean(JsonNode? node)
            => node == null ? null : node.GetValue<bool>();


        private sealed class StubRunTool : IRunTool
        {
            public StubRunTool(
                string name,
                JsonNode inputSchema,
                bool? readOnlyHint = null,
                bool? destructiveHint = null,
                bool? idempotentHint = null,
                bool? openWorldHint = null)
            {
                Name = name;
                InputSchema = inputSchema;
                ReadOnlyHint = readOnlyHint;
                DestructiveHint = destructiveHint;
                IdempotentHint = idempotentHint;
                OpenWorldHint = openWorldHint;
            }

            public string Name { get; }
            public bool Enabled { get; set; } = true;
            public string? Title => null;
            public string? Description => null;
            public MethodInfo? Method => null;
            public string? SkillDescription => null;
            public string? SkillBody => null;
            public JsonNode? InputSchema { get; }
            public JsonNode? OutputSchema => JsonNode.Parse(
                "{\"type\":\"object\",\"additionalProperties\":{\"customKeyword\":true}}")!;
            public bool? ReadOnlyHint { get; }
            public bool? DestructiveHint { get; }
            public bool? IdempotentHint { get; }
            public bool? OpenWorldHint { get; }
            public int TokenCount => 0;

            public Task<ResponseCallTool> Run(
                string requestId,
                IReadOnlyDictionary<string, JsonElement>? namedParameters,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(ResponseCallTool.Success().SetRequestID(requestId));
            }
        }
    }
}
