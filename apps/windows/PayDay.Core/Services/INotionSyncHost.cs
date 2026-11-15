using System.Threading.Tasks;
using PayDay.Models;

namespace PayDay.Services;

/// <summary>Whether this device runs the Notion connector (<c>spec/sync-protocol.md</c> 12.5).</summary>
public enum NotionRole
{
    /// <summary>Sync is off: the connector works exactly as before sync existed.</summary>
    SyncOff,

    /// <summary>Sync is on and no device owns the connector yet: connecting Notion here makes this device the owner.</summary>
    NoOwner,

    /// <summary>This device owns the connector and has pushed since it became the owner: push and pull as usual.</summary>
    Owner,

    /// <summary>This device just became the owner: it must push every bill before its first pull, so its last-pushed values are known.</summary>
    OwnerMustPushFirst,

    /// <summary>Another device owns the connector: this one neither pushes nor pulls ("Notion: connected on &lt;device&gt;").</summary>
    OtherDevice,
}

/// <summary>The connector's role on this device, and the owner device when there is one.</summary>
public sealed record NotionConnectorState(NotionRole Role, string? OwnerDeviceId = null, string? OwnerDeviceName = null)
{
    public static readonly NotionConnectorState SyncOff = new(NotionRole.SyncOff);

    /// <summary>True when this device may call Notion.</summary>
    public bool MayRun => Role is NotionRole.SyncOff or NotionRole.Owner or NotionRole.OwnerMustPushFirst;
}

/// <summary>
/// What the Notion connector needs from the database when sync can be on. The routing write path
/// implements it; a plain database (or a test fake) does not, and the connector then behaves as it
/// always has.
/// </summary>
public interface INotionSyncHost
{
    /// <summary>The connector's role on this device right now.</summary>
    NotionConnectorState GetNotionState();

    /// <summary>
    /// Makes this device the connector's owner (sets the synced <c>NotionOwnerDeviceId</c> to this
    /// device and forgets its last-pushed values, so it pushes before it pulls). Nothing while sync is off.
    /// </summary>
    Task ClaimNotionAsync();

    /// <summary>
    /// Saves a bill pulled from Notion. With sync on only the fields that differ from the merged state
    /// are written, and Remaining goes through <c>setRemaining</c> with source <c>notion</c> and the
    /// last-pushed value (an echo of the connector's own push writes nothing). With sync off it is an
    /// ordinary upsert.
    /// </summary>
    Task SaveBillFromNotionAsync(Bill pulled);

    /// <summary>Records that the connector just wrote <paramref name="remaining"/> to the bill's Notion page.</summary>
    void RecordNotionPush(string billId, double remaining);

    /// <summary>The local bill ID a Notion page's "Bill ID" text stands for (a legacy ID maps to its synced ID).</summary>
    string MapNotionBillId(string notionBillId);
}
