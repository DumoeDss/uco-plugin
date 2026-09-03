/*
┌────────────────────────────────────────────────────────────────────────┐
│  Author: Ivan Murzak (https://github.com/IvanMurzak)                   │
│  Repository: GitHub (https://github.com/IvanMurzak/MCP-Plugin-dotnet)  │
│  Copyright (c) 2025 Ivan Murzak                                        │
│  Licensed under the Apache License, Version 2.0.                       │
│  See the LICENSE file in the project root for more information.        │
└────────────────────────────────────────────────────────────────────────┘
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using com.IvanMurzak.ReflectorNet.Utils;

namespace com.IvanMurzak.McpPlugin.Utils
{
    public static class ArgumentUtils
    {
        public static void RemoveRequestIDParameters(JsonNode schema, MethodInfo methodInfo)
        {
            // Pre-fetch schema nodes to avoid repeated lookups
            var properties = schema[JsonSchema.Properties]?.AsObject();
            var required = schema[JsonSchema.Required]?.AsArray();

            // If neither exists, there's nothing to modify
            if (properties == null && required == null)
                return;

            var injectedTypeIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var param in methodInfo.GetParameters())
            {
                // Use IsDefined for faster attribute checking
                if (param.IsDefined(typeof(RequestIDAttribute), false) ||
                    param.IsDefined(typeof(ToolCallContextAttribute), false))
                {
                    var name = param.Name;
                    if (string.IsNullOrEmpty(name)) continue;
                    injectedTypeIds.Add(TypeUtils.GetSchemaTypeId(param.ParameterType));
                    // Remove from properties
                    properties?.Remove(name);

            // Remove from required
                    if (required != null)
                    {
                        var nodeToRemove = required.FirstOrDefault(x => x?.GetValue<string>() == name);
                        if (nodeToRemove != null)
                            required.Remove(nodeToRemove);
                    }
                }
            }

            ReplaceInvalidDefinitions(schema);

            // ReflectorNet builds definitions while it creates every parameter
            // schema.  An injected context parameter can therefore leave its
            // own $defs entries (including an error schema for its opaque
            // extension-data dictionary) after its property is removed.  Drop
            // only the definitions belonging to injected parameter types; the
            // rest of ReflectorNet's definitions are intentionally preserved,
            // including generic definitions it emits for compatibility.
            RemoveInjectedDefinitions(schema, injectedTypeIds);
        }

        private static void ReplaceInvalidDefinitions(JsonNode schema)
        {
            if (schema is not JsonObject schemaObject ||
                schemaObject[JsonSchema.Defs] is not JsonObject definitions ||
                definitions.Count == 0)
                return;

            // ReflectorNet can return an error object for a generic
            // Dictionary<string, JsonElement> definition when its reusable
            // JsonElement schema node is already attached elsewhere.  Keep
            // the definition and its $ref stable, but expose the intended
            // open object shape instead of leaking an implementation error
            // into the public MCP catalog.
            foreach (var definition in definitions.ToList())
            {
                if (definition.Value is JsonObject definitionObject &&
                    definitionObject.ContainsKey(JsonSchema.Error))
                {
                    definitions[definition.Key] = new JsonObject
                    {
                        [JsonSchema.Type] = JsonSchema.Object,
                        [JsonSchema.AdditionalProperties] = true,
                    };
                }
            }
        }

        private static void RemoveInjectedDefinitions(
            JsonNode schema,
            IReadOnlyCollection<string> injectedTypeIds)
        {
            if (schema is not JsonObject schemaObject ||
                schemaObject[JsonSchema.Defs] is not JsonObject definitions ||
                definitions.Count == 0 ||
                injectedTypeIds.Count == 0)
                return;

            var injectedDefinitions = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Stack<string>(injectedTypeIds);
            while (pending.Count > 0)
            {
                var name = pending.Pop();
                if (!injectedDefinitions.Add(name))
                    continue;

                if (definitions.TryGetPropertyValue(name, out var definition))
                    CollectDefinitionReferences(definition, pending);
            }

            // Keep an injected definition if a public parameter still refers
            // to it.  This avoids deleting a shared type when a method happens
            // to expose the same type both internally and publicly.
            var publicReferences = new Stack<string>();
            foreach (var property in schemaObject)
            {
                if (string.Equals(property.Key, JsonSchema.Defs, StringComparison.Ordinal))
                    continue;
                CollectDefinitionReferences(property.Value, publicReferences);
            }

            var publicDefinitions = new HashSet<string>(StringComparer.Ordinal);
            while (publicReferences.Count > 0)
            {
                var name = publicReferences.Pop();
                if (!publicDefinitions.Add(name))
                    continue;

                if (definitions.TryGetPropertyValue(name, out var definition))
                    CollectDefinitionReferences(definition, publicReferences);
            }

            foreach (var name in injectedDefinitions
                .Where(name => !publicDefinitions.Contains(name))
                .ToList())
            {
                definitions.Remove(name);
            }

            if (definitions.Count == 0)
                schemaObject.Remove(JsonSchema.Defs);
        }

        private static void CollectDefinitionReferences(JsonNode? node, Stack<string> pending)
        {
            if (node is JsonObject objectNode)
            {
                foreach (var property in objectNode)
                {
                    if (string.Equals(property.Key, JsonSchema.Ref, StringComparison.Ordinal) &&
                        property.Value is JsonValue referenceValue &&
                        referenceValue.TryGetValue<string>(out var reference) &&
                        reference.StartsWith(JsonSchema.RefValue, StringComparison.Ordinal))
                    {
                        var name = Uri.UnescapeDataString(
                                reference.Substring(JsonSchema.RefValue.Length))
                            .Replace("~1", "/")
                            .Replace("~0", "~");
                        if (name.Length > 0)
                            pending.Push(name);
                    }
                    else
                    {
                        CollectDefinitionReferences(property.Value, pending);
                    }
                }
                return;
            }

            if (node is JsonArray arrayNode)
            {
                foreach (var item in arrayNode)
                    CollectDefinitionReferences(item, pending);
            }
        }
    }
}
