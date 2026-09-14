/*
┌──────────────────────────────────────────────────────────────────┐
│  Author: Ivan Murzak (https://github.com/IvanMurzak)             │
│  Repository: GitHub (https://github.com/IvanMurzak/MCP-Plugin-dotnet) │
│  Copyright (c) 2025 Ivan Murzak                                  │
│  Licensed under the Apache License, Version 2.0.                 │
│  See the LICENSE file in the project root for more information.  │
└──────────────────────────────────────────────────────────────────┘
*/

using System;

namespace com.AtelierAI.Uco.Framework
{
    /// <summary>
    /// Bounded exception diagnostics shared by the tool manager and the
    /// execution pipeline (COCli-09): the wire envelope keeps the exception
    /// type, message, and stack so script compile failures and inner
    /// exceptions stay diagnosable at the CLI, without leaking unbounded
    /// payloads. Text members are flattened to a single line and hard-capped.
    /// </summary>
    internal static class ExceptionDiagnostics
    {
        public static System.Text.Json.Nodes.JsonObject? BoundedExceptionDetails(Exception ex)
        {
            try
            {
                var root = ex.GetBaseException();
                return new System.Text.Json.Nodes.JsonObject
                {
                    ["exceptionType"] = BoundDetailText(root.GetType().FullName ?? root.GetType().Name, 160),
                    ["exceptionMessage"] = BoundDetailText(root.Message, 1024),
                    ["exceptionStackTrace"] = string.IsNullOrEmpty(root.StackTrace)
                        ? null
                        : BoundDetailText(root.StackTrace, 2048),
                };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// A bounded, single-line failure message: "TypeName: message" from
        /// the base exception, capped at <paramref name="maximum"/> chars.
        /// </summary>
        public static string BoundedFailureMessage(Exception ex, string prefix, int maximum = 512)
        {
            try
            {
                var root = ex.GetBaseException();
                var typeName = root.GetType().Name;
                var message = string.IsNullOrEmpty(root.Message)
                    ? typeName
                    : $"{typeName}: {root.Message}";
                return $"{prefix}: {BoundDetailText(message, maximum)}";
            }
            catch
            {
                return prefix;
            }
        }

        public static string BoundDetailText(string value, int maximum)
        {
            var flattened = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return flattened.Length <= maximum ? flattened : flattened.Substring(0, maximum);
        }
    }
}
