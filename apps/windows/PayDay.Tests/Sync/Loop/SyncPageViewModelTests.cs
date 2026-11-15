using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PayDay.Services;
using PayDay.Sync.Coordination;
using PayDay.Sync.Crypto;
using PayDay.Sync.Drive;
using PayDay.Sync.Files;
using PayDay.Sync.Local;
using PayDay.Tests.Sync.Local;
using PayDay.ViewModels;

namespace PayDay.Tests.Sync.Loop;

/// <summary>
/// The Sync page's view model over real coordinators on the in-memory drive: not configured, turning
/// sync on (sign in, create with the recovery phrase shown once, or join with consent), blocked rows,
/// status and warnings, unlocking, rejoining, adding a device with the pairing countdown, the Notion
/// line, and turning sync off. All data is fictional.
/// </summary>
public sealed class SyncPageViewModelTests : IDisposable
{
    private readonly SyncWorld _world = new();

    public void Dispose() => _world.Dispose();

    private static SyncPageViewModel Vm(SyncDevice d, FakeDriveAccount? account = null, NotionSyncService? notion = null)
        => new(d.Coordinator, account ?? new FakeDriveAccount(), notion, d.Env.Time);

    private async Task<(SyncDevice Device, SyncPageViewModel Vm, string Phrase)> CreatedAsync(bool notionToken = false)
    {
        var a = await _world.AddDeviceAsync("Windows PC", notionToken: notionToken);
        a.Env.InsertLegacyData();
        var vm = Vm(a);
        await vm.LoadAsync();
        await vm.TurnOnAsync();
        _world.Advance(1000);
        await vm.CreateVaultAsync();
        var phrase = string.Join(' ', vm.RecoveryWords.Select(w => w[(w.IndexOf(' ') + 1)..]));
        vm.PhraseSaved = true;
        vm.FinishRecoveryPhrase();
        return (a, vm, phrase);
    }

    [Fact]
    public async Task WithoutAClientId_ThePageSaysOneDriveIsNotConfigured_AndDoesNothing()
    {
        var a = await _world.AddDeviceAsync("Windows PC");
        var vm = new SyncPageViewModel(null, new FakeDriveAccount { IsConfigured = false });

        await vm.LoadAsync();
        await vm.TurnOnAsync();

        Assert.Equal(SyncScreen.NotConfigured, vm.Screen);
        Assert.Equal("OneDrive isn't configured in this build.", vm.NotConfiguredMessage);
        Assert.False(a.Env.Sync.IsOn);
        Assert.Empty(_world.Drive.Paths);
    }

    [Fact]
    public async Task Loading_WithSyncOff_ChangesNothing_AndOffersTurnOn()
    {
        var a = await _world.AddDeviceAsync("Windows PC");
        a.Env.InsertLegacyData();
        var dump = a.Env.Dump();
        var account = new FakeDriveAccount();
        var vm = Vm(a, account);

        await vm.LoadAsync();

        Assert.Equal(SyncScreen.Off, vm.Screen);
        Assert.Equal(0, account.SignIns);
        Assert.False(a.Env.Sync.IsOn);
        Assert.Empty(a.Env.Store.WriteHistory);
        Assert.Equal(dump, a.Env.Dump());
        Assert.Empty(_world.Drive.Paths);
    }

    [Fact]
    public async Task TurnOn_SignsIn_FindsNoVault_ThenCreatesIt_AndShowsThePhraseOnceUntilConfirmed()
    {
        var a = await _world.AddDeviceAsync("Windows PC");
        a.Env.InsertLegacyData();
        var account = new FakeDriveAccount { SignedInUser = null };
        var vm = Vm(a, account);
        await vm.LoadAsync();

        await vm.TurnOnAsync();
        Assert.Equal(1, account.SignIns);
        Assert.Equal("someone@example.invalid", vm.AccountName);
        Assert.Equal(SyncScreen.Create, vm.Screen);
        Assert.False(a.Env.Sync.IsOn); // looking changed nothing

        _world.Advance(1000);
        await vm.CreateVaultAsync();

        Assert.Equal(SyncScreen.RecoveryPhrase, vm.Screen);
        Assert.Equal(24, vm.RecoveryWords.Count);
        Assert.Equal("1. ", vm.RecoveryWords[0][..3]);
        Assert.Contains(string.Join(' ', vm.RecoveryWords.Select(w => w[(w.IndexOf(' ') + 1)..])), vm.RecoveryPhraseFileText, StringComparison.Ordinal);
        Assert.Contains(a.Env.Store.WriteHistory, n => n.StartsWith(BackupRotationService.PreSyncFileNamePrefix, StringComparison.Ordinal));
        Assert.True(_world.Drive.Exists(SyncPaths.VaultFileName));

        // The phrase stays until the user confirms they saved it; then it is gone for good.
        Assert.False(vm.CanFinishRecoveryPhrase);
        vm.FinishRecoveryPhrase();
        Assert.Equal(SyncScreen.RecoveryPhrase, vm.Screen);
        vm.PhraseSaved = true;
        vm.FinishRecoveryPhrase();
        Assert.Equal(SyncScreen.Status, vm.Screen);
        Assert.Empty(vm.RecoveryWords);
        Assert.Null(vm.RecoveryPhraseFileText);
        Assert.Equal("Up to date", vm.StatusTitle);
    }

    [Fact]
    public async Task ACancelledSignIn_ChangesNothing()
    {
        var a = await _world.AddDeviceAsync("Windows PC");
        var account = new FakeDriveAccount { SignedInUser = null, NextSignIn = new DriveSignInResult(DriveSignInOutcome.Cancelled, null, "Sign-in was cancelled.") };
        var vm = Vm(a, account);
        await vm.LoadAsync();

        await vm.TurnOnAsync();

        Assert.Equal(SyncScreen.Off, vm.Screen);
        Assert.False(vm.HasError);
        Assert.Equal("Sign-in was cancelled.", vm.InfoMessage);
        Assert.Equal(0, _world.Drive.ListCalls);
    }

    [Fact]
    public async Task BlockedRows_AreListed_AndNothingIsWritten()
    {
        var a = await _world.AddDeviceAsync("Windows PC");
        a.Env.InsertLegacyData();
        a.Env.Exec("INSERT INTO Payments (Id, BillId, PeriodKey, AmountPaid) VALUES (9, '13', 'not-a-date', 1);");
        var dump = a.Env.Dump();
        var vm = Vm(a);
        await vm.LoadAsync();
        await vm.TurnOnAsync();

        await vm.CreateVaultAsync();

        Assert.Equal(SyncScreen.Blocked, vm.Screen);
        Assert.Contains(vm.BlockingRows, r => r.Contains("not-a-date", StringComparison.Ordinal));
        Assert.False(a.Env.Sync.IsOn);
        Assert.Empty(a.Env.Store.WriteHistory);
        Assert.Empty(_world.Drive.Paths);
        Assert.Equal(dump, a.Env.Dump());
        vm.Cancel();
        Assert.Equal(SyncScreen.Off, vm.Screen);
    }

    [Fact]
    public async Task Join_WithAPhrase_AsksBeforeReplacingThisDevicesData()
    {
        var (_, _, phrase) = await CreatedAsync();
        var b = await _world.AddDeviceAsync("Windows Laptop");
        b.Env.Exec("INSERT INTO Bills (Id, Name, Type, Payment) VALUES ('99', 'Local only bill', 'Other', 10);");
        var dump = b.Env.Dump();
        var vm = Vm(b);
        await vm.LoadAsync();
        await vm.TurnOnAsync();
        Assert.Equal(SyncScreen.Join, vm.Screen);

        vm.PhraseInput = "not the right words";
        await vm.JoinWithPhraseAsync();
        Assert.Equal("Check the words and try again.", vm.ErrorMessage);

        vm.PhraseInput = phrase;
        _world.Advance(1000);
        await vm.JoinWithPhraseAsync();
        Assert.True(vm.IsConsentPending);
        Assert.Contains("backup", vm.ConsentMessage, StringComparison.Ordinal);
        Assert.Equal(dump, b.Env.Dump());

        // Declining changes nothing.
        vm.CancelReplace();
        Assert.False(b.Env.Sync.IsOn);
        Assert.Equal(dump, b.Env.Dump());

        await vm.JoinWithPhraseAsync();
        await vm.ConfirmReplaceAsync();
        Assert.Equal(SyncScreen.Status, vm.Screen);
        Assert.Contains("backup", vm.InfoMessage, StringComparison.Ordinal);
        Assert.Equal(6, (await b.Env.Db.GetAllBillsAsync()).Count);
        Assert.Empty(vm.PhraseInput);
    }

    [Fact]
    public async Task AddADevice_ShowsTheQrWithACountdown_AndAnotherPcJoinsByTypingTheCode()
    {
        var (a, vmA, _) = await CreatedAsync();
        await vmA.StartPairingAsync();
        Assert.Equal(SyncScreen.Pairing, vmA.Screen);
        Assert.NotNull(vmA.PairingQrPng);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, vmA.PairingQrPng!.Take(4));
        Assert.StartsWith(PairingQr.Prefix, vmA.PairingCodeText, StringComparison.Ordinal);
        Assert.Equal("This code expires in 10:00.", vmA.PairingCountdownText);

        _world.Advance(61_000);
        await vmA.TickAsync();
        Assert.Equal("This code expires in 8:59.", vmA.PairingCountdownText);

        var b = await _world.AddDeviceAsync("Windows Laptop");
        var vmB = Vm(b);
        await vmB.LoadAsync();
        await vmB.TurnOnAsync();
        vmB.PairingCodeInput = vmA.PairingCodeText.ToLowerInvariant();
        await vmB.JoinWithPairingCodeAsync();
        Assert.Equal(SyncScreen.Status, vmB.Screen);
        Assert.True(b.Env.Sync.IsOn);

        // The PC notices the new device on its next pairing-screen sync and closes the screen.
        _world.Advance(16_000);
        await vmA.TickAsync();
        Assert.Equal(SyncScreen.Status, vmA.Screen);
        Assert.Equal("The new device joined.", vmA.InfoMessage);
        Assert.Null(vmA.PairingQrPng);
        Assert.DoesNotContain(_world.Drive.Paths, p => p.StartsWith(SyncPaths.PairingFolder, StringComparison.Ordinal));
        _ = a;
    }

    [Fact]
    public async Task ClosingThePairingScreen_DeletesThePairingFile_AndItExpiresAfterTenMinutes()
    {
        var (_, vm, _) = await CreatedAsync();
        await vm.StartPairingAsync();
        Assert.Single(_world.Drive.Paths, p => p.StartsWith(SyncPaths.PairingFolder, StringComparison.Ordinal));

        await vm.ClosePairingAsync();
        Assert.Equal(SyncScreen.Status, vm.Screen);
        Assert.DoesNotContain(_world.Drive.Paths, p => p.StartsWith(SyncPaths.PairingFolder, StringComparison.Ordinal));

        await vm.StartPairingAsync();
        _world.Advance(10 * 60_000);
        await vm.TickAsync();
        Assert.Equal(SyncScreen.Status, vm.Screen);
        Assert.Contains("expired", vm.InfoMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(_world.Drive.Paths, p => p.StartsWith(SyncPaths.PairingFolder, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ALockedPc_UnlocksWithItsPhrase_FromTheStatusScreen()
    {
        var (a, vm, phrase) = await CreatedAsync();
        a.Keys.Clear();
        await vm.SyncNowAsync();
        Assert.True(vm.NeedsUnlock);
        Assert.Equal("Sync is locked", vm.StatusTitle);

        vm.PhraseInput = phrase;
        await vm.UnlockWithPhraseAsync();

        Assert.False(vm.NeedsUnlock);
        Assert.Equal("Sync is unlocked.", vm.InfoMessage);
        Assert.Equal(SyncPhase.Idle, vm.Phase);
    }

    [Fact]
    public async Task Warnings_AndPendingUploads_ArePlainLanguage()
    {
        var (a, vm, phrase) = await CreatedAsync();
        var b = await _world.AddDeviceAsync("Windows Laptop");
        _world.Advance(1000);
        await b.Coordinator.JoinWithRecoveryPhraseAsync(phrase, false);
        var bill = (await b.Env.Routed.GetAllBillsAsync()).First();
        bill.Notes = "From the laptop";
        await b.Env.Routed.UpsertBillAsync(bill);
        _world.Advance(1000);
        await b.SyncAsync();
        var path = SyncPaths.ChangeFilePath(b.DeviceId, 2);
        var bytes = _world.Drive.Read(path);
        bytes[^1] ^= 1;
        _world.Drive.Overwrite(path, bytes);
        await vm.SyncNowAsync();
        _world.Advance(25 * 60 * 60 * 1000L);

        _world.Drive.FailNext(DriveOperation.Upload);
        var mine = (await a.Env.Routed.GetAllBillsAsync()).First();
        mine.Notes = "From the PC";
        await a.Env.Routed.UpsertBillAsync(mine);
        await vm.SyncNowAsync();

        Assert.Equal("Waiting to sync", vm.StatusTitle);
        Assert.Equal("1 batch of changes from this PC is waiting to upload.", vm.PendingText);
        Assert.Contains("A file from Windows Laptop could not be read.", vm.Warnings);
        Assert.True(vm.HasWarnings);
    }

    [Fact]
    public async Task TheNotionLine_NamesTheOwner_AndOffersToRunNotionHere()
    {
        var (a, vm, phrase) = await CreatedAsync();
        a.Credentials.Set(NotionSyncService.TokenKey, "secret_fictional_token");
        using var notion = new NotionSyncService(a.Env.Routed, a.Credentials, new FakeNotion(() => a.Env.Time.GetUtcNow()));
        var withNotion = Vm(a, notion: notion);
        await withNotion.LoadAsync();
        Assert.Equal("Notion: not connected on any device.", withNotion.NotionLine);
        Assert.True(withNotion.CanRunNotionHere);

        await withNotion.RunNotionHereAsync();
        Assert.Equal("Notion: connected on this PC.", withNotion.NotionLine);
        _world.Advance(1000);
        await a.SyncAsync();

        var b = await _world.AddDeviceAsync("Windows Laptop");
        _world.Advance(1000);
        await b.Coordinator.JoinWithRecoveryPhraseAsync(phrase, false);
        using var bNotion = new NotionSyncService(b.Env.Routed, b.Credentials);
        var vmB = Vm(b, notion: bNotion);
        await vmB.LoadAsync();
        Assert.StartsWith("Notion: connected on Windows PC.", vmB.NotionLine, StringComparison.Ordinal);
        Assert.False(vmB.CanRunNotionHere);
        _ = vm;
    }

    [Fact]
    public async Task TurnOff_LeavesTheVault_SignsOut_AndKeepsTheData()
    {
        var (a, vm, _) = await CreatedAsync();
        var account = new FakeDriveAccount();
        var page = Vm(a, account);
        await page.LoadAsync();
        var bills = (await a.Env.Db.GetAllBillsAsync()).Count;

        await page.TurnOffAsync();

        Assert.Equal(SyncScreen.Off, page.Screen);
        Assert.False(a.Env.Sync.IsOn);
        Assert.Equal(1, account.SignOuts);
        Assert.Null(page.AccountName);
        Assert.Equal(bills, (await a.Env.Db.GetAllBillsAsync()).Count);
        Assert.Contains("Sync is off", page.InfoMessage, StringComparison.Ordinal);
        Assert.True(_world.Drive.Exists(SyncPaths.VaultFileName));
        _ = vm;
    }

    [Fact]
    public async Task AnIdentityClash_OffersRejoin_WhichJoinsAgainUnderANewId()
    {
        var (a, _, phrase) = await CreatedAsync();
        var b = await _world.AddDeviceAsync("Windows Laptop");
        _world.Advance(1000);
        await b.Coordinator.JoinWithRecoveryPhraseAsync(phrase, false);
        var clone = await _world.AddDeviceAsync("Windows Laptop (copy)", clonePath: _world.CloneDatabase(b));
        clone.Env.Sync.Load();
        var vaultId = b.Env.Sync.Replica!.Read((_, m) => m.VaultId)!;
        clone.Keys.Save(vaultId, 1, b.Keys.Load(vaultId, 1)!);
        foreach (var d in new[] { b, clone })
        {
            var bill = (await d.Env.Routed.GetAllBillsAsync()).First();
            bill.Notes = d.Name;
            await d.Env.Routed.UpsertBillAsync(bill);
            _world.Advance(1000);
            await d.SyncAsync();
        }
        var vm = Vm(clone);
        await vm.LoadAsync();
        vm.RefreshStatus();
        Assert.True(vm.NeedsRejoin);
        var oldId = clone.DeviceId;

        await vm.RejoinAsync();
        Assert.Equal(SyncScreen.Join, vm.Screen);
        vm.PhraseInput = phrase;
        _world.Advance(1000);
        await vm.JoinWithPhraseAsync();
        await vm.ConfirmReplaceAsync();

        Assert.Equal(SyncScreen.Status, vm.Screen);
        Assert.NotEqual(oldId, clone.DeviceId);
        _ = a;
    }

    [Fact]
    public async Task StatusChanges_FromTheSyncLoop_GoThroughTheDispatcher()
    {
        var (a, _, _) = await CreatedAsync();
        var dispatched = new List<Action>();
        using var vm = new SyncPageViewModel(a.Coordinator, new FakeDriveAccount(), null, a.Env.Time, dispatched.Add);
        await vm.LoadAsync();

        await a.Coordinator.SyncAsync();

        Assert.NotEmpty(dispatched);
        foreach (var act in dispatched) act();
        Assert.Equal(SyncPhase.Idle, vm.Phase);
    }

    /// <summary>A drive account that signs in at once as a fictional user.</summary>
    private sealed class FakeDriveAccount : IDriveAccount
    {
        public string ProviderName => "OneDrive";

        public bool IsConfigured { get; set; } = true;

        public string? SignedInUser { get; set; } = "someone@example.invalid";

        public DriveSignInResult? NextSignIn { get; set; }

        public int SignIns { get; private set; }

        public int SignOuts { get; private set; }

        public Task<string?> GetSignedInUserAsync(CancellationToken ct = default) => Task.FromResult(SignedInUser);

        public Task<DriveSignInResult> SignInAsync(CancellationToken ct = default)
        {
            SignIns++;
            if (NextSignIn is { } next)
            {
                NextSignIn = null;
                return Task.FromResult(next);
            }
            SignedInUser = "someone@example.invalid";
            return Task.FromResult(new DriveSignInResult(DriveSignInOutcome.SignedIn, SignedInUser, null));
        }

        public Task SignOutAsync(CancellationToken ct = default)
        {
            SignOuts++;
            SignedInUser = null;
            return Task.CompletedTask;
        }
    }
}
