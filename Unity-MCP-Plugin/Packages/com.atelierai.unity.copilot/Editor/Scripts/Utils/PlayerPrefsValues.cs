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
using UnityEngine;

namespace com.AtelierAI.Unity.Copilot.Editor.Utils
{
    /// <summary>
    /// A Boolean preference stored in cocli's legacy-compatible key space.
    /// </summary>
    public struct PlayerPrefsBool
    {
        const string Prefix = "Boolean:";

        public string Key { get; }
        public string InternalKey { get; }
        public bool DefaultValue { get; }

        public bool Value
        {
            get => PlayerPrefs.GetInt(InternalKey, DefaultValue ? 1 : 0) == 1;
            set => PlayerPrefs.SetInt(InternalKey, value ? 1 : 0);
        }

        public PlayerPrefsBool(string key, bool defaultValue = default)
        {
            Key = key;
            InternalKey = Prefix + key;
            DefaultValue = defaultValue;
        }
    }

    /// <summary>
    /// A string preference stored in cocli's legacy-compatible key space.
    /// </summary>
    public struct PlayerPrefsString
    {
        const string Prefix = "String:";

        public string Key { get; }
        public string InternalKey { get; }
        public string DefaultValue { get; }

        public string Value
        {
            get => PlayerPrefs.GetString(InternalKey, DefaultValue);
            set => PlayerPrefs.SetString(InternalKey, value);
        }

        public PlayerPrefsString(string key, string defaultValue = "")
        {
            Key = key;
            InternalKey = Prefix + key;
            DefaultValue = defaultValue;
        }
    }

    /// <summary>
    /// An integer preference stored in cocli's legacy-compatible key space.
    /// </summary>
    public struct PlayerPrefsInt
    {
        const string Prefix = "Int32:";

        public string Key { get; }
        public string InternalKey { get; }
        public int DefaultValue { get; }

        public int Value
        {
            get => PlayerPrefs.GetInt(InternalKey, DefaultValue);
            set => PlayerPrefs.SetInt(InternalKey, value);
        }

        public PlayerPrefsInt(string key, int defaultValue = default)
        {
            Key = key;
            InternalKey = Prefix + key;
            DefaultValue = defaultValue;
        }
    }
}
