<#
.SYNOPSIS
    Narrow TODO item 0f: a UDS-only connection leak under churn (exactly one server-side connection
    never reaches OnClosed). Same method that cracked 0e - remove one variable at a time.

.DESCRIPTION
    The leak is quiet: no crash, no wedge of the whole process, just `live=1 (client=0 server=1)` after a
    churn that moved ~12,000 connections. Sustained churn would exhaust the slot table. The smoke matrix
    does not see it because its churn cell is TCP.

    WHAT EACH VARIANT WOULD MEAN, written before running:
      TCP control     Must be clean. If TCP leaks too, this is not UDS-specific and 0f is misfiled.
      graceful close  Removes SO_LINGER{0}/RST. Gone => the abortive path is what strands it.
      shards 1        Gone => it involves the accept hand-off between shards (AF_UNIX is single-listener,
                      so every accepted socket is bounced via TryPlace to another shard - unlike the
                      multi-bind IP path).
      sockets 4096    Gone => needs a tight table, i.e. slot reuse (0e's signal).
      close-after 1   More closes per connection. Worse => the leak scales with closes.
      managed backend Clean => the fault is in the IOCP accept/teardown path, not in AF_UNIX on Windows.

    The managed row is the important control: it is the same OS socket type through completely different
    transport code, and it is what proved the earlier UDS connect bug was IOCP's rather than Windows'.
#>
[CmdletBinding()]
param([string]$Exe, [int]$Reps = 6, [int]$Seconds = 6)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
if (-not $Exe) { $Exe = Join-Path $repo "SmokeTest\bin\Release\net10.0\SmokeTest.exe" }
if (-not (Test-Path $Exe)) { throw "no SmokeTest.exe at $Exe" }

$dir = Join-Path $env:TEMP "ss-0f"
New-Item -ItemType Directory -Force $dir | Out-Null
$port = 18500

# base shape: --iocp -s -c 32 --churn N --close-after 2 --sockets 128 --reset-close  over UDS
$variants = @(
    @{ n = "baseline (UDS)      "; tp = @("--iocp"); ep = "uds"; extra = @("--reset-close") }
    @{ n = "CONTROL tcp         "; tp = @("--iocp"); ep = "tcp"; extra = @("--reset-close") }
    @{ n = "CONTROL managed uds "; tp = @("-m"); ep = "uds"; extra = @("--reset-close") }
    @{ n = "graceful close      "; tp = @("--iocp"); ep = "uds"; extra = @() }
    @{ n = "shards 1            "; tp = @("--iocp", "-n", "1"); ep = "uds"; extra = @("--reset-close") }
    @{ n = "sockets 4096        "; tp = @("--iocp"); ep = "uds"; extra = @("--reset-close"); sockets = "4096" }
    @{ n = "close-after 1       "; tp = @("--iocp"); ep = "uds"; extra = @("--reset-close"); closeAfter = "1" }
)

Write-Host ""
Write-Host "0f bisection: $($variants.Count) variants x $Reps reps x ${Seconds}s" -ForegroundColor Cyan
Write-Host ""

foreach ($v in $variants) {
    $leaks = 0; $worst = 0; $ran = 0
    foreach ($rep in 1..$Reps) {
        $port++
        $sock = Join-Path $dir "v$($v.n.Trim() -replace '\W','')$rep.sock"
        if (Test-Path -LiteralPath $sock) { Remove-Item -LiteralPath $sock -Force }
        $epArgs = if ($v.ep -eq "uds") { @("-u", $sock) } else { @("--port", "$port") }
        $args = @($v.tp) + @("-s", "-c", "32", "--churn", "$Seconds",
            "--close-after", $(if ($v.closeAfter) { $v.closeAfter } else { "2" }),
            "--sockets", $(if ($v.sockets) { $v.sockets } else { "128" })) + $v.extra + $epArgs
        $o = Join-Path $dir "v.out"
        $p = Start-Process -FilePath $Exe -ArgumentList $args -PassThru -NoNewWindow `
            -RedirectStandardOutput $o -RedirectStandardError (Join-Path $dir "v.err")
        $p.WaitForExit(($Seconds + 90) * 1000) | Out-Null
        $t = Get-Content $o -Raw -ErrorAction SilentlyContinue
        if ($t -match "live=(\d+) \(client=(\d+) server=(\d+)\)") {
            $ran++
            $live = [int]$Matches[1]
            if ($live -gt 0) { $leaks++; if ($live -gt $worst) { $worst = $live } }
        }
    }
    $colour = if ($leaks -gt 0) { "Red" } else { "Green" }
    Write-Host ("  {0}  {1}/{2} leaked (worst live={3}, {4} ran)" -f $v.n, $leaks, $Reps, $worst, $ran) -ForegroundColor $colour
}
Write-Host ""
Write-Host "Read the CONTROLs first: tcp and managed-uds must be clean, or 0f is misfiled." -ForegroundColor Yellow
