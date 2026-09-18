# Removes the ~15s stall Hearthstone hits on every game-server connect.
#
# The client resolves the game-server IP back to a name before it finishes connecting, and
# blocks its own network pump while it waits ("Network.ProcessNetwork not called for 15s 600ms"
# in Hearthstone's log). Blizzard's game-server ranges have no reverse-DNS records and typical
# resolvers never answer the query, so the client waits out the full Windows resolver timeout -
# on every match join AND every reconnect, because Windows does not cache a timed-out lookup.
#
# Adding stub entries to the hosts file makes the lookup resolve locally and instantly.
# This writes the same marked block that HsReconnector.exe maintains, so the app will pick it
# up and keep it current. The app does this on its own; this script is for applying the fix
# without running the app, or undoing it.
#
# Run elevated:
#   powershell -ExecutionPolicy Bypass -File tools\prime-dns.ps1
#   powershell -ExecutionPolicy Bypass -File tools\prime-dns.ps1 -Ranges 24.105.28.0/22
#   powershell -ExecutionPolicy Bypass -File tools\prime-dns.ps1 -Remove
#
#   -Ranges  one or more IPv4 ranges as a.b.c.d/22..24 (default: two Blizzard EU game-server
#            ranges). Take yours from the "GotoGameServe() - address=" lines in Hearthstone's
#            Logs\Hearthstone_<timestamp>\GameNetLogger.log.
#   -Remove  delete the block and restore the previous behaviour
# Ranges are seeded a /22 at a time. A /24 proved too narrow: servers were handed out across
# 5.42.176.x and 5.42.177.x, so seeding one /24 left the neighbouring range stalling for 15s.
param(
    [ValidatePattern('^(25[0-5]|2[0-4]\d|1?\d?\d)(\.(25[0-5]|2[0-4]\d|1?\d?\d)){3}/(2[2-4])$')]  # wider ranges would bloat the hosts file
    [string[]]$Ranges = @("5.42.176.0/22", "37.244.24.0/22"),
    [switch]$Remove
)

# "5.42.177.0/22" -> @{ Prefix = "5.42.176"; Bits = 22; Blocks = 4 }
function Split-Cidr([string]$cidr) {
    $parts = $cidr -split '/'
    $bits = [int]$parts[1]
    $o = $parts[0] -split '\.'
    $blocks = [int][math]::Pow(2, 24 - $bits)
    $base3 = ([int]$o[2]) -band (256 - $blocks)
    return @{ Prefix = "$($o[0]).$($o[1]).$base3"; Bits = $bits; Blocks = $blocks }
}

$ErrorActionPreference = "Stop"

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    throw "This must run as administrator - it edits the Windows hosts file. Right-click PowerShell > Run as administrator, then re-run."
}

$hosts  = Join-Path $env:SystemRoot "System32\drivers\etc\hosts"
$backup = "$hosts.hsreconnector.bak"
$begin  = "# BEGIN HsReconnector - reverse-DNS stubs"
$end    = "# END HsReconnector"

function Measure-Ptr([string]$ip) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    try { [System.Net.Dns]::GetHostEntry($ip) | Out-Null } catch { }
    $sw.Stop()
    return $sw.ElapsedMilliseconds
}

# --- Sample IP used to prove the change worked -------------------------------
$sample = (Split-Cidr $Ranges[0]).Prefix + ".253"
if (-not $Remove) {
    Write-Host "Measuring reverse lookup for $sample before the change (expect ~15s)..." -ForegroundColor Cyan
    $before = Measure-Ptr $sample
    Write-Host ("  before: {0:N0} ms" -f $before)
}

# --- Rewrite the managed block ------------------------------------------------
if (-not (Test-Path $backup)) { Copy-Item $hosts $backup }

$kept = @()
$inBlock = $false
foreach ($line in (Get-Content $hosts)) {
    $t = $line.Trim()
    if ($t -like "$begin*") { $inBlock = $true;  continue }
    if ($t -like "$end*")   { $inBlock = $false; continue }
    if (-not $inBlock)      { $kept += $line }
}
while ($kept.Count -gt 0 -and -not $kept[-1].Trim()) {
    if ($kept.Count -eq 1) { $kept = @() } else { $kept = $kept[0..($kept.Count - 2)] }
}

$sb = [Text.StringBuilder]::new()
foreach ($line in $kept) { [void]$sb.AppendLine($line) }

if (-not $Remove) {
    [void]$sb.AppendLine()
    [void]$sb.AppendLine($begin)
    [void]$sb.AppendLine("# Stub names for Hearthstone game-server ranges. Without these, Windows")
    [void]$sb.AppendLine("# never answers the reverse lookup the game does on connect and the client")
    [void]$sb.AppendLine("# freezes for ~15s every match join and every reconnect.")
    [void]$sb.AppendLine("# Delete this whole block to undo. Original file: hosts.hsreconnector.bak")
    foreach ($range in $Ranges) {
        $r = Split-Cidr $range
        $o = $r.Prefix -split '\.'
        [void]$sb.AppendLine("# range $($r.Prefix).0/$($r.Bits)")
        for ($b = 0; $b -lt $r.Blocks; $b++) {
            $third = [int]$o[2] + $b
            0..255 | ForEach-Object {
                $ip = "$($o[0]).$($o[1]).$third.$_"
                [void]$sb.AppendLine("$ip`ths-gs-$($ip -replace '\.','-').hsreconnector.invalid")
            }
        }
    }
    [void]$sb.AppendLine($end)
}

[IO.File]::WriteAllText($hosts, $sb.ToString(), [Text.UTF8Encoding]::new($false))
ipconfig /flushdns | Out-Null

if ($Remove) {
    Write-Host "Block removed; hosts file restored to its previous behaviour." -ForegroundColor Green
    return
}

Write-Host "Wrote $($Ranges.Count) range(s) to $hosts and flushed the DNS cache." -ForegroundColor Green

# --- Prove it -----------------------------------------------------------------
Write-Host "`nMeasuring the same lookup again..." -ForegroundColor Cyan
$after = Measure-Ptr $sample
Write-Host ("  after:  {0:N0} ms" -f $after)

if ($after -lt 100) {
    Write-Host ("`nWorking: {0:N0} ms -> {1:N0} ms. Hearthstone should now reconnect in well under a second." -f $before, $after) -ForegroundColor Green
} else {
    Write-Warning ("Still slow ({0:N0} ms). The hosts entry is not being used - check that the block exists in $hosts and that no antivirus reverted it." -f $after)
}
