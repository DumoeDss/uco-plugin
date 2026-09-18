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
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using com.AtelierAI.Unity.Copilot.Runtime.Utils;
using Microsoft.Extensions.Logging;
using UnityEngine;

namespace com.AtelierAI.Unity.Copilot
{
    public partial class UnityCopilotPluginEditor
    {
        public static string ResourcesFileName => "uco-config";

        /// <summary>
        /// Pre-rename filename (uco 0.3.0 / plugin 0.76.0 renamed
        /// AI-Game-Developer-Config.json to uco-config.json). When only the
        /// legacy file exists it is read and then migrated: saved under the
        /// new name and the legacy file removed.
        /// </summary>
        public static string LegacyResourcesFileName => "AI-Game-Developer-Config";

        /// <summary>True when only the legacy-named config exists right now.</summary>
        static bool LegacyConfigNeedsMigration
        {
            get
            {
                var newPath = Path.GetFullPath(Path.Combine(ProjectRootPath, $"UserSettings/{ResourcesFileName}.json"));
                var legacyPath = Path.GetFullPath(Path.Combine(ProjectRootPath, $"UserSettings/{LegacyResourcesFileName}.json"));
                return !File.Exists(newPath) && File.Exists(legacyPath);
            }
        }

        /// <summary>Project-relative path of the config actually present on disk.</summary>
        public static string EffectiveAssetsFilePath
        {
            get
            {
                var newPath = $"UserSettings/{ResourcesFileName}.json";
                var legacyPath = $"UserSettings/{LegacyResourcesFileName}.json";
                if (File.Exists(Path.GetFullPath(Path.Combine(ProjectRootPath, newPath))))
                    return newPath;
                return File.Exists(Path.GetFullPath(Path.Combine(ProjectRootPath, legacyPath)))
                    ? legacyPath
                    : newPath;
            }
        }

        /// <summary>
        /// One-shot rename migration: after a config was loaded from the
        /// legacy filename, persist it under the new name and delete the old
        /// file so the project converges on UserSettings/uco-config.json.
        /// </summary>
        static void MigrateLegacyConfigFileIfNeeded()
        {
            try
            {
                if (!LegacyConfigNeedsMigration)
                    return;
                var legacyPath = Path.GetFullPath(Path.Combine(ProjectRootPath, $"UserSettings/{LegacyResourcesFileName}.json"));
                var newPath = Path.GetFullPath(Path.Combine(ProjectRootPath, $"UserSettings/{ResourcesFileName}.json"));
                File.Copy(legacyPath, newPath, overwrite: false);
                File.Delete(legacyPath);
                _logger.LogWarning("{method}: migrated {legacy} to {new} (uco rename); the old file was removed.",
                    nameof(MigrateLegacyConfigFileIfNeeded), LegacyResourcesFileName, ResourcesFileName);
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "{method}: legacy config migration failed; falling back to the legacy name for this session.",
                    nameof(MigrateLegacyConfigFileIfNeeded));
            }
        }

        /// <summary>
        /// Project-relative path used by Unity's AssetDatabase API.
        /// Do NOT use this with System.IO File/Directory operations — use <see cref="AssetsFileAbsolutePath"/> instead.
        /// </summary>
        public static string AssetsFilePath => $"UserSettings/{ResourcesFileName}.json";

        /// <summary>
        /// Gets the Unity project root folder path (the folder that contains the Assets directory).
        /// Uses <see cref="Application.dataPath"/> to ensure correctness regardless of the process working directory.
        /// </summary>
        public static string ProjectRootPath => Path.GetDirectoryName(Application.dataPath)!;

        /// <summary>
        /// Absolute path to the config file for use with System.IO File/Directory operations.
        /// Built from <see cref="ProjectRootPath"/> and <see cref="AssetsFilePath"/> to keep paths consistent.
        /// </summary>
        public static string AssetsFileAbsolutePath => Path.GetFullPath(Path.Combine(ProjectRootPath, AssetsFilePath));


        /// <summary>
        /// Resets the config file to its default state.
        /// </summary>
        public static void ResetConfig()
        {
            Instance.unityConnectionConfig.SetDefault();
            var plugin = CurrentPlugin;
            if (plugin != null)
                Instance.ApplyConfigToUcoPlugin(plugin);
            Instance.Save(captureCurrentToolStates: false);
        }

#if UNITY_EDITOR
        public static UnityEngine.TextAsset AssetFile => UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.TextAsset>(EffectiveAssetsFilePath);
#endif

        UnityConnectionConfig GetOrCreateConfig() => GetOrCreateConfig(out _);
        UnityConnectionConfig GetOrCreateConfig(out bool wasCreated)
        {
            wasCreated = false;
            try
            {
                // Both Edit mode and Play mode read from the same UserSettings JSON file
                // Use the absolute path so File.Exists/ReadAllText resolve correctly regardless of CWD.
                // The effective path prefers the renamed uco-config.json and falls back to
                // the legacy AI-Game-Developer-Config.json until the one-shot migration runs.
                var effectiveAbsolute = Path.GetFullPath(Path.Combine(ProjectRootPath, EffectiveAssetsFilePath));
                var json = File.Exists(effectiveAbsolute) ? File.ReadAllText(effectiveAbsolute) : null;
                if (!string.IsNullOrWhiteSpace(json))
                    MigrateLegacyConfigFileIfNeeded();

                UnityConnectionConfig? config = null;
                try
                {
                    config = string.IsNullOrWhiteSpace(json)
                        ? null
                        : JsonSerializer.Deserialize<UnityConnectionConfig>(
                            // Legacy 1.0.2-and-earlier configs may carry the removed Cloud
                            // connection mode; JsonStringEnumConverter would throw on it.
                            json!.Replace("\"connectionMode\": \"Cloud\"", "\"connectionMode\": \"Custom\"")
                                  .Replace("\"ConnectionMode\": \"Cloud\"", "\"ConnectionMode\": \"Custom\""),
                            new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true,
                            Converters = { new JsonStringEnumConverter() }
                        });
                }
                catch (Exception e)
                {
                    _logger.LogCritical(e, "{method}: <color=red><b>{file}</b> file is corrupted at <i>{path}</i></color>",
                        nameof(GetOrCreateConfig), ResourcesFileName, AssetsFilePath);
                }
                // Track whether the config originated from a deserialized disk file. The
                // safe-defaults migration below only applies to configs that pre-existed on
                // disk — a freshly created config already starts with the safe defaults.
                var loadedFromDisk = config != null;
                if (config == null)
                {
                    _logger.LogWarning("{method}: <color=orange><b>Creating {file}</b> file at <i>{path}</i></color>",
                        nameof(GetOrCreateConfig), ResourcesFileName, AssetsFilePath);
                    config = new UnityConnectionConfig();
                    wasCreated = true;
                }
                if (string.IsNullOrEmpty(config.LocalToken))
                {
                    config.LocalToken = GenerateToken();
                    wasCreated = true;
                }

                // ── Safe-defaults (fail-closed) one-shot migration ─────────────────────────
                //
                // Newly created configs default AllowLanBind/AllowInsecureRemoteHttp to false so
                // a fresh project never starts the server on 0.0.0.0 without explicit consent.
                // However, projects that EXISTED before this change may already have LocalHost set
                // to a non-loopback URL (e.g. 0.0.0.0 or a LAN IP) on purpose. Silently dropping
                // their server start would be a regression. Detect that case here and grant the
                // opt-in once, with a one-time LogWarning so the user knows it happened and can
                // turn it back off if they did not intend it.
                //
                // Triggers ONLY when:
                //   • The config was actually loaded from disk (loadedFromDisk == true)
                //   • LocalHost parses as a URL with a non-loopback host
                //   • AllowLanBind is currently false (default)
                // The migration sets AllowLanBind = true and flags `wasCreated` so the Save() at
                // the end of construction persists the new flag immediately. AllowInsecureRemoteHttp
                // is NOT auto-granted — it gates the configurators (which are advisory) and only
                // affects emitted client configs, not server start, so a one-time warning at start
                // is sufficient there.
                if (loadedFromDisk && !config.AllowLanBind && !string.IsNullOrEmpty(config.LocalHost))
                {
                    if (com.AtelierAI.Unity.Copilot.Editor.Utils.SafeDefaultsGuard.IsNonLoopbackUrl(config.LocalHost))
                    {
                        _logger.LogWarning(
                            "{method}: <color=orange>Detected pre-existing LAN/all-interfaces LocalHost ({host}) without AllowLanBind. " +
                            "Granting AllowLanBind=true once to preserve current behaviour. " +
                            "Open the Game Developer window and turn it off if you did not intend to expose the server to the network.</color>",
                            nameof(GetOrCreateConfig), config.LocalHost);
                        config.AllowLanBind = true;
                        wasCreated = true; // force Save() so the migration persists.
                    }
                }

                return config;
            }
            catch (Exception e)
            {
                _logger.LogCritical(e, "{method}: <color=red><b>{file}</b> file can't be loaded from <i>{path}</i></color>",
                    nameof(GetOrCreateConfig), ResourcesFileName, AssetsFilePath);
                throw;
            }
        }

        public void Save(bool captureCurrentToolStates = true)
        {
#if UNITY_EDITOR
            Validate();
            try
            {
                var directory = Path.GetDirectoryName(AssetsFileAbsolutePath);
                if (!Directory.Exists(directory))
                    Directory.CreateDirectory(directory!);

                unityConnectionConfig ??= new UnityConnectionConfig();

                if (captureCurrentToolStates)
                {
                    var enabledToolNames = Tools?.GetAllTools()
                        ?.Select(t => new UnityConnectionConfig.CopilotFeature(t.Name, Tools.IsToolEnabled(t.Name)))
                        ?.ToList();

                    var enabledPromptNames = Prompts?.GetAllPrompts()
                        ?.Select(p => new UnityConnectionConfig.CopilotFeature(p.Name, Prompts.IsPromptEnabled(p.Name)))
                        ?.ToList();

                    var enabledResourceNames = Resources?.GetAllResources()
                        ?.Select(r => new UnityConnectionConfig.CopilotFeature(r.Name, Resources.IsResourceEnabled(r.Name)))
                        ?.ToList();

                    unityConnectionConfig.Tools = enabledToolNames != null && enabledToolNames.Count > 0
                        ? enabledToolNames
                        : UnityConnectionConfig.DefaultTools;

                    unityConnectionConfig.Prompts = enabledPromptNames != null && enabledPromptNames.Count > 0
                        ? enabledPromptNames
                        : UnityConnectionConfig.DefaultPrompts;

                    unityConnectionConfig.Resources = enabledResourceNames != null && enabledResourceNames.Count > 0
                        ? enabledResourceNames
                        : UnityConnectionConfig.DefaultResources;
                }

                // Runtime-only overrides (env vars / CLI flags) MUST NOT be persisted to disk.
                // Temporarily restore the disk-baseline values for any overridden field, serialize,
                // then re-apply the overrides so the in-memory config keeps its runtime values.
                // The "in-memory keeps overrides" property is critical: callers (UI, configurators,
                // SignalR client) hold references to unityConnectionConfig and continue reading
                // from it after Save returns. Restoring overrides ensures behaviour is unchanged.
                var hasRuntimeOverrides = RuntimeOverrides != null && RuntimeOverrides.HasAny;
                if (hasRuntimeOverrides)
                    EnvironmentUtils.ApplyBaseline(unityConnectionConfig, RuntimeOverrides!);
                try
                {
                    var json = JsonSerializer.Serialize(unityConnectionConfig, new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        Converters = { new JsonStringEnumConverter() }
                    });
                    File.WriteAllText(AssetsFileAbsolutePath, json);
                }
                finally
                {
                    if (hasRuntimeOverrides)
                        EnvironmentUtils.ApplyOverrides(unityConnectionConfig, RuntimeOverrides!);
                }

                var assetFile = AssetFile;
                if (assetFile != null)
                    UnityEditor.EditorUtility.SetDirty(assetFile);
                else
                    UnityEditor.AssetDatabase.ImportAsset(AssetsFilePath);
            }
            catch (Exception e)
            {
                _logger.LogCritical(e, "{method}: <color=red><b>{file}</b> file can't be saved at <i>{path}</i></color>",
                    nameof(Save), ResourcesFileName, AssetsFilePath);
            }
#else
            // do nothing in runtime builds
            return;
#endif
        }
    }
}
