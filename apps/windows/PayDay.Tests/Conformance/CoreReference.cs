using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using PayDay.Models;
using PayDay.Services;
using PayDay.ViewModels;

namespace PayDay.Tests.Conformance;

/// <summary>
/// Runs one conformance case through the C# reference implementation and returns the result in
/// the fixture's <c>expected</c> shape. The same code checks the fixtures and regenerates them, so
/// expected values always come from the C# rules. Shapes are documented in
/// <c>spec/conformance/core/README.md</c>.
/// </summary>
internal static class CoreReference
{
    private const string DateFormat = "yyyy-MM-dd";
    private const string DateTimeFormat = "yyyy-MM-dd'T'HH:mm:ss";

    public static Task<JsonNode> RunAsync(string fileName, JsonObject input) => fileName switch
    {
        "pay-periods.json" => Task.FromResult(PayPeriods(input)),
        "bill-assignment.json" => Task.FromResult(BillAssignment(input)),
        "totals.json" => TotalsAsync(input),
        "payoff.json" => PayoffAsync(input),
        "backup.json" => Task.FromResult(Backup(input)),
        _ => throw new InvalidDataException($"No conformance runner for {fileName}."),
    };

    // ------------------------------------------------------------------
    // pay-periods.json
    // ------------------------------------------------------------------

    private static JsonNode PayPeriods(JsonObject input)
    {
        RequireKeys(input, "anchor", "now");
        var periods = PayPeriodService.GetPayPeriods(Date(input, "anchor"), DateTimeValue(input, "now"));
        return new JsonObject { ["periods"] = new JsonArray(periods.Select(PeriodJson).ToArray<JsonNode?>()) };
    }

    private static JsonObject PeriodJson(PayPeriod p) => new()
    {
        ["label"] = p.Label,
        ["key"] = p.Key,
        ["start"] = FormatDate(p.Start),
        ["end"] = FormatDate(p.End),
        ["payday"] = FormatDate(p.Payday),
        ["isCurrent"] = p.IsCurrent,
    };

    // ------------------------------------------------------------------
    // bill-assignment.json
    // ------------------------------------------------------------------

    private static JsonNode BillAssignment(JsonObject input)
    {
        var kind = (string?)input["kind"];
        switch (kind)
        {
            case "periods":
            {
                RequireKeys(input, "kind", "anchor", "now", "bills");
                var periods = PayPeriodService.GetPayPeriods(Date(input, "anchor"), DateTimeValue(input, "now"));
                var assigned = PayPeriodService.AssignBillsToPeriods(Bills(input), periods);
                return new JsonObject
                {
                    ["periods"] = new JsonArray(assigned.Select(a => (JsonNode?)new JsonObject
                    {
                        ["label"] = a.Period.Label,
                        ["key"] = a.Period.Key,
                        ["start"] = FormatDate(a.Period.Start),
                        ["end"] = FormatDate(a.Period.End),
                        ["bills"] = new JsonArray(a.Bills.Select(b => (JsonNode?)new JsonObject
                        {
                            ["id"] = b.Bill.Id,
                            ["dueDate"] = b.DueDate is { } d ? FormatDate(d) : null,
                        }).ToArray()),
                    }).ToArray()),
                };
            }
            case "monthlyDueDate":
            {
                RequireKeys(input, "kind", "dueDay", "periodStart", "periodEnd");
                var due = PayPeriodService.GetBillDueDate(
                    (int)input["dueDay"]!, Date(input, "periodStart"), Date(input, "periodEnd"));
                return new JsonObject { ["dueDate"] = due is { } d ? FormatDate(d) : null };
            }
            default:
                throw new InvalidDataException($"bill-assignment: unknown kind \"{kind}\".");
        }
    }

    // ------------------------------------------------------------------
    // totals.json
    // ------------------------------------------------------------------

    private static async Task<JsonNode> TotalsAsync(JsonObject input)
    {
        var kind = (string?)input["kind"];
        switch (kind)
        {
            case "payDayPage":
            {
                RequireKeys(input, "kind", "anchor", "now", "bills", "payments");
                var db = DbWith(input);
                var paymentId = 1L;
                foreach (var p in Array(input, "payments").Select(n => n!.AsObject()))
                {
                    RequireKeys(p, "billId", "periodKey", "amountPaid");
                    db.Payments.Add(new Payment
                    {
                        Id = paymentId++,
                        BillId = (string)p["billId"]!,
                        PeriodKey = (string)p["periodKey"]!,
                        AmountPaid = (double)p["amountPaid"]!,
                    });
                }
                var vm = new PayDayPageViewModel(db);
                await vm.LoadAsync(DateTimeValue(input, "now")).ConfigureAwait(true);
                return new JsonObject
                {
                    ["periodKey"] = vm.CurrentPeriodKey,
                    ["totalDue"] = vm.TotalDue,
                    ["totalPaid"] = vm.TotalPaid,
                    ["remaining"] = vm.Remaining,
                    ["progressFraction"] = vm.ProgressFraction,
                    ["isAllPaid"] = vm.IsAllPaid,
                    ["autoPayTotal"] = vm.AutoPayTotal,
                    ["autoPayBills"] = Ids(vm.AutoPayBills.Select(r => r.Bill.Id)),
                    ["unpaidBills"] = Ids(vm.UnpaidBills.Select(r => r.Bill.Id)),
                    ["paidBills"] = new JsonArray(vm.PaidBills.Select(r => (JsonNode?)new JsonObject
                    {
                        ["id"] = r.Bill.Id,
                        ["amountPaid"] = r.AmountPaid,
                    }).ToArray()),
                };
            }
            case "dashboard":
            {
                RequireKeys(input, "kind", "anchor", "now", "bills");
                var vm = new DashboardPageViewModel(DbWith(input));
                await vm.LoadAsync(DateTimeValue(input, "now")).ConfigureAwait(true);
                return new JsonObject
                {
                    ["totalMonthlyObligations"] = vm.TotalMonthlyObligations,
                    ["totalRemaining"] = vm.TotalRemaining,
                    ["hasCreditCards"] = vm.HasCreditCards,
                    ["creditUtilizationPct"] = vm.CreditUtilizationPct,
                    ["billsDueThisPeriod"] = vm.BillsDueThisPeriod,
                    ["sections"] = new JsonArray(vm.Sections.Select(s => (JsonNode?)new JsonObject
                    {
                        ["label"] = s.Label,
                        ["key"] = s.Assigned.Period.Key,
                        ["manualTotal"] = s.ManualTotal,
                        ["autoPayTotal"] = s.AutoPayTotal,
                        ["grandTotal"] = s.GrandTotal,
                        ["billCount"] = s.TotalBillCount,
                        ["manualBills"] = Ids(s.ManualBills.Select(b => b.Bill.Id)),
                        ["autoPayBills"] = Ids(s.AutoPayBills.Select(b => b.Bill.Id)),
                    }).ToArray()),
                };
            }
            case "billGroups":
            {
                RequireKeys(input, "kind", "bills");
                var db = new FakeDatabaseService();
                db.Bills.AddRange(Bills(input));
                var vm = new AllBillsPageViewModel(db);
                await vm.LoadAsync().ConfigureAwait(true);
                return new JsonObject
                {
                    ["totalBills"] = vm.TotalBills,
                    ["groups"] = new JsonArray(vm.Groups.Select(g => (JsonNode?)new JsonObject
                    {
                        ["type"] = g.Key,
                        ["count"] = g.Count,
                        ["totalPayment"] = g.TotalPayment,
                        ["totalRemaining"] = g.TotalRemaining,
                        ["bills"] = Ids(g.Bills.Select(b => b.Id)),
                    }).ToArray()),
                };
            }
            default:
                throw new InvalidDataException($"totals: unknown kind \"{kind}\".");
        }
    }

    private static FakeDatabaseService DbWith(JsonObject input)
    {
        var db = new FakeDatabaseService();
        db.Settings[PayPeriodService.PayAnchorKey] = (string)input["anchor"]!;
        db.Bills.AddRange(Bills(input));
        return db;
    }

    // ------------------------------------------------------------------
    // payoff.json
    // ------------------------------------------------------------------

    private static async Task<JsonNode> PayoffAsync(JsonObject input)
    {
        var kind = (string?)input["kind"];
        switch (kind)
        {
            case "estimate":
            {
                RequireKeys(input, "kind", "remaining", "payment", "apr");
                var months = PayoffCalculator.EstimatePayoff(
                    (double)input["remaining"]!, (double)input["payment"]!, (double)input["apr"]!);
                return new JsonObject { ["months"] = Months(months) };
            }
            case "tracker":
            {
                RequireKeys(input, "kind", "bills");
                var db = new FakeDatabaseService();
                db.Bills.AddRange(Bills(input));
                var vm = new PayoffTrackerPageViewModel(db);
                await vm.LoadAsync().ConfigureAwait(true);

                var items = vm.Items.ToList();
                var byStrategy = new JsonObject();
                foreach (var (name, strategy) in new[]
                {
                    ("payoffTime", PayoffStrategy.PayoffTime),
                    ("snowball", PayoffStrategy.Snowball),
                    ("avalanche", PayoffStrategy.Avalanche),
                })
                {
                    // StrategyIndex ignores a set to the current value, so move away first.
                    vm.StrategyIndex = strategy == PayoffStrategy.Snowball ? (int)PayoffStrategy.Avalanche : (int)PayoffStrategy.Snowball;
                    vm.StrategyIndex = (int)strategy;
                    byStrategy[name] = Ids(vm.Items.Select(i => i.Bill.Id));
                }

                // "items" lists every included bill in input order (the order before sorting).
                var inputOrder = Bills(input).Select(b => b.Id).ToList();
                return new JsonObject
                {
                    ["itemCount"] = vm.ItemCount,
                    ["totalRemaining"] = vm.TotalRemaining,
                    ["items"] = new JsonArray(items
                        .OrderBy(i => inputOrder.IndexOf(i.Bill.Id))
                        .Select(i => (JsonNode?)new JsonObject
                        {
                            ["id"] = i.Bill.Id,
                            ["months"] = Months(i.EstimatedMonths),
                            ["hasLimit"] = i.HasProgress,
                            ["limitUsedFraction"] = i.ProgressFraction,
                        }).ToArray()),
                    ["order"] = byStrategy,
                };
            }
            default:
                throw new InvalidDataException($"payoff: unknown kind \"{kind}\".");
        }
    }

    private static JsonNode? Months(int? months) => months switch
    {
        null => null,
        int.MaxValue => JsonValue.Create("never"),
        int n => JsonValue.Create(n),
    };

    // ------------------------------------------------------------------
    // backup.json
    // ------------------------------------------------------------------

    private static JsonNode Backup(JsonObject input)
    {
        var kind = (string?)input["kind"];
        switch (kind)
        {
            case "serialize":
            {
                var json = SerializeDataset(input);
                return new JsonObject { ["document"] = JsonNode.Parse(json) };
            }
            case "roundTrip":
            {
                var json = SerializeDataset(input);
                return new JsonObject { ["backup"] = NormalizedBackup(BackupSerializer.FromJson(json)) };
            }
            case "parse":
            {
                string text;
                if (input.ContainsKey("text"))
                {
                    RequireKeys(input, "kind", "text");
                    text = (string)input["text"]!;
                }
                else
                {
                    RequireKeys(input, "kind", "document");
                    text = input["document"]!.ToJsonString();
                }
                BackupFile parsed;
                try
                {
                    parsed = BackupSerializer.FromJson(text);
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException)
                {
                    return new JsonObject { ["rejected"] = true };
                }
                return new JsonObject { ["backup"] = NormalizedBackup(parsed) };
            }
            default:
                throw new InvalidDataException($"backup: unknown kind \"{kind}\".");
        }
    }

    private static string SerializeDataset(JsonObject input)
    {
        RequireKeys(input, "kind", "exportedAtUtc", "bills", "payments", "snapshots", "settings");
        var exportedAt = DateTime.SpecifyKind(DateTimeValue(input, "exportedAtUtc"), DateTimeKind.Utc);
        var payments = Array(input, "payments").Select(n =>
        {
            var p = n!.AsObject();
            AllowKeys(p, "payment", "id", "billId", "periodKey", "amountPaid", "paidAt", "notionPageId");
            return new Payment
            {
                Id = (long?)p["id"] ?? 0,
                BillId = (string?)p["billId"] ?? string.Empty,
                PeriodKey = (string?)p["periodKey"] ?? string.Empty,
                AmountPaid = (double?)p["amountPaid"] ?? 0,
                PaidAt = (string?)p["paidAt"],
                NotionPageId = (string?)p["notionPageId"],
            };
        }).ToList();
        var snapshots = Array(input, "snapshots").Select(n =>
        {
            var s = n!.AsObject();
            AllowKeys(s, "snapshot", "id", "snapshotDate", "totalRemaining", "details", "notionPageId");
            return new Snapshot
            {
                Id = (long?)s["id"] ?? 0,
                SnapshotDate = (string?)s["snapshotDate"] ?? string.Empty,
                TotalRemaining = (double?)s["totalRemaining"] ?? 0,
                Details = (string?)s["details"],
                NotionPageId = (string?)s["notionPageId"],
            };
        }).ToList();
        var settings = input["settings"]!.AsObject()
            .ToDictionary(kv => kv.Key, kv => (string?)kv.Value);
        return BackupSerializer.ToJson(Bills(input), payments, snapshots, settings, exportedAt);
    }

    private static JsonObject NormalizedBackup(BackupFile b) => new()
    {
        ["formatVersion"] = b.FormatVersion,
        ["exportedAt"] = b.ExportedAt,
        ["bills"] = new JsonArray(b.Bills.Select(bill => (JsonNode?)new JsonObject
        {
            ["id"] = bill.Id,
            ["name"] = bill.Name,
            ["type"] = bill.Type,
            ["payment"] = bill.Payment,
            ["remaining"] = bill.Remaining,
            ["available"] = bill.Available,
            ["creditLimit"] = bill.CreditLimit,
            ["dueDay"] = bill.DueDay,
            ["rate"] = bill.Rate,
            ["apr"] = bill.APR,
            ["autoPay"] = bill.AutoPay,
            ["active"] = bill.Active,
            ["yearlyDate"] = bill.YearlyDate,
            ["notes"] = bill.Notes,
            ["createdAt"] = bill.CreatedAt,
            ["updatedAt"] = bill.UpdatedAt,
            ["notionPageId"] = bill.NotionPageId,
        }).ToArray()),
        ["payments"] = new JsonArray(b.Payments.Select(p => (JsonNode?)new JsonObject
        {
            ["id"] = p.Id,
            ["billId"] = p.BillId,
            ["periodKey"] = p.PeriodKey,
            ["amountPaid"] = p.AmountPaid,
            ["paidAt"] = p.PaidAt,
            ["notionPageId"] = p.NotionPageId,
        }).ToArray()),
        ["snapshots"] = new JsonArray(b.Snapshots.Select(s => (JsonNode?)new JsonObject
        {
            ["id"] = s.Id,
            ["snapshotDate"] = s.SnapshotDate,
            ["totalRemaining"] = s.TotalRemaining,
            ["details"] = s.Details,
            ["notionPageId"] = s.NotionPageId,
        }).ToArray()),
        ["settings"] = new JsonObject(b.Settings
            .Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, kv.Value is null ? null : JsonValue.Create(kv.Value)))),
    };

    // ------------------------------------------------------------------
    // Shared input readers
    // ------------------------------------------------------------------

    private static readonly string[] BillKeys =
    {
        "id", "name", "type", "payment", "remaining", "available", "creditLimit", "dueDay", "rate",
        "apr", "autoPay", "active", "yearlyDate", "notes", "createdAt", "updatedAt", "notionPageId",
    };

    /// <summary>Reads <c>input.bills</c>. Omitted fields take the same defaults as a new C# <see cref="Bill"/>.</summary>
    private static List<Bill> Bills(JsonObject input) => Array(input, "bills").Select(n =>
    {
        var o = n!.AsObject();
        AllowKeys(o, "bill", BillKeys);
        var bill = new Bill { Id = (string)o["id"]! };
        if (o.ContainsKey("name")) bill.Name = (string)o["name"]!;
        if (o.ContainsKey("type")) bill.Type = (string)o["type"]!;
        if (o.ContainsKey("payment")) bill.Payment = (double)o["payment"]!;
        if (o.ContainsKey("remaining")) bill.Remaining = (double)o["remaining"]!;
        if (o.ContainsKey("available")) bill.Available = (double)o["available"]!;
        if (o.ContainsKey("creditLimit")) bill.CreditLimit = (double)o["creditLimit"]!;
        if (o.ContainsKey("dueDay")) bill.DueDay = (int)o["dueDay"]!;
        if (o.ContainsKey("rate")) bill.Rate = (string)o["rate"]!;
        if (o.ContainsKey("apr")) bill.APR = (double)o["apr"]!;
        if (o.ContainsKey("autoPay")) bill.AutoPay = (bool)o["autoPay"]!;
        if (o.ContainsKey("active")) bill.Active = (bool)o["active"]!;
        bill.YearlyDate = (string?)o["yearlyDate"];
        bill.Notes = (string?)o["notes"];
        bill.CreatedAt = (string?)o["createdAt"];
        bill.UpdatedAt = (string?)o["updatedAt"];
        bill.NotionPageId = (string?)o["notionPageId"];
        return bill;
    }).ToList();

    private static JsonArray Ids(IEnumerable<string> ids)
        => new(ids.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());

    private static JsonArray Array(JsonObject input, string key)
        => input[key] as JsonArray ?? throw new InvalidDataException($"input.{key} must be an array.");

    private static DateTime Date(JsonObject input, string key)
        => DateTime.ParseExact((string)input[key]!, DateFormat, CultureInfo.InvariantCulture);

    private static DateTime DateTimeValue(JsonObject input, string key)
        => DateTime.ParseExact((string)input[key]!, DateTimeFormat, CultureInfo.InvariantCulture);

    private static string FormatDate(DateTime d) => d.ToString(DateFormat, CultureInfo.InvariantCulture);

    /// <summary>Fails on any key not listed, so a typo in a fixture can't silently fall back to a default.</summary>
    private static void RequireKeys(JsonObject o, params string[] allowed)
    {
        foreach (var key in allowed.Where(k => !o.ContainsKey(k)))
        {
            throw new InvalidDataException($"input is missing \"{key}\".");
        }
        AllowKeys(o, "input", allowed);
    }

    private static void AllowKeys(JsonObject o, string what, params string[] allowed)
    {
        foreach (var key in o.Select(p => p.Key).Where(k => !allowed.Contains(k)))
        {
            throw new InvalidDataException($"{what} has unknown key \"{key}\".");
        }
    }
}
