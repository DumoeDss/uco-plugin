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

namespace com.AtelierAI.Unity.Copilot.Editor.Branding
{
    // Central place for every user-visible product string. UI code references these
    // constants instead of inlining literals — so when the product name changes again,
    // only this file needs editing.
    //
    // Phase C rename baseline: "MCP" → "Unity Copilot". The name "Unity Copilot" itself
    // is provisional; expect another pass.
    public static class ProductInfo
    {
        public const string ProductName  = "Unity Copilot";
        public const string ParentBrand  = "AI Game Developer";
        public const string ProductLong  = ParentBrand + " — " + ProductName;

        // Stable package id used for EditorPrefs key namespacing. Decoupled from the UPM
        // package name on disk so a UPM rename doesn't silently wipe per-user state — the
        // PrefsKeyMigration helper does that migration explicitly.
        public const string PackageId    = "com.atelierai.unity.copilot";
        public const string LegacyPackageId = "com.ivanmurzak.unity.mcp";

        // Menu item paths (passed to Unity's [MenuItem] attribute).
        public const string MainWindowMenu = "Window/" + ProductLong + " %&a";
        public const string ToolsMenuRoot  = "Tools/" + ParentBrand;
        public const string ServerMenuRoot = ToolsMenuRoot + "/Server";

        // EditorWindow titleContent / GetWindow titles.
        public const string ToolsWindowTitle     = ProductName + " Tools";
        public const string ResourcesWindowTitle = ProductName + " Resources";
        public const string PromptsWindowTitle   = ProductName + " Prompts";

        // Debug.Log prefix for Plugin-originating messages.
        public const string LogPrefix = "[" + ProductName + "]";

        // Server status label fragments (rendered in MainWindow status row).
        public const string ServerLabelPrefix = "Server";

        // Notification titles surfaced via EditorUtility.DisplayDialog.
        public const string NotifyBinariesDeletedTitle  = ProductName + " Server Binaries Deleted";
        public const string NotifyBinariesNotFoundTitle = ProductName + " Server Binaries Not Found";
        public const string NotifyInspectorUnavailable  = ProductName + " Inspector Unavailable";

        // Project Settings provider — path users see under Edit > Project Settings.
        public const string SettingsPath     = "Project/" + ParentBrand;
        public const string SettingsKeywords = "AI Copilot Unity " + ParentBrand;

        // SessionState / EditorPrefs key prefixes for transient + persistent state.
        // The hash suffix is appended by callers using Application.dataPath FNV-1a.
        public const string SessionStatePrefix = "Copilot_";
        public const string EditorPrefsPrefix  = "UnityCopilot";  // ProductName without spaces
    }
}
