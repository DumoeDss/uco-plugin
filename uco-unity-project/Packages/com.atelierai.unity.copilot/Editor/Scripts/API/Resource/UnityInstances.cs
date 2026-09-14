/*
 * Design inspired by MCP for Unity (CoplayDev/unity-mcp), Copyright (c) Coplay Inc., MIT License.
 */

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
using System.Text.Json;
using com.AtelierAI.Uco.Framework;
using com.AtelierAI.Uco.Framework.Common;
using com.AtelierAI.Uco.Framework.Common.Model;
using com.IvanMurzak.ReflectorNet.Utils;
using com.AtelierAI.Unity.Copilot.Editor.Utils;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    using Consts = com.AtelierAI.Uco.Framework.Common.Consts;

    [UcoResourceType]
    public partial class Resource_UnityInstances
    {
        public const string UnityInstancesResourceUri = "editor://unity-instances";

        [UcoResource
        (
            Name = "Unity Instances",
            Route = UnityInstancesResourceUri,
            MimeType = Consts.MimeType.TextJson,
            ListResources = nameof(ListAll),
            Description = "List all Unity Editor instances currently registered on this machine (soft discovery). " +
                "Each entry includes port, project path, Unity version, PID, instance id, and liveness flag. " +
                "Cross-instance tool routing is not yet implemented — this resource is discovery-only.",
            Enabled = true
        )]
        public ResponseResourceContent[] GetUnityInstances(string uri)
        {
            return MainThread.Instance.Run(() =>
            {
                var entries = UnityInstanceRegistry.List(includeStale: false);
                var json = JsonSerializer.Serialize(entries);
                return ResponseResourceContent.CreateText(
                    uri: uri,
                    mimeType: Consts.MimeType.TextJson,
                    text: json
                ).MakeArray();
            });
        }

        public ResponseListResource[] ListAll() => new[]
        {
            new ResponseListResource(
                uri: UnityInstancesResourceUri,
                name: "Unity Instances",
                enabled: true,
                mimeType: Consts.MimeType.TextJson)
        };
    }
}
