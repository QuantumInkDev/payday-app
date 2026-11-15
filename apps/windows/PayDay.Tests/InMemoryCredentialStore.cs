using System.Collections.Generic;
using PayDay.Services;

namespace PayDay.Tests;

internal sealed class InMemoryCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, string> _store = new();
    public IReadOnlyDictionary<string, string> Items => _store;

    public string? Get(string key) => _store.TryGetValue(key, out var v) ? v : null;
    public void Set(string key, string value) => _store[key] = value;
    public void Delete(string key) => _store.Remove(key);
    public bool Exists(string key) => _store.ContainsKey(key);
}
