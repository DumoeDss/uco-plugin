/*
┌────────────────────────────────────────────────────────────────────────┐
│  Author: Ivan Murzak (https://github.com/IvanMurzak/MCP-Plugin-dotnet)  │
│  Repository: GitHub (https://github.com/IvanMurzak/MCP-Plugin-dotnet)  │
│  Copyright (c) 2025 Ivan Murzak                                        │
│  Licensed under the Apache License, Version 2.0.                       │
│  See the LICENSE file in the project root for more information.        │
└────────────────────────────────────────────────────────────────────────┘
*/

using System;

namespace com.AtelierAI.Uco.Framework.Common
{
    /// <summary>
    /// Optional host-supplied Editor identity carried by the version handshake
    /// under the <c>bridge-identity-v1</c> capability. A host that registers an
    /// identity lets the server pin and verify calls to this specific Editor
    /// (project path, process id, Unity version, stable instance id); hosts
    /// that do not register one keep the legacy handshake shape.
    /// </summary>
    public interface IHandshakeIdentity
    {
        string? ProjectPath { get; }
        int? EditorPid { get; }
        string? UnityVersion { get; }
        string? InstanceId { get; }
    }

    /// <summary>
    /// Immutable carrier for hosts that can capture identity once at build time.
    /// Members are read lazily through the supplied delegates so a live host can
    /// refresh them across domain reloads.
    /// </summary>
    public sealed class DelegatingHandshakeIdentity : IHandshakeIdentity
    {
        readonly Func<string?>? _projectPath;
        readonly Func<int?>? _editorPid;
        readonly Func<string?>? _unityVersion;
        readonly Func<string?>? _instanceId;

        public DelegatingHandshakeIdentity(
            Func<string?>? projectPath = null,
            Func<int?>? editorPid = null,
            Func<string?>? unityVersion = null,
            Func<string?>? instanceId = null)
        {
            _projectPath = projectPath;
            _editorPid = editorPid;
            _unityVersion = unityVersion;
            _instanceId = instanceId;
        }

        public string? ProjectPath => Invoke(_projectPath);
        public int? EditorPid => Invoke(_editorPid);
        public string? UnityVersion => Invoke(_unityVersion);
        public string? InstanceId => Invoke(_instanceId);

        static string? Invoke(Func<string?>? getter)
        {
            try
            {
                return getter?.Invoke();
            }
            catch
            {
                // Identity capture must never break the connection handshake.
                return null;
            }
        }

        static int? Invoke(Func<int?>? getter)
        {
            try
            {
                return getter?.Invoke();
            }
            catch
            {
                return null;
            }
        }
    }
}
