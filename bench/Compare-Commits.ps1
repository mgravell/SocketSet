<#
.SYNOPSIS
    Same-session A/B of two commits, measured back to back in ISOLATED git worktrees.

.DESCRIPTION
    Answers "did that change actually help", which cross-run comparisons cannot: on this host two
    identical builds measured up to 6% apart, so any delta smaller than that is indistinguishable from
    drift unless before and after are taken minutes apart under one power state.

    WHY WORKTREES, AND NOT `git checkout <sha> -- <paths>`.
    An earlier version of this harness did exactly that, and it silently corrupted the repository.
    `git checkout <commit> -- <paths>` updates the INDEX as well as the working tree. While it ran in the
    background, an unrelated `git add <one-file>; git commit` in the same checkout picked the staged
    reverts up - because `git commit` writes the whole index, not just what you added - and committed a
    revert of the very change under test under an unrelated message. The A/B then measured that reverted
    code as its "after" half, so it compared the old code with itself and reported a plausible ~4%
    "regression". Clean `git status`, passing build, believable number, entirely wrong.

    A worktree cannot do that: each side gets its own checkout and its own index, and the repository you
    are working in is never touched.

.PARAMETER Before
    Commit-ish for the baseline (default: HEAD~1).

.PARAMETER After
    Commit-ish for the change (default: HEAD).

.EXAMPLE
    .\Compare-Commits.ps1 -Before d2cec1e~1 -After d2cec1e
.EXAMPLE
    .\Compare-Commits.ps1 -Sizes 512,262144 -Repetitions 5
#>
[CmdletBinding()]
param(
    [string]$Before = "HEAD~1",
    [string]$After = "HEAD",
    [int[]]$Sizes = @(512, 4096, 16384, 262144),
    [int]$Connections = 64,
    [int]$Shards = 16,
    [string]$Duration = "5s",
    # Per-MEASUREMENT warm-up load, discarded. Distinct from discarding pass 1: every measurement starts
    # a fresh server process, so every one has a transient, not just the first.
    [string]$WarmupDuration = "3s",
    # First pass per side is discarded as warm-up, so this is scored passes + 1.
    [int]$Repetitions = 4,
    [int]$PortBase = 41000,
    # Measure the BRIDGED path (AspNetDemo through Kestrel) instead of the bare responder (SmokeTest
    # --http). Added 2026-07-29 because the headline Windows tables are bridged numbers, and a change
    # can land differently on the two: dd8cdce's copy removal is in OutboundConnection.Flush, which
    # both paths use, but the bridge sends a ReadOnlySequence of ~4KB pipe segments where the bare
    # responder sends one contiguous span - and that difference has already made one fix worth +58-65%
    # on the bare path and exactly nothing on the bridged one (see TODO item 1).
    [switch]$Bridged,
    [ValidateSet("iocp", "rio")]
    [string]$Backend = "iocp",
    # Extra demo/responder flags applied to BOTH sides, e.g. --byo, --pipe-segment 65536. Without this a
    # change that only affects an opt-in path (zero-copy send is reached only via --byo) measures as
    # exactly nothing, and the null result looks like a property of the change rather than of the leg.
    [string[]]$ExtraArgs = @()
)

$ErrorActionPreference = "Stop"
Add-Type -Namespace Bench -Name Power -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
public static extern uint SetThreadExecutionState(uint esFlags);
'@ -ErrorAction SilentlyContinue
try { [Bench.Power]::SetThreadExecutionState(0x80000000 -bor 0x00000001) | Out-Null } catch { }

$repo = Split-Path -Parent $PSScriptRoot
$bombardier = Join-Path $PSScriptRoot ".tools\bombardier.exe"
if (-not (Test-Path $bombardier)) { throw "bombardier missing: run a bench script once to fetch it" }

$cpuCount = [Environment]::ProcessorCount
$half = [int]($cpuCount / 2)
$serverMask = ([int64]1 -shl $half) - 1
$clientMask = ((([int64]1 -shl $cpuCount) - 1) -bxor $serverMask)
$env:DOTNET_PROCESSOR_COUNT = "$half"
$env:GOMAXPROCS = "$half"

# STABLE path, deliberately not timestamped. Each side builds a SmokeTest.exe that has to listen, and on
# Windows that means a firewall prompt for any path the firewall has not seen before. A timestamped root
# gives every run a brand-new path, so it prompts EVERY time and leaves behind a dead allow-rule for a
# directory that no longer exists - and a pending firewall dialog is a documented 2.8x benchmark
# confounder (see README). Fixed paths can be allow-listed once. The tradeoff is that a killed run leaves
# a worktree behind, so New-Side clears the path before reusing it.
$root = Join-Path ([System.IO.Path]::GetTempPath()) "ss-ab"
$worktrees = @()

& git -C $repo worktree prune 2>$null

function New-Side([string]$name, [string]$commit) {
    $path = Join-Path $root $name
    Write-Host "  worktree $name -> $commit" -ForegroundColor DarkGray
    # Reclaim the fixed path from any previous run that did not get to clean up.
    if (Test-Path $path) {
        & git -C $repo worktree remove --force $path 2>$null
        Remove-Item -Recurse -Force $path -ErrorAction SilentlyContinue
    }
    & git -C $repo worktree add --detach --quiet $path $commit
    if ($LASTEXITCODE -ne 0) { throw "git worktree add failed for $commit" }
    $script:worktrees += $path
    if ($Bridged) {
        & dotnet build (Join-Path $path "AspNetDemo\AspNetDemo.csproj") -c Release -v q --nologo | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "build failed for $commit" }
        return (Join-Path $path "AspNetDemo\bin\Release\net10.0\AspNetDemo.exe")
    }
    & dotnet build (Join-Path $path "SmokeTest\SmokeTest.csproj") -f net10.0 -c Release -v q --nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "build failed for $commit" }
    return (Join-Path $path "SmokeTest\bin\Release\net10.0\SmokeTest.exe")
}

# One measurement of one side at one payload. Split out of Measure-Side so the two sides can be
# INTERLEAVED (see below) rather than run as two blocks.
function Measure-One([string]$exe, [int]$port, [int]$sz) {
    $argList = if ($Bridged) { @("--$Backend", "--shards", "$Shards", "--port", "$port") + $ExtraArgs }
               else { @("--http", "--$Backend", "-n", "$Shards", "-z", "$sz", "--port", "$port") + $ExtraArgs }
    $proc = Start-Process $exe -ArgumentList $argList `
        -PassThru -NoNewWindow -RedirectStandardOutput "$env:TEMP\ab.out" -RedirectStandardError "$env:TEMP\ab.err"
    try { $proc.ProcessorAffinity = [IntPtr]$serverMask } catch { }
    try {
        # A TLS side speaks https and presents a self-signed cert, so both the gate and the load need -k.
        # Hard-wiring http here meant `-ExtraArgs --tls` produced "config mismatch" on every measurement:
        # the rig was talking plaintext to an https listener and reporting it as a TRANSPORT mismatch,
        # which reads as "the build is wrong" rather than "the harness cannot reach it".
        $scheme = if ($ExtraArgs -contains "--tls" -or $ExtraArgs -contains "--ktls") { "https" } else { "http" }
        $url = if ($Bridged) { "${scheme}://127.0.0.1:$port/payload?n=$sz" } else { "${scheme}://127.0.0.1:$port/" }
        if ($Bridged) {
            # Trust the banner, not the flag: refuse to measure a side whose transport is not the one asked for.
            $cfg = $null
            $deadline = (Get-Date).AddSeconds(40)
            while ((Get-Date) -lt $deadline) {
                $raw = & curl.exe -sk --max-time 3 "${scheme}://127.0.0.1:$port/config" 2>$null
                if ($LASTEXITCODE -eq 0 -and $raw) { try { $cfg = ($raw | ConvertFrom-Json).config; break } catch { } }
                Start-Sleep -Milliseconds 400
            }
            if ($cfg -notlike "*transport=socketset/$Backend*") { Write-Warning "config mismatch: $cfg"; return $null }
            # And gate TLS explicitly: without this a --tls side that silently fell back to plaintext would
            # pass the transport check and be measured as a TLS leg.
            if ($scheme -eq "https" -and $cfg -notlike "*tls=*" -or ($scheme -eq "https" -and $cfg -like "*tls=off*")) {
                Write-Warning "expected a TLS side, banner says: $cfg"; return $null
            }
        }
        else { Start-Sleep 4 }

        # A warm-up load before the scored one, per measurement. Added 2026-07-29: without it this rig's
        # per-side spread at 256KB was 6-10% where Run-TlsSizes.ps1 (which does warm up per leg) sees
        # 2.2% on the same leg and host. Discarding pass 1 does not substitute - every measurement here
        # starts a FRESH server process, so each one pays its own JIT and pool-fill transient, not just
        # the first. A rig that can only resolve 10% cannot answer the questions it is pointed at.
        foreach ($phase in @($WarmupDuration, $Duration)) {
            # -k: skip certificate verification (the demo's cert is a throwaway self-signed one). Harmless
            # on the plaintext legs, and required on the TLS ones.
            $b = Start-Process $bombardier -ArgumentList @("-k", "-c", "$Connections", "-d", $phase, "-o", "json", "-p", "r", $url) `
                -PassThru -NoNewWindow -RedirectStandardOutput "$env:TEMP\ab.json" -RedirectStandardError "$env:TEMP\ab.jerr"
            try { $b.ProcessorAffinity = [IntPtr]$clientMask } catch { }
            $b.WaitForExit()
        }
        $j = Get-Content "$env:TEMP\ab.json" -Raw
        if ($j -notmatch '"result"') { return $null }
        return [math]::Round((($j | ConvertFrom-Json).result.rps.mean * $sz / 1MB), 1)
    }
    finally {
        try { if (-not $proc.HasExited) { $proc.Kill() } } catch { }
        Start-Sleep 2
    }
}

function Get-Median([double[]]$v) {
    if ($v.Count -eq 0) { return 0 }
    $s = $v | Sort-Object; $m = [int][math]::Floor($s.Count / 2)
    if ($s.Count % 2 -eq 1) { $s[$m] } else { ($s[$m - 1] + $s[$m]) / 2 }
}

try {
    Write-Host "A/B: $Before  vs  $After" -ForegroundColor Cyan
    Write-Host "  cpus=$cpuCount server=0x$($serverMask.ToString('x')) client=0x$($clientMask.ToString('x'))  -c $Connections -d $Duration shards=$Shards"
    $exeBefore = New-Side "before" $Before
    $exeAfter = New-Side "after"  $After

    # Guard against the failure this harness exists because of: if the two builds are byte-identical, the
    # commits differ in nothing that affects this binary and any delta reported would be pure noise.
    #
    # In -Bridged mode hash SocketSet.dll, not the host exe: a transport-only change (dd8cdce touches
    # OutboundConnection.cs and nothing else) leaves AspNetDemo.exe identical on both sides, and hashing
    # it would abort a comparison that is perfectly valid.
    $hashOf = { param($exe) if ($Bridged) { Join-Path (Split-Path $exe) "SocketSet.dll" } else { $exe } }
    $hb = (Get-FileHash (& $hashOf $exeBefore)).Hash
    $ha = (Get-FileHash (& $hashOf $exeAfter)).Hash
    if ($hb -eq $ha) {
        Write-Host "ABORT: both sides produced an identical binary - there is nothing to compare." -ForegroundColor Red
        return
    }

    # INTERLEAVED, changed 2026-07-29. This used to measure all of `before`, then all of `after`, which
    # puts every before-pass earlier in wall-clock than every after-pass: anything that drifts over a run
    # (thermals, a background task starting, the client's ephemeral-port pool filling) lands entirely on
    # one side of the subtraction and is indistinguishable from the change. Alternating within each pass
    # costs nothing and removes that whole class of error.
    $rb = @{}; $ra = @{}
    $p = $PortBase
    foreach ($rep in 1..$Repetitions) {
        foreach ($sz in $Sizes) {
            $p += 2
            # Swap which side goes first on alternate passes, so neither one is always the leg that runs
            # into a still-settling host.
            $order = if ($rep % 2 -eq 1) { @(@{ e = $exeBefore; a = $rb; o = 0 }, @{ e = $exeAfter; a = $ra; o = 1 }) }
                     else { @(@{ e = $exeAfter; a = $ra; o = 1 }, @{ e = $exeBefore; a = $rb; o = 0 }) }
            foreach ($side in $order) {
                $v = Measure-One $side.e ($p + $side.o) $sz
                if ($null -ne $v -and $rep -gt 1) { $side.a[$sz] = @($side.a[$sz]) + $v }
            }
        }
    }

    Write-Host ""
    Write-Host "=== goodput MiB/s, median of $($Repetitions - 1) scored passes ===" -ForegroundColor Cyan
    Write-Host ("  {0,-9} {1,10} {2,11} {3,9}   {4}" -f "payload", "before", "after", "change", "passes (before | after)")
    foreach ($sz in $Sizes) {
        $b = Get-Median @($rb[$sz]); $a = Get-Median @($ra[$sz])
        $chg = if ($b -gt 0) { ($a - $b) / $b } else { 0 }
        Write-Host ("  {0,-9} {1,10:n1} {2,11:n1} {3,9:p1}   {4} | {5}" -f `
                $sz, $b, $a, $chg, (@($rb[$sz]) -join ','), (@($ra[$sz]) -join ','))
    }
    Write-Host ""
    Write-Host "Noise floor on this host has been measured at ~6% between IDENTICAL builds." -ForegroundColor Yellow
    Write-Host "Treat any change smaller than the per-side pass spread shown above as unproven."
}
finally {
    foreach ($w in $worktrees) {
        & git -C $repo worktree remove --force $w 2>$null
    }
    & git -C $repo worktree prune 2>$null
    if (Test-Path $root) { Remove-Item -Recurse -Force $root -ErrorAction SilentlyContinue }
    Write-Host "worktrees cleaned; your checkout was never touched" -ForegroundColor DarkGray
}
