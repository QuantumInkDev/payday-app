using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PayDay.Models;

namespace PayDay.Services;

/// <summary>
/// Notion sync engine for PayDay (plan §5). Bills sync bidirectionally —
/// last-write-wins on <c>UpdatedAt</c> vs Notion's <c>last_edited_time</c>.
/// Payments and Snapshots push only.
///
/// <para>Wiring: the WinUI app constructs this with a real
/// <see cref="HttpClientHandler"/>; tests inject a recording handler.</para>
///
/// <para>Data-source IDs are read from the Settings table on each sync
/// (keys <c>NotionBillsDb</c>, <c>NotionPaymentsDb</c>, <c>NotionSnapshotsDb</c>).
/// Integration token is fetched from the credential store under key
/// <see cref="TokenKey"/>.</para>
/// </summary>
public sealed class NotionSyncService : IDisposable
{
    public const string TokenKey = "NotionToken";
    public const string LastSyncedSetting = "LastNotionSync";
    public const string BillsDataSourceSetting = "NotionBillsDb";
    public const string PaymentsDataSourceSetting = "NotionPaymentsDb";
    public const string SnapshotsDataSourceSetting = "NotionSnapshotsDb";

    private const string NotionVersion = "2025-09-03";
    private const string BaseUrl = "https://api.notion.com/";

    private readonly IDatabaseService _db;
    private readonly ICredentialStore _credentials;
    private readonly HttpClient _http;
    private readonly bool _ownsHandler;

    public NotionSyncService(IDatabaseService db, ICredentialStore credentials, HttpMessageHandler? handler = null)
    {
        _db = db;
        _credentials = credentials;
        _ownsHandler = handler is null;
        _http = new HttpClient(handler ?? new HttpClientHandler(), disposeHandler: _ownsHandler)
        {
            BaseAddress = new Uri(BaseUrl),
        };
        _http.DefaultRequestHeaders.Add("Notion-Version", NotionVersion);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public void Dispose() => _http.Dispose();

    // ------------------------------------------------------------------
    // Token + last-synced helpers
    // ------------------------------------------------------------------

    public bool HasToken() => _credentials.Exists(TokenKey);
    public void SaveToken(string token) => _credentials.Set(TokenKey, token);
    public void DeleteToken() => _credentials.Delete(TokenKey);

    public async Task<DateTimeOffset?> GetLastSyncedAsync()
    {
        var raw = await _db.GetSettingAsync(LastSyncedSetting).ConfigureAwait(false);
        if (string.IsNullOrEmpty(raw)) return null;
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var v)
            ? v
            : (DateTimeOffset?)null;
    }

    public Task SetLastSyncedAsync(DateTimeOffset value)
        => _db.SetSettingAsync(LastSyncedSetting, value.ToString("O", CultureInfo.InvariantCulture));

    // ------------------------------------------------------------------
    // Connection check
    // ------------------------------------------------------------------

    public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        var token = _credentials.Get(TokenKey);
        if (string.IsNullOrWhiteSpace(token)) return false;
        using var req = new HttpRequestMessage(HttpMethod.Get, "v1/users/me");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------
    // Bills — bidirectional sync
    // ------------------------------------------------------------------

    public async Task<NotionSyncResult> SyncBillsAsync(CancellationToken ct = default)
    {
        var token = _credentials.Get(TokenKey);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("No Notion token configured. Save one in Settings first.");

        var dataSourceId = await _db.GetSettingAsync(BillsDataSourceSetting).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(dataSourceId))
            throw new InvalidOperationException($"Setting '{BillsDataSourceSetting}' is empty.");

        var errors = new List<string>();
        int created = 0, updated = 0, pulled = 0, archived = 0;

        var remotePages = await QueryAllPagesAsync(dataSourceId!, token, ct).ConfigureAwait(false);
        var remoteByBillId = new Dictionary<string, NotionPage>(StringComparer.Ordinal);
        foreach (var page in remotePages)
        {
            var billId = page.BillId;
            if (!string.IsNullOrWhiteSpace(billId) && !remoteByBillId.ContainsKey(billId!))
                remoteByBillId[billId!] = page;
        }

        var localBills = await _db.GetAllBillsAsync().ConfigureAwait(false);
        var localByBillId = new Dictionary<string, Bill>(StringComparer.Ordinal);
        foreach (var b in localBills)
            localByBillId[b.Id] = b;

        foreach (var local in localBills)
        {
            try
            {
                if (remoteByBillId.TryGetValue(local.Id, out var remote))
                {
                    // Cache the page id locally if we hadn't yet.
                    if (string.IsNullOrEmpty(local.NotionPageId))
                        local.NotionPageId = remote.Id;

                    var localUpdated = ParseSqliteUtc(local.UpdatedAt);
                    if (localUpdated > remote.LastEditedTime)
                    {
                        await UpdatePageAsync(remote.Id, BuildBillProperties(local), token, ct).ConfigureAwait(false);
                        await _db.UpsertBillAsync(local).ConfigureAwait(false);
                        updated++;
                    }
                    else if (remote.LastEditedTime > localUpdated)
                    {
                        var merged = remote.ToBill(local.Id);
                        merged.NotionPageId = remote.Id;
                        await _db.UpsertBillAsync(merged).ConfigureAwait(false);
                        pulled++;
                    }
                    else
                    {
                        // Equal timestamps — still persist NotionPageId if we just learned it.
                        if (!string.Equals(local.NotionPageId, remote.Id, StringComparison.Ordinal))
                        {
                            local.NotionPageId = remote.Id;
                            await _db.UpsertBillAsync(local).ConfigureAwait(false);
                        }
                    }
                }
                else
                {
                    var newPageId = await CreatePageAsync(dataSourceId!, BuildBillProperties(local), token, ct).ConfigureAwait(false);
                    local.NotionPageId = newPageId;
                    await _db.UpsertBillAsync(local).ConfigureAwait(false);
                    created++;
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Bill '{local.Name}' ({local.Id}): {ex.Message}");
            }
        }

        foreach (var (billId, remote) in remoteByBillId)
        {
            if (localByBillId.ContainsKey(billId)) continue;
            try
            {
                var pulledBill = remote.ToBill(billId);
                pulledBill.NotionPageId = remote.Id;
                await _db.UpsertBillAsync(pulledBill).ConfigureAwait(false);
                pulled++;
            }
            catch (Exception ex)
            {
                errors.Add($"Pull Notion page {remote.Id}: {ex.Message}");
            }
        }

        // TODO(phase 5+): archive Notion pages whose Bill IDs are no longer local.
        // Needs a tombstone table — without it we can't distinguish "never existed locally"
        // from "deleted locally last session". Tracking this for a future polish chunk.
        _ = archived;

        await SetLastSyncedAsync(DateTimeOffset.UtcNow).ConfigureAwait(false);
        return new NotionSyncResult(created, updated, pulled, archived, errors);
    }

    // ------------------------------------------------------------------
    // Payments + Snapshots (push only) — chunk 5c
    // ------------------------------------------------------------------

    public async Task<string> PushPaymentAsync(Payment payment, string billName, CancellationToken ct = default)
    {
        var token = _credentials.Get(TokenKey);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("No Notion token configured.");
        var dataSourceId = await _db.GetSettingAsync(PaymentsDataSourceSetting).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(dataSourceId))
            throw new InvalidOperationException($"Setting '{PaymentsDataSourceSetting}' is empty.");

        var properties = new Dictionary<string, object?>
        {
            ["Name"] = Title($"{billName} — {payment.PeriodKey}"),
            ["Bill ID"] = RichText(payment.BillId),
            ["Period"] = RichText(payment.PeriodKey),
            ["Amount Paid"] = Number(payment.AmountPaid),
            ["Paid At"] = RichText(payment.PaidAt ?? string.Empty),
        };
        return await CreatePageAsync(dataSourceId!, properties, token!, ct).ConfigureAwait(false);
    }

    public async Task<string> PushSnapshotAsync(Snapshot snapshot, CancellationToken ct = default)
    {
        var token = _credentials.Get(TokenKey);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("No Notion token configured.");
        var dataSourceId = await _db.GetSettingAsync(SnapshotsDataSourceSetting).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(dataSourceId))
            throw new InvalidOperationException($"Setting '{SnapshotsDataSourceSetting}' is empty.");

        var properties = new Dictionary<string, object?>
        {
            ["Name"] = Title($"Snapshot {snapshot.SnapshotDate}"),
            ["Date"] = Date(snapshot.SnapshotDate),
            ["Total Owed"] = Number(snapshot.TotalOwed),
            ["Details"] = RichText(snapshot.Details ?? string.Empty),
        };
        return await CreatePageAsync(dataSourceId!, properties, token!, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    // Notion HTTP wrappers
    // ------------------------------------------------------------------

    private async Task<List<NotionPage>> QueryAllPagesAsync(string dataSourceId, string token, CancellationToken ct)
    {
        var pages = new List<NotionPage>();
        string? cursor = null;
        do
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"v1/data_sources/{dataSourceId}/query");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var body = new Dictionary<string, object?> { ["page_size"] = 100 };
            if (cursor is not null) body["start_cursor"] = cursor;
            req.Content = JsonContent.Create(body);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            await EnsureSuccessAsync(resp, "Notion query").ConfigureAwait(false);

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var root = doc.RootElement;
            if (root.TryGetProperty("results", out var results))
            {
                foreach (var r in results.EnumerateArray())
                    pages.Add(NotionPage.FromElement(r));
            }
            cursor = root.TryGetProperty("has_more", out var hasMore) && hasMore.GetBoolean()
                ? root.GetProperty("next_cursor").GetString()
                : null;
        } while (!string.IsNullOrEmpty(cursor));
        return pages;
    }

    private async Task<string> CreatePageAsync(
        string dataSourceId,
        IReadOnlyDictionary<string, object?> properties,
        string token,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/pages");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var body = new Dictionary<string, object?>
        {
            ["parent"] = new Dictionary<string, object?>
            {
                ["type"] = "data_source_id",
                ["data_source_id"] = dataSourceId,
            },
            ["properties"] = properties,
        };
        req.Content = JsonContent.Create(body);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(resp, "Notion create page").ConfigureAwait(false);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return doc.RootElement.GetProperty("id").GetString() ?? throw new InvalidOperationException("Notion returned no page id.");
    }

    private async Task UpdatePageAsync(
        string pageId,
        IReadOnlyDictionary<string, object?> properties,
        string token,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Patch, $"v1/pages/{pageId}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content = JsonContent.Create(new Dictionary<string, object?> { ["properties"] = properties });
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(resp, "Notion update page").ConfigureAwait(false);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage resp, string opLabel)
    {
        if (resp.IsSuccessStatusCode) return;
        var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        throw new HttpRequestException($"{opLabel} failed: {(int)resp.StatusCode} {resp.ReasonPhrase}. {Truncate(body, 240)}");
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s.Substring(0, max) + "...";

    // ------------------------------------------------------------------
    // Bill ↔ Notion property mapping
    // ------------------------------------------------------------------

    /// <summary>Builds the Notion property payload for a local <see cref="Bill"/>.</summary>
    internal static IReadOnlyDictionary<string, object?> BuildBillProperties(Bill bill) => new Dictionary<string, object?>
    {
        ["Name"] = Title(bill.Name),
        ["Type"] = RichText(bill.Type),
        ["Payment"] = Number(bill.Cost),
        ["Owed"] = Number(bill.Owed),
        ["Available"] = Number(bill.Available),
        ["Credit Limit"] = Number(bill.CreditLimit),
        ["Due Day"] = Number(bill.DueDay),
        ["Frequency"] = RichText(bill.Rate),
        ["APR"] = Number(bill.APR),
        ["Auto-Pay"] = Checkbox(bill.AutoPay),
        ["Active"] = Checkbox(bill.Active),
        ["Bill ID"] = RichText(bill.Id),
        ["Yearly Date"] = RichText(bill.YearlyDate ?? string.Empty),
        ["Notes"] = RichText(bill.Notes ?? string.Empty),
    };

    private static object Title(string text) => new Dictionary<string, object?>
    {
        ["title"] = new[] { new Dictionary<string, object?> { ["type"] = "text", ["text"] = new Dictionary<string, object?> { ["content"] = text ?? string.Empty } } },
    };

    private static object RichText(string text) => new Dictionary<string, object?>
    {
        ["rich_text"] = new[] { new Dictionary<string, object?> { ["type"] = "text", ["text"] = new Dictionary<string, object?> { ["content"] = text ?? string.Empty } } },
    };

    private static object Number(double n) => new Dictionary<string, object?> { ["number"] = n };

    private static object Checkbox(bool b) => new Dictionary<string, object?> { ["checkbox"] = b };

    private static object Date(string isoDate) => new Dictionary<string, object?>
    {
        ["date"] = new Dictionary<string, object?> { ["start"] = isoDate },
    };

    /// <summary>Parses a SQLite <c>datetime('now')</c> string (UTC, no zone suffix).</summary>
    internal static DateTimeOffset ParseSqliteUtc(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return DateTimeOffset.MinValue;
        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
            return dto;
        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt))
            return new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc), TimeSpan.Zero);
        return DateTimeOffset.MinValue;
    }

    // ------------------------------------------------------------------
    // Notion page projection (read side)
    // ------------------------------------------------------------------

    internal sealed class NotionPage
    {
        public string Id { get; init; } = string.Empty;
        public DateTimeOffset LastEditedTime { get; init; }
        public bool Archived { get; init; }
        public string? Name { get; init; }
        public string? Type { get; init; }
        public double Payment { get; init; }
        public double Owed { get; init; }
        public double Available { get; init; }
        public double CreditLimit { get; init; }
        public int DueDay { get; init; }
        public string? Frequency { get; init; }
        public double APR { get; init; }
        public bool AutoPay { get; init; }
        public bool Active { get; init; }
        public string? BillId { get; init; }
        public string? YearlyDate { get; init; }
        public string? Notes { get; init; }

        public Bill ToBill(string billId) => new()
        {
            Id = billId,
            Name = Name ?? string.Empty,
            Type = Type ?? "Other",
            Cost = Payment,
            Owed = Owed,
            Available = Available,
            CreditLimit = CreditLimit,
            DueDay = DueDay <= 0 ? 1 : DueDay,
            Rate = string.IsNullOrWhiteSpace(Frequency) ? "Monthly" : Frequency!,
            APR = APR,
            AutoPay = AutoPay,
            Active = Active,
            YearlyDate = string.IsNullOrWhiteSpace(YearlyDate) ? null : YearlyDate,
            Notes = string.IsNullOrWhiteSpace(Notes) ? null : Notes,
        };

        public static NotionPage FromElement(JsonElement page)
        {
            var props = page.GetProperty("properties");
            return new NotionPage
            {
                Id = page.GetProperty("id").GetString() ?? string.Empty,
                LastEditedTime = page.TryGetProperty("last_edited_time", out var lt) && lt.ValueKind == JsonValueKind.String
                    ? DateTimeOffset.Parse(lt.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)
                    : DateTimeOffset.MinValue,
                Archived = page.TryGetProperty("archived", out var a) && a.ValueKind == JsonValueKind.True,
                Name = ReadTitle(props, "Name"),
                Type = ReadRichText(props, "Type"),
                Payment = ReadNumber(props, "Payment"),
                Owed = ReadNumber(props, "Owed"),
                Available = ReadNumber(props, "Available"),
                CreditLimit = ReadNumber(props, "Credit Limit"),
                DueDay = (int)Math.Round(ReadNumber(props, "Due Day")),
                Frequency = ReadRichText(props, "Frequency"),
                APR = ReadNumber(props, "APR"),
                AutoPay = ReadCheckbox(props, "Auto-Pay"),
                Active = ReadCheckbox(props, "Active"),
                BillId = ReadRichText(props, "Bill ID"),
                YearlyDate = ReadRichText(props, "Yearly Date"),
                Notes = ReadRichText(props, "Notes"),
            };
        }

        private static string? ReadTitle(JsonElement props, string name)
        {
            if (!props.TryGetProperty(name, out var p) || !p.TryGetProperty("title", out var arr)) return null;
            return ExtractPlainText(arr);
        }

        private static string? ReadRichText(JsonElement props, string name)
        {
            if (!props.TryGetProperty(name, out var p) || !p.TryGetProperty("rich_text", out var arr)) return null;
            return ExtractPlainText(arr);
        }

        private static double ReadNumber(JsonElement props, string name)
        {
            if (!props.TryGetProperty(name, out var p) || !p.TryGetProperty("number", out var n)) return 0;
            return n.ValueKind == JsonValueKind.Number ? n.GetDouble() : 0;
        }

        private static bool ReadCheckbox(JsonElement props, string name)
        {
            if (!props.TryGetProperty(name, out var p) || !p.TryGetProperty("checkbox", out var c)) return false;
            return c.ValueKind == JsonValueKind.True;
        }

        private static string? ExtractPlainText(JsonElement arr)
        {
            if (arr.ValueKind != JsonValueKind.Array) return null;
            var sb = new StringBuilder();
            foreach (var item in arr.EnumerateArray())
            {
                if (item.TryGetProperty("plain_text", out var pt) && pt.ValueKind == JsonValueKind.String)
                    sb.Append(pt.GetString());
                else if (item.TryGetProperty("text", out var t) && t.TryGetProperty("content", out var c))
                    sb.Append(c.GetString());
            }
            return sb.Length == 0 ? null : sb.ToString();
        }
    }
}
