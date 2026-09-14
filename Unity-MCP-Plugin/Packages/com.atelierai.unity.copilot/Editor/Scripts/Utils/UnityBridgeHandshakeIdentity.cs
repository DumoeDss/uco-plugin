/*
┌──────────────────────────────────────────────────────────────────┐
│  Editor identity advertised through the bridge-identity-v1       │
│  handshake capability so the Node server can pin calls to this   │
│  specific Editor (project path, pid, version, instance id).      │
└──────────────────────────────────────────────────────────────────┘
*/

#nullable enable
using System.Diagnostics;
using System.IO;
using com.AtelierAI.Uco.Framework.Common;
using UnityEngine;

namespace com.AtelierAI.Unity.Copilot.Editor.Utils
{
    /// <summary>
    /// Reads lazily so the handshake always reports the current Editor state even
    /// after domain reloads; every getter is exception-safe because identity
    /// capture must never break the connection sequence.
    /// </summary>
    internal sealed class UnityBridgeHandshakeIdentity : IHandshakeIdentity
    {
        public string? ProjectPath
        {
            get
            {
                try
                {
                    return Path.GetDirectoryName(Application.dataPath);
                }
                catch
                {
                    return null;
                }
            }
        }

        public int? EditorPid
        {
            get
            {
                try
                {
                    using var process = Process.GetCurrentProcess();
                    return process.Id;
                }
                catch
                {
                    return null;
                }
            }
        }

        public string? UnityVersion
        {
            get
            {
                try
                {
                    return string.IsNullOrEmpty(Application.unityVersion) ? null : Application.unityVersion;
                }
                catch
                {
                    return null;
                }
            }
        }

        public string? InstanceId
        {
            get
            {
                try
                {
                    var instanceId = UnityInstanceRegistry.BuildSelfEntry().InstanceId;
                    return string.IsNullOrEmpty(instanceId) ? null : instanceId;
                }
                catch
                {
                    return null;
                }
            }
        }
    }
}
