using PayDay.Sync.Crypto;
using PayDay.Sync.Ids;
using PayDay.Sync.Json;

namespace PayDay.Sync.Files;

/// <summary>One key generation listed in vault.json.</summary>
public sealed record VaultGeneration(long Id, string KeyCheck, string? CreatedAt);

/// <summary>A vault.json that passed <see cref="VaultFile.Check"/>.</summary>
public sealed record VaultInfo(
    string VaultId,
    string? CreatedAt,
    string? CreatedBy,
    long CurrentGeneration,
    IReadOnlyList<VaultGeneration> Generations)
{
    /// <summary>The generation IDs, ascending.</summary>
    public IReadOnlyList<long> GenerationIds => Generations.Select(g => g.Id).Order().ToArray();
}

/// <summary>vault.json (<c>spec/sync-protocol.md</c> section 7): reading, validating, key check matching and writing.</summary>
public static class VaultFile
{
    public const string Type = "futurecheck-vault";

    /// <summary>Strict-parses vault.json bytes (section 6.3), then runs <see cref="Check"/>.</summary>
    public static VaultInfo CheckBytes(ReadOnlySpan<byte> utf8) => Check(StrictJson.Parse(utf8));

    /// <summary>
    /// Validates vault.json, failing with <c>malformed</c> or <c>unsupportedFormat</c>: the right
    /// <c>type</c>, <c>formatVersion</c> 1, a non-empty <c>generations</c> list of unique integer
    /// IDs ≥ 1 with 32-hex-digit key checks, and a <c>currentGeneration</c> that is listed. Unknown
    /// fields are ignored.
    /// </summary>
    public static VaultInfo Check(JsonData vault)
    {
        ArgumentNullException.ThrowIfNull(vault);
        if (vault.Kind != JsonDataKind.Object || vault["type"]?.AsString() != Type)
        {
            throw new SyncException(SyncErrorCodes.Malformed, "not a vault.json");
        }
        Envelope.CheckFormatVersionOf(vault);
        var vaultId = vault["vaultId"]?.AsString();
        if (!Uuid.IsCanonical(vaultId))
        {
            throw new SyncException(SyncErrorCodes.Malformed, "vaultId must be a canonical lowercase UUID");
        }
        var gens = vault["generations"];
        if (gens is null || gens.Kind != JsonDataKind.Array || gens.Items.Count == 0)
        {
            throw new SyncException(SyncErrorCodes.Malformed, "generations must be a non-empty array");
        }
        var list = new List<VaultGeneration>();
        foreach (var g in gens.Items)
        {
            if (g.Kind != JsonDataKind.Object || !StrictJson.TryGetInteger(g["id"], out var id) || id < 1)
            {
                throw new SyncException(SyncErrorCodes.Malformed, "generation id must be an integer >= 1");
            }
            var keyCheck = g["keyCheck"]?.AsString();
            if (!DeviceIds.IsLowerHex(keyCheck, 32))
            {
                throw new SyncException(SyncErrorCodes.Malformed, "keyCheck must be 32 lowercase hex digits");
            }
            list.Add(new VaultGeneration(id, keyCheck!, g["createdAt"]?.AsString()));
        }
        var hasCurrent = StrictJson.TryGetInteger(vault["currentGeneration"], out var current);
        if (list.Select(g => g.Id).Distinct().Count() != list.Count || !hasCurrent || list.All(g => g.Id != current))
        {
            throw new SyncException(SyncErrorCodes.Malformed, "generation IDs must be unique and include currentGeneration");
        }
        return new VaultInfo(vaultId!, vault["createdAt"]?.AsString(), vault["createdBy"]?.AsString(), current, list);
    }

    /// <summary>The generation whose key check matches <paramref name="vaultKey"/>, or null (section 7.2).</summary>
    public static long? MatchGeneration(VaultInfo vault, ReadOnlySpan<byte> vaultKey)
    {
        ArgumentNullException.ThrowIfNull(vault);
        var kc = SyncKeys.KeyCheckHex(vaultKey);
        foreach (var g in vault.Generations)
        {
            if (g.KeyCheck == kc) return g.Id;
        }
        return null;
    }

    /// <summary>The vault.json of a new vault: one generation (format 1 creates exactly one).</summary>
    public static JsonData Create(string vaultId, string createdAt, string createdBy, ReadOnlySpan<byte> vaultKey)
    {
        if (!Uuid.IsCanonical(vaultId)) throw new ArgumentException("Vault ID must be a canonical UUID.", nameof(vaultId));
        if (!DeviceIds.IsValid(createdBy)) throw new SyncException(SyncErrorCodes.BadDeviceId);
        var generation = JsonData.Object(
            ("id", JsonData.Number(1)),
            ("createdAt", JsonData.String(createdAt)),
            ("keyCheck", JsonData.String(SyncKeys.KeyCheckHex(vaultKey))));
        return JsonData.Object(
            ("type", JsonData.String(Type)),
            ("formatVersion", JsonData.Number(Envelope.SupportedFormatVersion)),
            ("vaultId", JsonData.String(vaultId)),
            ("createdAt", JsonData.String(createdAt)),
            ("createdBy", JsonData.String(createdBy)),
            ("currentGeneration", JsonData.Number(1)),
            ("generations", JsonData.Array(new[] { generation })));
    }

    /// <summary>
    /// Picks the vault every device uses when two vault.json files exist (section 12.1): the
    /// earliest <c>createdAt</c>, ties broken by the smaller <c>vaultId</c>. A <c>createdAt</c> that is
    /// not a valid timestamp sorts after every valid one.
    /// </summary>
    public static VaultInfo ChooseAmongDuplicates(IEnumerable<VaultInfo> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return candidates
            .OrderBy(v => Model.SyncDates.IsTimestamp(v.CreatedAt) ? 0 : 1)
            .ThenBy(v => Model.SyncDates.IsTimestamp(v.CreatedAt) ? v.CreatedAt : string.Empty, StringComparer.Ordinal)
            .ThenBy(v => v.VaultId, StringComparer.Ordinal)
            .First();
    }
}
