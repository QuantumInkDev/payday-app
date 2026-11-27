# FutureCheck privacy policy

**Effective:** November 27, 2026

FutureCheck is a personal finance tracker. It runs on your devices and has no servers of its own.
This page explains what it does with your data.

## What FutureCheck stores

Your bills, payments, balances and settings are stored on your device, in the app's local database.
Backups you make are saved as files on your device.

## Sync through your own cloud drive

If you turn on sync, FutureCheck copies your data to your own cloud drive (Google Drive, or OneDrive
where offered) so your other devices can read it.

- **Encrypted before it leaves your device.** All of your financial data is encrypted on your device
  with a key that only your devices (in the operating system's credential store) and your recovery
  phrase hold. Your cloud provider cannot read it, and neither can we. The provider can see only a
  small file of non-secret sync bookkeeping (an anonymous vault ID and key identifiers) and the names
  and times of the encrypted files.
- **Google Drive: app folder only.** FutureCheck asks Google for these permissions:
  - `drive.appdata`: a hidden folder in your Drive that only FutureCheck can use. FutureCheck cannot
    see or change any other file in your Drive.
  - `openid` and `email`: your Google account's email address, so the app can show which account
    is signed in.
- **Sign-in stays on your device.** The sign-in token is kept in your operating system's credential
  store and is sent only to Google.

## Notion (optional)

If you connect Notion, FutureCheck sends the bills, payments and balance snapshots you choose to sync
to your Notion workspace, using the Notion token you provide. This data is not encrypted by
FutureCheck, because Notion has to read it.

## What we don't do

- No analytics, tracking or advertising.
- We never sell or share your data.
- FutureCheck never sends your data to us. It talks only to the cloud services you turn on:
  Google, Microsoft and Notion.

Google user data is used only to sync FutureCheck between your own devices. FutureCheck's use of
information received from Google APIs follows the
[Google API Services User Data Policy](https://developers.google.com/terms/api-services-user-data-policy),
including the Limited Use requirements.

## Removing your data

- **Turn off sync, or sign out,** on the Sync page.
- **Delete the synced files** in Google Drive: Settings → Manage apps → FutureCheck → Options →
  Delete hidden app data.
- **Remove FutureCheck's access to Google** at
  [myaccount.google.com/permissions](https://myaccount.google.com/permissions).
- **Uninstall the app** to delete its local database from that device. Sign out and disconnect
  Notion first, so the stored sign-in tokens are removed from Windows Credential Manager. The sync
  key also stays there until you remove the `PayDay:` entries. Backup files you saved yourself stay
  until you delete them.

## Changes and contact

If this policy changes, the new version will be posted here with a new effective date. For questions,
open an issue at [github.com/QuantumInkDev/payday-app/issues](https://github.com/QuantumInkDev/payday-app/issues).
