/*
┌──────────────────────────────────────────────────────────────────┐
│  Author: Ivan Murzak (https://github.com/IvanMurzak)             │
│  Repository: GitHub (https://github.com/IvanMurzak/Unity-MCP)    │
│  Copyright (c) 2025 Ivan Murzak                                  │
│  Licensed under the Apache License, Version 2.0.                 │
│  See the LICENSE file in the project root for more information.  │
└──────────────────────────────────────────────────────────────────┘
*/

#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using com.AtelierAI.Unity.Copilot.Runtime.Utils;
using NUnit.Framework;
using static com.AtelierAI.Uco.Framework.Common.Consts.MCP.Server;

namespace com.AtelierAI.Unity.Copilot.Editor.Tests
{
    /// <summary>
    /// Covers the layered config-loader contract:
    ///   flags > env vars > on-disk config > defaults
    /// implemented by <see cref="EnvironmentUtils.ApplyEnvironmentOverrides"/>.
    /// Since 1.0.3 removed the Cloud connection mode, every config is local
    /// (ConnectionMode.Custom) and token overrides always route to LocalToken.
    /// </summary>
    public class EnvironmentUtilsTests
    {
        const string DiskHost = "http://localhost:24029";
        const string DiskLocalToken = "DISK_LOCAL_TOKEN";

        static UnityCopilotPlugin.UnityConnectionConfig BuildDiskConfig()
        {
            // Simulates a config that came back from disk: explicit values,
            // default mode Custom (matching the plugin's default).
            return new UnityCopilotPlugin.UnityConnectionConfig
            {
                LocalHost = DiskHost,
                LocalToken = DiskLocalToken,
                ConnectionMode = ConnectionMode.Custom,
                AuthOption = AuthOption.required,
                KeepConnected = true,
                KeepServerRunning = false,
                TransportMethod = TransportMethod.streamableHttp
            };
        }

        static IReadOnlyDictionary<string, string> NoArgs()
            => new Dictionary<string, string>();
        static Func<string, string?> NoEnv()
            => _ => null;
        static Func<string, string?> Env(params (string k, string v)[] pairs)
        {
            var dict = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var (k, v) in pairs) dict[k] = v;
            return key => dict.TryGetValue(key, out var val) ? val : null;
        }
        static IReadOnlyDictionary<string, string> Args(params (string k, string v)[] pairs)
        {
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (k, v) in pairs) dict[k] = v;
            return dict;
        }

        // --- Disk-only path: env / args absent ---

        [Test]
        public void Override_DiskOnly_NoEnvNoArgs_LeavesConfigUnchanged()
        {
            var config = BuildDiskConfig();
            var record = EnvironmentUtils.ApplyEnvironmentOverrides(config, NoArgs(), NoEnv());

            Assert.IsFalse(record.HasAny, "Expected no overrides when env and args are empty.");
            Assert.AreEqual(DiskHost, config.LocalHost);
            Assert.AreEqual(DiskLocalToken, config.LocalToken);
            Assert.AreEqual(ConnectionMode.Custom, config.ConnectionMode);
            Assert.AreEqual(AuthOption.required, config.AuthOption);
        }

        // --- Env var override ---

        [Test]
        public void Override_EnvToken_WritesToLocalToken()
        {
            var config = BuildDiskConfig();
            var env = Env((EnvironmentUtils.EnvToken, "ENV_TOKEN"));

            var record = EnvironmentUtils.ApplyEnvironmentOverrides(config, NoArgs(), env);

            Assert.IsTrue(record.HasAny);
            Assert.IsTrue(record.Contains(EnvironmentUtils.FieldLocalToken),
                "Expected the LocalToken backing field to be tracked.");
            Assert.AreEqual("ENV_TOKEN", config.LocalToken);
        }

        // --- Flag override (highest priority, beats env) ---

        [Test]
        public void Override_FlagToken_BeatsEnvToken()
        {
            var config = BuildDiskConfig();
            var args = Args((EnvironmentUtils.FlagToken, "FLAG_TOKEN"));
            var env = Env((EnvironmentUtils.EnvToken, "ENV_TOKEN"));

            EnvironmentUtils.ApplyEnvironmentOverrides(config, args, env);

            Assert.AreEqual("FLAG_TOKEN", config.LocalToken,
                "When both --token and UNITY_MCP_TOKEN are set, the flag must win.");
        }

        [Test]
        public void Override_UnityMcpStyleFlag_BeatsEnvVar()
        {
            // CLI translates --token foo into UNITY_MCP_TOKEN env, but the plugin still
            // accepts --UNITY_MCP_TOKEN=foo style flags directly. They must beat env vars.
            var config = BuildDiskConfig();
            var args = Args((EnvironmentUtils.EnvToken, "FLAG_VIA_FULL_NAME"));
            var env = Env((EnvironmentUtils.EnvToken, "ENV_TOKEN"));

            EnvironmentUtils.ApplyEnvironmentOverrides(config, args, env);

            Assert.AreEqual("FLAG_VIA_FULL_NAME", config.LocalToken);
        }

        // --- Per-field layering ---

        [Test]
        public void Override_PerField_TokenFromEnv_HostFromDisk()
        {
            var config = BuildDiskConfig();
            var env = Env((EnvironmentUtils.EnvToken, "ENV_TOKEN"));

            EnvironmentUtils.ApplyEnvironmentOverrides(config, NoArgs(), env);

            Assert.AreEqual(DiskHost, config.LocalHost,
                "Host must remain at the disk baseline when only the token was overridden.");
            Assert.AreEqual("ENV_TOKEN", config.LocalToken);
        }

        // --- Trailing-slash robustness ---

        [Test]
        public void Override_TrailingSlashOnHostUrl_IsStripped()
        {
            var config = BuildDiskConfig();
            var env = Env((EnvironmentUtils.EnvHost, "http://localhost:5220/"));

            EnvironmentUtils.ApplyEnvironmentOverrides(config, NoArgs(), env);

            Assert.AreEqual("http://localhost:5220", config.LocalHost,
                "Trailing slash must be stripped defensively.");
        }

        [TestCase("http://localhost:5220/", true)]
        [TestCase("http://127.0.0.1:5220", true)]
        [TestCase("http://[::1]:5220/", true)]
        [TestCase("https://example.com", false)]
        [TestCase("not-a-url", false)]
        [TestCase("", false)]
        public void IsLoopbackUrl_RecognisesLoopbackHosts(string url, bool expected)
        {
            Assert.AreEqual(expected, EnvironmentUtils.IsLoopbackUrl(url),
                $"IsLoopbackUrl mismatch for '{url}'.");
        }

        [Test]
        public void Override_RemoteHostUrl_NeverFlipsMode()
        {
            var config = BuildDiskConfig();
            var env = Env((EnvironmentUtils.EnvHost, "https://example.com"));

            EnvironmentUtils.ApplyEnvironmentOverrides(config, NoArgs(), env);

            Assert.AreEqual(ConnectionMode.Custom, config.ConnectionMode);
            Assert.AreEqual("https://example.com", config.LocalHost);
        }

        [Test]
        public void Override_InvalidEnumValue_IsIgnored()
        {
            var config = BuildDiskConfig();
            var env = Env((EnvironmentUtils.EnvConnectionMode, "Bogus"));

            EnvironmentUtils.ApplyEnvironmentOverrides(config, NoArgs(), env);

            Assert.AreEqual(ConnectionMode.Custom, config.ConnectionMode,
                "Unparseable enum values must be silently ignored (legacy Cloud included).");
        }

        [Test]
        public void Override_QuotedValue_IsTrimmed()
        {
            var config = BuildDiskConfig();
            var env = Env((EnvironmentUtils.EnvToken, "\"QUOTED_TOKEN\""));

            EnvironmentUtils.ApplyEnvironmentOverrides(config, NoArgs(), env);

            Assert.AreEqual("QUOTED_TOKEN", config.LocalToken);
        }

        // --- Persistence: overrides MUST NOT be written to disk ---

        [Test]
        public void Persistence_BaselineRoundTripsToDiskWithoutOverrides()
        {
            var config = BuildDiskConfig();
            var env = Env(
                (EnvironmentUtils.EnvHost, "http://localhost:5220"),
                (EnvironmentUtils.EnvToken, "ENV_TOKEN"));

            var record = EnvironmentUtils.ApplyEnvironmentOverrides(config, NoArgs(), env);
            // sanity
            Assert.AreEqual("http://localhost:5220", config.LocalHost);
            Assert.AreEqual("ENV_TOKEN", config.LocalToken);

            // Simulate Save: restore baseline → serialize → restore overrides.
            EnvironmentUtils.ApplyBaseline(config, record);

            Assert.AreEqual(DiskHost, config.LocalHost, "Baseline restore failed for LocalHost.");
            Assert.AreEqual(DiskLocalToken, config.LocalToken, "Baseline restore failed for LocalToken.");

            var json = SerializeForDisk(config);
            // The serialized JSON must NOT contain the runtime override values.
            StringAssert.DoesNotContain("http://localhost:5220", json);
            StringAssert.DoesNotContain("ENV_TOKEN", json);
            StringAssert.Contains(DiskHost, json);
            StringAssert.Contains(DiskLocalToken, json);

            // After serialization, re-apply overrides.
            EnvironmentUtils.ApplyOverrides(config, record);
            Assert.AreEqual("http://localhost:5220", config.LocalHost);
            Assert.AreEqual("ENV_TOKEN", config.LocalToken);
        }

        [Test]
        public void Persistence_EmptyRecord_NoOpOnApplyBaselineAndOverrides()
        {
            var config = BuildDiskConfig();
            var record = new EnvironmentUtils.OverrideRecord();
            EnvironmentUtils.ApplyBaseline(config, record);
            EnvironmentUtils.ApplyOverrides(config, record);
            // No exceptions, values unchanged.
            Assert.AreEqual(DiskHost, config.LocalHost);
        }

        // --- Helpers ---

        static string SerializeForDisk(UnityCopilotPlugin.UnityConnectionConfig config)
        {
            return JsonSerializer.Serialize(config, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Converters = { new JsonStringEnumConverter() }
            });
        }
    }
}
