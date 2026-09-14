/*
 * Portions of this file are derived from MCP for Unity (CoplayDev/unity-mcp),
 * Copyright (c) Coplay Inc., licensed under the MIT License.
 * Original: https://github.com/CoplayDev/unity-mcp/blob/main/Server/src/services/tools/unity_docs.py
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
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_Docs
    {
        /// <summary>
        /// Small async HTTP wrapper used by the docs-* tools. Holds a single shared
        /// <see cref="HttpClient"/>, applies a fixed timeout, follows redirects and
        /// caches successful responses in-memory for a short TTL so parallel lookups
        /// of the same URL only hit the network once.
        /// </summary>
        internal static class HttpFetcher
        {
            // Single shared HttpClient — recommended pattern. .NET pools sockets internally.
            private static readonly HttpClient s_client = CreateClient();
            private const string UserAgent = "Unity-MCP-Docs/1.0";
            private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(10);
            private static readonly TimeSpan s_cacheTtl = TimeSpan.FromSeconds(30);

            private static readonly ConcurrentDictionary<string, CacheEntry> s_cache = new();

            private static HttpClient CreateClient()
            {
                var handler = new HttpClientHandler
                {
                    AllowAutoRedirect = true,
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                };
                var client = new HttpClient(handler)
                {
                    Timeout = TimeSpan.FromSeconds(15) // hard cap; per-call CTS enforces s_timeout earlier.
                };
                client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
                client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml");
                return client;
            }

            /// <summary>
            /// Fetch a URL and return (statusCode, body, finalUrl). On network failure
            /// returns (-1, "", url) and sets <paramref name="errorMessage"/>.
            /// </summary>
            public static async Task<FetchResult> FetchAsync(string url)
            {
                if (s_cache.TryGetValue(url, out var cached) && cached.ExpiresAt > DateTime.UtcNow)
                    return new FetchResult(cached.Status, cached.Body, cached.FinalUrl, null);

                using var cts = new CancellationTokenSource(s_timeout);
                try
                {
                    using var resp = await s_client.GetAsync(url, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
                    var status = (int)resp.StatusCode;
                    var body = status == 404
                        ? string.Empty
                        : await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var final = resp.RequestMessage?.RequestUri?.ToString() ?? url;

                    // Cache successful + 404 responses (not 5xx — those can be transient)
                    if (status == 200 || status == 404)
                    {
                        s_cache[url] = new CacheEntry(status, body, final, DateTime.UtcNow + s_cacheTtl);
                    }

                    return new FetchResult(status, body, final, null);
                }
                catch (TaskCanceledException)
                {
                    return new FetchResult(-1, string.Empty, url, $"Timed out after {s_timeout.TotalSeconds:F0}s fetching {url}.");
                }
                catch (HttpRequestException ex)
                {
                    return new FetchResult(-1, string.Empty, url, $"HTTP error fetching {url}: {ex.Message}");
                }
                catch (Exception ex)
                {
                    return new FetchResult(-1, string.Empty, url, $"Unexpected error fetching {url}: {ex.Message}");
                }
            }

            internal readonly struct FetchResult
            {
                public readonly int Status;
                public readonly string Body;
                public readonly string FinalUrl;
                public readonly string? ErrorMessage;

                public FetchResult(int status, string body, string finalUrl, string? errorMessage)
                {
                    Status = status;
                    Body = body;
                    FinalUrl = finalUrl;
                    ErrorMessage = errorMessage;
                }

                public bool IsOk => Status == 200;
                public bool IsNotFound => Status == 404;
                public bool IsNetworkError => Status == -1;
            }

            private readonly struct CacheEntry
            {
                public readonly int Status;
                public readonly string Body;
                public readonly string FinalUrl;
                public readonly DateTime ExpiresAt;

                public CacheEntry(int status, string body, string finalUrl, DateTime expiresAt)
                {
                    Status = status;
                    Body = body;
                    FinalUrl = finalUrl;
                    ExpiresAt = expiresAt;
                }
            }
        }
    }
}
