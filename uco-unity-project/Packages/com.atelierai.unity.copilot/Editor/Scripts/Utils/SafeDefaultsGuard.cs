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
using System.Net;
using System.Net.Sockets;

namespace com.AtelierAI.Unity.Copilot.Editor.Utils
{
    /// <summary>
    /// Centralised "is the current configuration safe to start?" policy. Implements the
    /// fail-closed posture described on <see cref="UnityCopilotPlugin.UnityConnectionConfig.AllowLanBind"/>:
    /// the local MCP server only auto-starts on a non-loopback bind / over insecure remote HTTP
    /// when the user has explicitly opted in via the corresponding flag, AND any LAN bind
    /// additionally requires a token when <c>ForceTokenWhenLanBind</c> is true.
    ///
    /// The guard is purely advisory at the URL-parsing layer (no Unity API access) so it can be
    /// unit-tested without entering Play mode. The only Unity-side side effect lives in
    /// <see cref="ShouldRejectStart"/>, which reads the editor's current config.
    /// </summary>
    public static class SafeDefaultsGuard
    {
        /// <summary>
        /// Returns true when the given URL has a host that is NOT a loopback address.
        /// Loopback covers <c>localhost</c>, <c>127.0.0.0/8</c>, and <c>::1</c>.
        /// Wildcard binds (<c>0.0.0.0</c>, <c>::</c>, <c>*</c>, <c>+</c>) and concrete LAN IPs
        /// or hostnames are treated as non-loopback (and therefore "LAN bind" for the purposes
        /// of the guard).
        ///
        /// Defensive parsing: an unparseable / empty URL returns false so the guard never
        /// blocks startup on a corrupted host string — the existing host validation in the
        /// connection layer remains the authoritative parser.
        /// </summary>
        public static bool IsNonLoopbackUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return false;

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return false;

            return IsNonLoopbackHost(uri.Host);
        }

        /// <summary>
        /// Returns true when the bare host string represents a non-loopback bind target.
        /// Used by <see cref="IsNonLoopbackUrl"/> and by callers that already extracted
        /// the host (e.g. server arg builders).
        /// </summary>
        public static bool IsNonLoopbackHost(string? host)
        {
            if (string.IsNullOrWhiteSpace(host))
                return false;

            var h = host.Trim().Trim('[', ']');

            // Wildcard / all-interfaces literals used by Kestrel and friends.
            if (h == "0.0.0.0" || h == "::" || h == "*" || h == "+")
                return true;

            // Common loopback hostname.
            if (h.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                return false;

            if (IPAddress.TryParse(h, out var ip))
            {
                // IPAddress.Loopback covers 127.0.0.1 and ::1; IsLoopback covers the whole
                // 127.0.0.0/8 range and the v6 loopback. Anything else (LAN, public, link-local)
                // is treated as non-loopback.
                return !IPAddress.IsLoopback(ip);
            }

            // Non-IP hostnames (LAN names like "build-server.local", public hostnames, etc.)
            // are not loopback. The only special case we already handled is "localhost".
            return true;
        }

        /// <summary>
        /// True when the URL uses plain <c>http://</c> AND targets a non-loopback host.
        /// HTTPS endpoints, loopback HTTP, and unparseable URLs all return false — the
        /// guard does not block them.
        /// </summary>
        public static bool IsInsecureRemoteHttpUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return false;

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return false;

            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
                return false;

            return IsNonLoopbackHost(uri.Host);
        }

        /// <summary>
        /// Pure (no Unity API) evaluation of the fail-closed policy. Returns true when a
        /// caller should refuse to start the local MCP server with the supplied configuration.
        /// <paramref name="reason"/> contains a human-readable explanation when the result
        /// is <c>true</c>; otherwise it is the empty string.
        ///
        /// Policy:
        ///   1. LocalHost resolves to a non-loopback host AND AllowLanBind is false → reject.
        ///   2. LocalHost is a non-loopback bind, AllowLanBind is true, but
        ///      ForceTokenWhenLanBind is true and either AuthOption != required or Token is
        ///      empty → reject (LAN exposure without a token is a footgun).
        ///   3. Otherwise → safe.
        ///
        /// Insecure remote HTTP is NOT covered here — it is an advisory the configurators
        /// emit when they generate client config blocks; the server-start path does not gate
        /// on it because the local server is the *target* of the URL, not the *consumer*.
        /// </summary>
        public static bool ShouldRejectStart(
            string? localHost,
            bool allowLanBind,
            bool forceTokenWhenLanBind,
            global::com.AtelierAI.Uco.Framework.Common.Consts.MCP.Server.AuthOption authOption,
            string? token,
            out string reason)
        {
            reason = string.Empty;

            var isLan = IsNonLoopbackUrl(localHost);
            if (!isLan)
                return false;

            if (!allowLanBind)
            {
                reason =
                    $"LocalHost '{localHost}' binds to a non-loopback interface but " +
                    "'Allow LAN Bind' is disabled. Open the Game Developer window → " +
                    "'Network Safety' section and enable 'Allow LAN Bind' to expose the " +
                    "MCP server to the network. The safe default is loopback only.";
                return true;
            }

            if (forceTokenWhenLanBind)
            {
                var tokenRequired = authOption == global::com.AtelierAI.Uco.Framework.Common.Consts.MCP.Server.AuthOption.required;
                if (!tokenRequired || string.IsNullOrEmpty(token))
                {
                    reason =
                        $"LocalHost '{localHost}' binds to a non-loopback interface but " +
                        "authorization is not 'required' or the token is empty. Either " +
                        "set Authorization to 'required' and generate a token, or disable " +
                        "'Force Token When LAN Bind' in the Game Developer window (not " +
                        "recommended).";
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Editor-side wrapper that reads the current <see cref="UnityCopilotPluginEditor"/>
        /// configuration and delegates to the pure overload. Kept as a thin facade so the
        /// pure overload is the unit-testable surface and the editor surface stays small.
        /// </summary>
        public static bool ShouldRejectStart(out string reason)
        {
            return ShouldRejectStart(
                localHost: UnityCopilotPluginEditor.LocalHost,
                allowLanBind: UnityCopilotPluginEditor.AllowLanBind,
                forceTokenWhenLanBind: UnityCopilotPluginEditor.ForceTokenWhenLanBind,
                authOption: UnityCopilotPluginEditor.AuthOption,
                token: UnityCopilotPluginEditor.Token,
                out reason);
        }
    }
}
