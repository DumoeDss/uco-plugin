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
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using com.IvanMurzak.ReflectorNet;
using Microsoft.Extensions.Logging;

namespace com.AtelierAI.Uco.Framework
{
    public class ToolRunnerCollection : IDictionary<string, IRunTool>, IReadOnlyDictionary<string, IRunTool>
    {
        readonly Dictionary<string, IRunTool> _runners = new(StringComparer.Ordinal);
        readonly Reflector reflector;
        readonly ILogger? _logger;

        public ToolRunnerCollection(Reflector reflector, ILogger? logger)
        {
            this.reflector = reflector ?? throw new ArgumentNullException(nameof(reflector));
            _logger = logger;
            _logger?.LogTrace("Ctor.");
        }

        public IRunTool this[string key]
        {
            get => _runners[key];
            set => _runners[key] = GuardedRunTool.Wrap(value);
        }

        public Dictionary<string, IRunTool>.KeyCollection Keys => _runners.Keys;
        public Dictionary<string, IRunTool>.ValueCollection Values => _runners.Values;
        public int Count => _runners.Count;
        public IEqualityComparer<string> Comparer => _runners.Comparer;
        public bool IsReadOnly => false;

        ICollection<string> IDictionary<string, IRunTool>.Keys => _runners.Keys;
        ICollection<IRunTool> IDictionary<string, IRunTool>.Values => _runners.Values;
        IEnumerable<string> IReadOnlyDictionary<string, IRunTool>.Keys => _runners.Keys;
        IEnumerable<IRunTool> IReadOnlyDictionary<string, IRunTool>.Values => _runners.Values;

        public void Add(string key, IRunTool value)
            => _runners.Add(key, GuardedRunTool.Wrap(value));

        public bool TryAdd(string key, IRunTool value)
            => _runners.TryAdd(key, GuardedRunTool.Wrap(value));

        public bool ContainsKey(string key) => _runners.ContainsKey(key);
        public bool ContainsValue(IRunTool value) => _runners.ContainsValue(value);
        public bool Remove(string key) => _runners.Remove(key);
        public bool TryGetValue(string key, out IRunTool value) => _runners.TryGetValue(key, out value!);
        public void Clear() => _runners.Clear();
        public int EnsureCapacity(int capacity) => _runners.EnsureCapacity(capacity);
        public void TrimExcess() => _runners.TrimExcess();
        public void TrimExcess(int capacity) => _runners.TrimExcess(capacity);

        public Dictionary<string, IRunTool>.Enumerator GetEnumerator() => _runners.GetEnumerator();
        IEnumerator<KeyValuePair<string, IRunTool>> IEnumerable<KeyValuePair<string, IRunTool>>.GetEnumerator()
            => _runners.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => _runners.GetEnumerator();

        void ICollection<KeyValuePair<string, IRunTool>>.Add(KeyValuePair<string, IRunTool> item)
            => Add(item.Key, item.Value);
        bool ICollection<KeyValuePair<string, IRunTool>>.Contains(KeyValuePair<string, IRunTool> item)
            => ((ICollection<KeyValuePair<string, IRunTool>>)_runners).Contains(item);
        void ICollection<KeyValuePair<string, IRunTool>>.CopyTo(
            KeyValuePair<string, IRunTool>[] array,
            int arrayIndex)
            => ((ICollection<KeyValuePair<string, IRunTool>>)_runners).CopyTo(array, arrayIndex);
        bool ICollection<KeyValuePair<string, IRunTool>>.Remove(KeyValuePair<string, IRunTool> item)
            => ((ICollection<KeyValuePair<string, IRunTool>>)_runners).Remove(item);

        public ToolRunnerCollection Add(IEnumerable<ToolMethodData> methods)
        {
            foreach (var method in methods.Where(resource => !string.IsNullOrEmpty(resource.Attribute?.Name)))
                this[method.Attribute.Name] = RunToolFactory.Create(method, reflector, _logger);

            return this;
        }

        public ToolRunnerCollection Add(IDictionary<string, IRunTool> runners)
        {
            if (runners == null)
                throw new ArgumentNullException(nameof(runners));

            foreach (var runner in runners)
                Add(runner.Key, runner.Value);

            return this;
        }
    }
}
