using System.Threading.Tasks;

namespace PayDay.Pages;

/// <summary>
/// A page that reloads its data after the sync loop merged changes from another device. The main
/// window calls it on the UI thread, coalesced, after <c>LocalSync.DataChanged</c>.
/// </summary>
public interface ISyncReloadable
{
    /// <param name="settingsChanged">True when synced settings (pay anchor, type colours, …) changed.</param>
    Task ReloadAfterSyncAsync(bool settingsChanged);
}
