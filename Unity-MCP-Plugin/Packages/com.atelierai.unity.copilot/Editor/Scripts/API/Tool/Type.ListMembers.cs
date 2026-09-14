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
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using com.AtelierAI.Uco.Framework;
using com.IvanMurzak.ReflectorNet.Utils;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_Type
    {
        public const string TypeListMembersToolId = "type-list-members";
        [UcoTool
        (
            TypeListMembersToolId,
            Title = "Type / List Members",
            ReadOnlyHint = true,
            DestructiveHint = false,
            IdempotentHint = true,
            Enabled = true
        )]
        [UcoSkillDescription("List the members of a C# type (methods, properties, fields, events, constructors) " +
            "by reflection across all loaded assemblies. Verify what a type actually exposes in THIS Unity version " +
            "before calling it via script-execute / reflection-method-call.")]
        [UcoSkillBody("List the members of a C# type via reflection — the way to verify your knowledge of an API " +
            "against the ACTUAL loaded assembly (LLM training data can lag the installed Unity version, and packages add members).\n\n" +
            "## Inputs\n\n" +
            "- `typeName` — full type name preferred (e.g. `UnityEngine.Camera`, `UnityEditor.EditorGUILayout`). Simple names work when unambiguous.\n" +
            "- `memberKinds` (default `All`) — comma-separated subset: `Method,Property,Field,Event,Constructor`.\n" +
            "- `includeInherited` (default `true`) — `false` for members declared only on this exact type.\n" +
            "- `publicOnly` (default `false`) — `true` to hide non-public members.\n" +
            "- `maxResults` (default `200`) — cap to keep the response manageable; `0` = no limit.\n\n" +
            "## Output\n\n" +
            "JSON: `{ type, count, totalFound, truncated, members[] }`. Each member has `kind`, `name`, `isStatic`, `isPublic`, plus " +
            "`returnType`+`params[]` (methods/constructors), `memberType`+`canRead`+`canWrite` (properties), or `memberType` (fields/events). " +
            "Use it to confirm a member exists and its exact signature BEFORE invoking via script-execute or reflection-method-call.")]
        [Description("List the members of a C# type by reflection (methods/properties/fields/events/constructors) with signatures. " +
            "Use to verify what a type exposes in the current Unity version before calling it.")]
        public string ListMembers
        (
            [Description("Full C# type name. Examples: 'UnityEngine.Camera', 'UnityEditor.AssetDatabase', 'UnityEngine.Rigidbody'. " +
                "Simple names like 'Camera' work when unambiguous.")]
            string typeName,

            [Description("Comma-separated member kinds to include: 'Method', 'Property', 'Field', 'Event', 'Constructor', or 'All' (default). " +
                "Example: 'Property,Field'.")]
            string memberKinds = "All",

            [Description("Include inherited members (default true). Set false to see only members declared on this exact type.")]
            bool includeInherited = true,

            [Description("Only list public members (default false; non-public members are included).")]
            bool publicOnly = false,

            [Description("Maximum members to return (default 200). 0 = no limit. Truncation is reported in the output.")]
            int maxResults = 200,

            [Description("Pretty-print the JSON output (default false).")]
            bool writeIndented = false
        )
        {
            var type = TypeUtils.GetType(typeName);
            if (type == null)
                throw new ArgumentException($"Type '{typeName}' not found in any loaded assembly. " +
                    "Use the full type name including namespace (e.g. 'UnityEngine.Camera').", nameof(typeName));

            var kinds = ParseMemberKinds(memberKinds);
            bool want(string k) => kinds.Contains("ALL") || kinds.Contains(k);

            // Public|NonPublic|Instance|Static gives the full surface; DeclaredOnly drops inherited.
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            if (!includeInherited)
                flags |= BindingFlags.DeclaredOnly;

            var members = new List<object>();

            if (want("Method"))
            {
                foreach (var m in type.GetMethods(flags))
                {
                    if (m.IsSpecialName) continue; // property get/set, operators, event add/remove
                    if (publicOnly && !m.IsPublic) continue;
                    members.Add(new
                    {
                        kind = "Method",
                        name = m.Name,
                        returnType = FriendlyTypeName(m.ReturnType),
                        isStatic = m.IsStatic,
                        isPublic = m.IsPublic,
                        @params = m.GetParameters()
                            .Select(p => new { name = p.Name, type = FriendlyTypeName(p.ParameterType) })
                            .ToArray(),
                    });
                }
            }

            if (want("Property"))
            {
                foreach (var p in type.GetProperties(flags))
                {
                    var getter = p.GetMethod;
                    var setter = p.SetMethod;
                    var isPublic = (getter != null && getter.IsPublic) || (setter != null && setter.IsPublic);
                    if (publicOnly && !isPublic) continue;
                    members.Add(new
                    {
                        kind = "Property",
                        name = p.Name,
                        memberType = FriendlyTypeName(p.PropertyType),
                        isStatic = (getter != null && getter.IsStatic) || (setter != null && setter.IsStatic),
                        isPublic = isPublic,
                        canRead = p.CanRead,
                        canWrite = p.CanWrite,
                    });
                }
            }

            if (want("Field"))
            {
                foreach (var f in type.GetFields(flags))
                {
                    if (f.IsSpecialName) continue; // compiler-generated backing fields
                    if (publicOnly && !f.IsPublic) continue;
                    members.Add(new
                    {
                        kind = "Field",
                        name = f.Name,
                        memberType = FriendlyTypeName(f.FieldType),
                        isStatic = f.IsStatic,
                        isPublic = f.IsPublic,
                        isReadonly = f.IsInitOnly || f.IsLiteral,
                    });
                }
            }

            if (want("Event"))
            {
                foreach (var e in type.GetEvents(flags))
                {
                    var add = e.GetAddMethod(nonPublic: true);
                    if (publicOnly && add != null && !add.IsPublic) continue;
                    members.Add(new
                    {
                        kind = "Event",
                        name = e.Name,
                        memberType = FriendlyTypeName(e.EventHandlerType),
                        isStatic = add != null && add.IsStatic,
                        isPublic = add == null || add.IsPublic,
                    });
                }
            }

            if (want("Constructor"))
            {
                foreach (var c in type.GetConstructors(flags))
                {
                    if (publicOnly && !c.IsPublic) continue;
                    members.Add(new
                    {
                        kind = "Constructor",
                        name = type.Name,
                        isStatic = c.IsStatic,
                        isPublic = c.IsPublic,
                        @params = c.GetParameters()
                            .Select(p => new { name = p.Name, type = FriendlyTypeName(p.ParameterType) })
                            .ToArray(),
                    });
                }
            }

            var totalFound = members.Count;
            var truncated = maxResults > 0 && totalFound > maxResults;
            if (maxResults > 0 && totalFound > maxResults)
                members = members.Take(maxResults).ToList();

            var resultObj = new
            {
                type = type.FullName,
                count = members.Count,
                totalFound = totalFound,
                truncated = truncated,
                members = members,
            };
            return JsonSerializer.Serialize(resultObj, new JsonSerializerOptions
            {
                WriteIndented = writeIndented,
            });
        }

        static HashSet<string> ParseMemberKinds(string? memberKinds)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(memberKinds))
            {
                set.Add("ALL");
                return set;
            }
            foreach (var part in memberKinds.Split(','))
            {
                var k = part.Trim();
                if (k.Length > 0)
                    set.Add(k.ToUpperInvariant());
            }
            if (set.Count == 0)
                set.Add("ALL");
            return set;
        }

        // Render a runtime Type as a readable, callable name: keeps namespace, renders generics
        // as `Ns.G<T>` (not the CLR `Ns.G`1[[T, …]]`) and arrays as `T[]`.
        static string FriendlyTypeName(Type? t)
        {
            if (t == null) return "null";
            if (t.IsArray) return FriendlyTypeName(t.GetElementType()) + "[]";
            if (t.IsByRef) return FriendlyTypeName(t.GetElementType());
            if (t.IsGenericParameter) return t.Name;
            if (t.IsGenericType)
            {
                var def = t.IsGenericTypeDefinition ? t : t.GetGenericTypeDefinition();
                var baseName = def.FullName ?? def.Name;
                var tick = baseName.IndexOf('`');
                if (tick >= 0) baseName = baseName.Substring(0, tick);
                var args = string.Join(", ", t.GetGenericArguments().Select(FriendlyTypeName));
                return $"{baseName}<{args}>";
            }
            return t.FullName ?? t.Name;
        }
    }
}
