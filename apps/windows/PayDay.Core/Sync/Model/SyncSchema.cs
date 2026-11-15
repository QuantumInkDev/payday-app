namespace PayDay.Sync.Model;

/// <summary>Entity names (<c>spec/data-model.md</c> section 4).</summary>
public static class SyncEntities
{
    public const string Bill = "bill";
    public const string Payment = "payment";
    public const string BalanceSnapshot = "balanceSnapshot";
    public const string IncomeSource = "incomeSource";
    public const string Paycheck = "paycheck";
    public const string Device = "device";
    public const string Setting = "setting";

    /// <summary>The field every entity except <c>setting</c> uses as its tombstone.</summary>
    public const string DeletedField = "deleted";

    /// <summary>The only field of a <c>setting</c> row.</summary>
    public const string ValueField = "value";

    /// <summary>The plan entities a backup restore writes and tombstones (<c>sync-protocol.md</c> 12.3), in restore order.</summary>
    public static readonly IReadOnlyList<string> PlanEntities = new[] { Bill, Payment, BalanceSnapshot, IncomeSource, Paycheck };
}

/// <summary>Values of <c>payment.kind</c>.</summary>
public static class PaymentKinds
{
    public const string Regular = "Regular";
    public const string Extra = "Extra";
    public const string SetAside = "SetAside";

    /// <summary>Only these kinds lower Remaining.</summary>
    public static bool CountsAgainstRemaining(string kind) => kind is Regular or Extra;
}

/// <summary>Synced setting keys (<c>spec/data-model.md</c> section 6.1).</summary>
public static class SyncSettingKeys
{
    public const string PayAnchor = "PayAnchor";
    public const string EarlyStart = "EarlyStart";
    public const string TypeColorPrefix = "TypeColor/";
    public const string SafeToSpendBuffer = "SafeToSpendBuffer";
    public const string ReminderDaysBefore = "ReminderDaysBefore";
    public const string NotionOwnerDeviceId = "NotionOwnerDeviceId";
    public const string NotionBillsDb = "NotionBillsDb";
    public const string NotionPaymentsDb = "NotionPaymentsDb";
    public const string NotionSnapshotsDb = "NotionSnapshotsDb";

    /// <summary>Connector state that a restore never writes or clears.</summary>
    public static readonly IReadOnlyList<string> NotionKeys = new[] { NotionOwnerDeviceId, NotionBillsDb, NotionPaymentsDb, NotionSnapshotsDb };

    /// <summary>Plan settings with a fixed key; <c>TypeColor/&lt;type&gt;</c> keys are plan settings too.</summary>
    public static readonly IReadOnlyList<string> FixedPlanKeys = new[] { PayAnchor, EarlyStart, SafeToSpendBuffer, ReminderDaysBefore };

    public static string TypeColor(string type) => TypeColorPrefix + type;

    /// <summary>A synced plan setting that a restore writes or resets: the fixed plan keys and every <c>TypeColor/</c> key.</summary>
    public static bool IsPlanSetting(string key)
        => FixedPlanKeys.Contains(key, StringComparer.Ordinal) || key.StartsWith(TypeColorPrefix, StringComparison.Ordinal);
}

/// <summary>Field types of <c>spec/data-model.md</c> section 1.1.</summary>
public enum SyncFieldType
{
    String,
    NullableString,
    Bool,
    Int,
    NullableInt,
    Number,
    Money,
    Date,
    NullableDate,
    NullableTimestamp,
    Id,
    Enum,
}

/// <summary>One field of a known entity, with its default (section 1.2: a missing or wrong-type value reads as the default).</summary>
public sealed record SyncFieldSpec(string Name, SyncFieldType Type, SyncValue Default)
{
    /// <summary>True when <paramref name="value"/> has the right JSON type for this field.</summary>
    public bool Accepts(SyncValue value)
        => Type switch
        {
            SyncFieldType.String or SyncFieldType.Id or SyncFieldType.Enum or SyncFieldType.Date => value.IsString,
            SyncFieldType.NullableString or SyncFieldType.NullableDate or SyncFieldType.NullableTimestamp => value.IsString || value.IsNull,
            SyncFieldType.Bool => value.IsBool,
            SyncFieldType.Int => IsInteger(value),
            SyncFieldType.NullableInt => value.IsNull || IsInteger(value),
            SyncFieldType.Number or SyncFieldType.Money => value.IsNumber,
            _ => false,
        };

    /// <summary>Two values are the same edit: money compares in cents (section 4.4), everything else exactly.</summary>
    public bool SameValue(SyncValue a, SyncValue b)
    {
        if (Type == SyncFieldType.Money && a.IsNumber && b.IsNumber
            && SyncMoney.TryCents(a.NumberValue, out var ca) && SyncMoney.TryCents(b.NumberValue, out var cb))
        {
            return ca == cb;
        }
        return a == b;
    }

    private static bool IsInteger(SyncValue v)
        => v.IsNumber && Math.Floor(v.NumberValue) == v.NumberValue && Math.Abs(v.NumberValue) <= Json.StrictJson.MaxSafeInteger;
}

/// <summary>
/// The known entities and their fields, defaults and types (<c>spec/data-model.md</c> section 4).
/// Records for anything not listed here are still merged, stored and forwarded (section 4.5).
/// </summary>
public static class SyncSchema
{
    private static readonly SyncValue Empty = SyncValue.String(string.Empty);
    private static readonly SyncValue Zero = SyncValue.Number(0);

    private static readonly Dictionary<string, Dictionary<string, SyncFieldSpec>> Entities = new(StringComparer.Ordinal)
    {
        [SyncEntities.Bill] = Build(
            F("name", SyncFieldType.String, Empty),
            F("type", SyncFieldType.String, Empty),
            F("payment", SyncFieldType.Money, Zero),
            F("remainingCheckpoint", SyncFieldType.Money, Zero),
            F("available", SyncFieldType.Money, Zero),
            F("creditLimit", SyncFieldType.Money, Zero),
            F("dueDay", SyncFieldType.Int, SyncValue.Number(1)),
            F("rate", SyncFieldType.Enum, SyncValue.String("Monthly")),
            F("apr", SyncFieldType.Number, Zero),
            F("autoPay", SyncFieldType.Bool, SyncValue.False),
            F("active", SyncFieldType.Bool, SyncValue.True),
            F("yearlyDate", SyncFieldType.NullableString, SyncValue.Null),
            F("notes", SyncFieldType.NullableString, SyncValue.Null),
            F("targetPayoffDate", SyncFieldType.NullableDate, SyncValue.Null),
            F("notionPageId", SyncFieldType.NullableString, SyncValue.Null),
            F("createdAt", SyncFieldType.NullableTimestamp, SyncValue.Null),
            Deleted()),
        [SyncEntities.Payment] = Build(
            F("billId", SyncFieldType.Id, Empty),
            F("kind", SyncFieldType.Enum, SyncValue.String(PaymentKinds.Regular)),
            F("periodKey", SyncFieldType.Date, Empty),
            F("amountPaid", SyncFieldType.Money, Zero),
            F("paidAt", SyncFieldType.NullableTimestamp, SyncValue.Null),
            F("confirmationNumber", SyncFieldType.NullableString, SyncValue.Null),
            F("note", SyncFieldType.NullableString, SyncValue.Null),
            F("notionPageId", SyncFieldType.NullableString, SyncValue.Null),
            Deleted()),
        [SyncEntities.BalanceSnapshot] = Build(
            F("snapshotDate", SyncFieldType.Date, Empty),
            F("totalRemaining", SyncFieldType.Money, Zero),
            F("details", SyncFieldType.NullableString, SyncValue.Null),
            F("notionPageId", SyncFieldType.NullableString, SyncValue.Null),
            Deleted()),
        [SyncEntities.IncomeSource] = Build(
            F("name", SyncFieldType.String, Empty),
            F("cadence", SyncFieldType.Enum, SyncValue.String("Irregular")),
            F("expectedAmount", SyncFieldType.Money, Zero),
            F("anchorDate", SyncFieldType.NullableDate, SyncValue.Null),
            F("payDay1", SyncFieldType.NullableInt, SyncValue.Null),
            F("payDay2", SyncFieldType.NullableInt, SyncValue.Null),
            F("active", SyncFieldType.Bool, SyncValue.True),
            F("note", SyncFieldType.NullableString, SyncValue.Null),
            Deleted()),
        [SyncEntities.Paycheck] = Build(
            F("incomeSourceId", SyncFieldType.Id, Empty),
            F("payDate", SyncFieldType.Date, Empty),
            F("amount", SyncFieldType.Money, Zero),
            F("note", SyncFieldType.NullableString, SyncValue.Null),
            Deleted()),
        [SyncEntities.Device] = Build(
            F("name", SyncFieldType.String, Empty),
            F("platform", SyncFieldType.Enum, Empty),
            F("appVersion", SyncFieldType.NullableString, SyncValue.Null),
            F("createdAt", SyncFieldType.NullableTimestamp, SyncValue.Null),
            Deleted()),
    };

    public static bool IsKnownEntity(string entity) => entity == SyncEntities.Setting || Entities.ContainsKey(entity);

    /// <summary>The known fields of an entity (empty for <c>setting</c> and unknown entities).</summary>
    public static IReadOnlyCollection<SyncFieldSpec> FieldsOf(string entity)
        => Entities.TryGetValue(entity, out var fields) ? fields.Values : Array.Empty<SyncFieldSpec>();

    public static bool TryGetField(string entity, string field, out SyncFieldSpec spec)
    {
        if (Entities.TryGetValue(entity, out var fields) && fields.TryGetValue(field, out var found))
        {
            spec = found;
            return true;
        }
        spec = null!;
        return false;
    }

    private static SyncFieldSpec F(string name, SyncFieldType type, SyncValue def) => new(name, type, def);

    private static SyncFieldSpec Deleted() => new(SyncEntities.DeletedField, SyncFieldType.Bool, SyncValue.False);

    private static Dictionary<string, SyncFieldSpec> Build(params SyncFieldSpec[] fields)
        => fields.ToDictionary(f => f.Name, StringComparer.Ordinal);
}
