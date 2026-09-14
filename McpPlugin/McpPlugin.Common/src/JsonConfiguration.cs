/*
┌────────────────────────────────────────────────────────────────────────┐
│  Author: Ivan Murzak (https://github.com/IvanMurzak)                   │
│  Repository: GitHub (https://github.com/IvanMurzak/MCP-Plugin-dotnet)  │
│  Copyright (c) 2025 Ivan Murzak                                        │
│  Licensed under the Apache License, Version 2.0.                       │
│  See the LICENSE file in the project root for more information.        │
└────────────────────────────────────────────────────────────────────────┘
*/

using System.Text.Json;
using com.IvanMurzak.ReflectorNet;

namespace com.AtelierAI.Uco.Framework.Common
{
    public static class JsonConfiguration
    {
        /// <summary>
        /// Copies JSON serialization settings from the <paramref name="reflector"/> onto a
        /// plain <see cref="JsonSerializerOptions"/>. Used by the WebSocket transport
        /// (<c>WebSocketConnectionProvider</c>) to ensure the same JSON configuration as
        /// the rest of the system.
        /// </summary>
        public static void ConfigureJsonSerializer(Reflector reflector, JsonSerializerOptions options)
        {
            var src = reflector.JsonSerializerOptions;

            options.DefaultIgnoreCondition = src.DefaultIgnoreCondition;
            options.PropertyNamingPolicy = src.PropertyNamingPolicy;
            options.WriteIndented = src.WriteIndented;

            foreach (var converter in src.Converters)
                options.Converters.Add(converter);
        }
    }
}
