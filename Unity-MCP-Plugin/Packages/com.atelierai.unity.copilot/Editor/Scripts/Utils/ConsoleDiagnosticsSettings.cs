/*
┌──────────────────────────────────────────────────────────────────┐
│  Console-diagnostics settings (COCli-07): bridge/tool diagnostics │
│  default to the collector-only channel; mirroring is opt-in.      │
└──────────────────────────────────────────────────────────────────┘
*/

#nullable enable
using com.AtelierAI.Unity.Copilot.Utils;
using UnityEditor;

namespace com.AtelierAI.Unity.Copilot.Editor.Utils
{
    /// <summary>
    /// Editor preference controlling whether the plugin/bridge/tool
    /// diagnostics channel also mirrors into the Unity Editor Console for
    /// diagnosis. Default off: the Console stays reserved for product and
    /// Unity output; <c>console-get-logs</c> with a source filter always
    /// returns the channel's entries.
    /// </summary>
    [InitializeOnLoad]
    internal static class ConsoleDiagnosticsSettings
    {
        public const string MirrorKey = "UnityCopilot.MirrorDiagnosticsToConsole";

        public static bool MirrorDiagnosticsToConsole
        {
            get => EditorPrefs.GetBool(MirrorKey, false);
            set
            {
                EditorPrefs.SetBool(MirrorKey, value);
                PluginDiagnostics.MirrorToConsole = value;
            }
        }

        static ConsoleDiagnosticsSettings()
            => PluginDiagnostics.MirrorToConsole = MirrorDiagnosticsToConsole;
    }
}
