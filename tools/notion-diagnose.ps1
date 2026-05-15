# Notion sync diagnostic — reads the token from Windows Credential Manager
# (PayDay:NotionToken), then asks Notion what the integration can access.
# Prints the seeded IDs at the bottom so you can compare.
#
# Run:  pwsh -File tools/notion-diagnose.ps1

$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class Cred {
    [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredRead(string target, uint type, uint reservedFlag, out IntPtr credential);

    [DllImport("Advapi32.dll", EntryPoint = "CredFree", SetLastError = true)]
    static extern void CredFree(IntPtr cred);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct CREDENTIAL {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    public static string Read(string target) {
        IntPtr ptr;
        if (!CredRead(target, 1u, 0u, out ptr)) return null;
        try {
            var c = Marshal.PtrToStructure<CREDENTIAL>(ptr);
            if (c.CredentialBlobSize == 0) return "";
            var bytes = new byte[c.CredentialBlobSize];
            Marshal.Copy(c.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.Unicode.GetString(bytes);
        } finally {
            CredFree(ptr);
        }
    }
}
"@

$token = [Cred]::Read("PayDay:NotionToken")
if (-not $token) {
    Write-Host "Token not found in Credential Manager under 'PayDay:NotionToken'." -ForegroundColor Red
    Write-Host "Open the app, save your token, then re-run this." -ForegroundColor Red
    exit 1
}
Write-Host ("Token read OK (length: {0}, prefix: {1}...)" -f $token.Length, $token.Substring(0, [Math]::Min(8, $token.Length))) -ForegroundColor Green

$headers = @{
    "Authorization" = "Bearer $token"
    "Notion-Version" = "2025-09-03"
    "Accept" = "application/json"
}

# ---------- /v1/users/me ----------
Write-Host "`n=== /v1/users/me ===" -ForegroundColor Cyan
try {
    $me = Invoke-RestMethod -Method Get -Uri "https://api.notion.com/v1/users/me" -Headers $headers
    Write-Host ("Bot id: {0}" -f $me.id)
    if ($me.bot) {
        Write-Host ("Bot name: {0}" -f $me.bot.workspace_name)
        Write-Host ("Owner: {0}" -f $me.bot.owner.type)
    }
} catch {
    Write-Host ("FAILED: {0}" -f $_.Exception.Message) -ForegroundColor Red
}

# ---------- /v1/search filtered to databases ----------
Write-Host "`n=== /v1/search (databases visible to integration) ===" -ForegroundColor Cyan
$searchBody = @{
    filter = @{ value = "database"; property = "object" }
    page_size = 100
} | ConvertTo-Json

$databases = @()
try {
    $results = Invoke-RestMethod -Method Post -Uri "https://api.notion.com/v1/search" -Headers $headers -Body $searchBody -ContentType "application/json"
    foreach ($r in $results.results) {
        $title = if ($r.title) { ($r.title | ForEach-Object { $_.plain_text }) -join "" } else { "(untitled)" }
        Write-Host ("`nDB: '{0}'" -f $title) -ForegroundColor Yellow
        Write-Host ("  database id : {0}" -f $r.id)
        if ($r.data_sources) {
            foreach ($ds in $r.data_sources) {
                Write-Host ("  data source : {0}  (id: {1})" -f $ds.name, $ds.id) -ForegroundColor Green
            }
        } else {
            Write-Host "  (no data_sources field in search result — fetching via GET /v1/databases/{id}...)" -ForegroundColor DarkYellow
            try {
                $db = Invoke-RestMethod -Method Get -Uri ("https://api.notion.com/v1/databases/{0}" -f $r.id) -Headers $headers
                if ($db.data_sources) {
                    foreach ($ds in $db.data_sources) {
                        Write-Host ("  data source : {0}  (id: {1})" -f $ds.name, $ds.id) -ForegroundColor Green
                    }
                } else {
                    Write-Host "  (still no data_sources — may be a legacy single-source DB; query via /v1/databases/{id}/query)" -ForegroundColor DarkYellow
                }
            } catch {
                Write-Host ("  GET databases/{0} failed: {1}" -f $r.id, $_.Exception.Message) -ForegroundColor Red
            }
        }
        $databases += $r
    }
    if ($databases.Count -eq 0) {
        Write-Host "`nNo databases visible. The integration has zero access." -ForegroundColor Red
        Write-Host "Open each Notion DB → ... menu → Connections → add 'PayDay'." -ForegroundColor Yellow
    }
} catch {
    Write-Host ("FAILED: {0}" -f $_.Exception.Message) -ForegroundColor Red
}

# ---------- Compare against our seeded IDs ----------
Write-Host "`n=== Seeded IDs in our local Settings table ===" -ForegroundColor Cyan
$seeded = @{
    NotionBillsDb     = "f5fe82ee-9224-4566-9ff9-22d7ce4510e8"
    NotionPaymentsDb  = "a953d84c-5b80-4c6c-baf4-ac5cd40b70ec"
    NotionSnapshotsDb = "612d5c43-fb3c-428a-8e85-a16234ee28b7"
}
foreach ($k in $seeded.Keys) {
    Write-Host ("  {0,-18} = {1}" -f $k, $seeded[$k])
}

# ---------- Schema dump for each seeded data source ----------
Write-Host "`n=== Data-source schemas (GET /v1/data_sources/{id}) ===" -ForegroundColor Cyan
foreach ($k in $seeded.Keys) {
    $id = $seeded[$k]
    Write-Host ("`n--- {0} ({1}) ---" -f $k, $id) -ForegroundColor Yellow
    try {
        $ds = Invoke-RestMethod -Method Get -Uri ("https://api.notion.com/v1/data_sources/{0}" -f $id) -Headers $headers
        if ($ds.properties) {
            foreach ($propName in ($ds.properties | Get-Member -MemberType NoteProperty | ForEach-Object { $_.Name } | Sort-Object)) {
                $prop = $ds.properties.$propName
                $extra = ""
                if ($prop.type -eq "select" -or $prop.type -eq "multi_select") {
                    $opts = ($prop.($prop.type).options | ForEach-Object { $_.name }) -join ", "
                    $extra = " options=[$opts]"
                }
                Write-Host ("  {0,-18} : {1}{2}" -f $propName, $prop.type, $extra)
            }
        }
    } catch {
        $resp = $_.Exception.Response
        if ($resp) {
            $reader = New-Object System.IO.StreamReader($resp.GetResponseStream())
            $body = $reader.ReadToEnd()
            Write-Host ("  FAILED {0}: {1}" -f [int]$resp.StatusCode, $body) -ForegroundColor Red
        } else {
            Write-Host ("  FAILED: {0}" -f $_.Exception.Message) -ForegroundColor Red
        }
    }
}

# ---------- Direct query against the seeded bills ID ----------
Write-Host "`n=== POST /v1/data_sources/{seededBills}/query (the call that's failing) ===" -ForegroundColor Cyan
$probeBody = @{ page_size = 1 } | ConvertTo-Json
try {
    $probe = Invoke-RestMethod -Method Post -Uri ("https://api.notion.com/v1/data_sources/{0}/query" -f $seeded.NotionBillsDb) -Headers $headers -Body $probeBody -ContentType "application/json"
    Write-Host ("OK — {0} results, has_more={1}" -f $probe.results.Count, $probe.has_more) -ForegroundColor Green
} catch {
    $resp = $_.Exception.Response
    if ($resp) {
        $reader = New-Object System.IO.StreamReader($resp.GetResponseStream())
        $body = $reader.ReadToEnd()
        Write-Host ("STATUS: {0}" -f [int]$resp.StatusCode) -ForegroundColor Red
        Write-Host ("BODY:   {0}" -f $body) -ForegroundColor Red
    } else {
        Write-Host ("FAILED: {0}" -f $_.Exception.Message) -ForegroundColor Red
    }
}

Write-Host "`nDone." -ForegroundColor Cyan
