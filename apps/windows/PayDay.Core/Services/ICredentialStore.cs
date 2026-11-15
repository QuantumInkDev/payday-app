namespace PayDay.Services;

/// <summary>
/// Abstraction over a per-user secret store. The Windows-backed implementation
/// (<c>WindowsCredentialStore</c>) lives in the app project because it
/// P/Invokes <c>Advapi32.dll</c>. Tests use an in-memory fake.
/// </summary>
public interface ICredentialStore
{
    /// <summary>Returns the stored secret for <paramref name="key"/>, or <c>null</c> if none.</summary>
    string? Get(string key);

    /// <summary>Stores <paramref name="value"/> under <paramref name="key"/>. Overwrites if it already exists.</summary>
    void Set(string key, string value);

    /// <summary>Removes the entry. No-ops if the key is absent.</summary>
    void Delete(string key);

    /// <summary>Returns true if a secret exists at <paramref name="key"/>.</summary>
    bool Exists(string key);
}
