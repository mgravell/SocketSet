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
    # First pass per side is discarded as warm-up, so this is scored passes + 1.
    [int]$Repetitions = 4,
    [int]$PortBase = 41000
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

$root = Join-Path ([System.IO.Path]::GetTempPath()) "ss-ab-$(Get-Date -Format yyyyMMddHHmmss)"
$worktrees = @()

function New-Side([string]$name, [string]$commit) {
    $path = Join-Path $root $name
    Write-Host "  worktree $name -> $commit" -ForegroundColor DarkGray
    & git -C $repo worktree add --detach --quiet $path $commit
    if ($LASTEXITCODE -ne 0) { throw "git worktree add failed for $commit" }
    $script:worktrees += $path
    & dotnet build (Join-Path $path "SmokeTest\SmokeTest.csproj") -f net10.0 -c Release -v q --nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "build failed for $commit" }
    return (Join-Path $path "SmokeTest\bin\Release\net10.0\SmokeTest.exe")
}

function Measure-Side([string]$exe, [int]$portBase) {
    $acc = @{}
    $p = $portBase
    foreach ($rep in 1..$Repetitions) {
        foreach ($sz in $Sizes) {
            $p++
            $proc = Start-Process $exe -ArgumentList @("--http", "--iocp", "-n", "$Shards", "-z", "$sz", "--port", "$p") `
                -PassThru -NoNewWindow -RedirectStandardOutput "$env:TEMP\ab.out" -RedirectStandardError "$env:TEMP\ab.err"
            try { $proc.ProcessorAffinity = [IntPtr]$serverMask } catch { }
            Start-Sleep 4
            $b = Start-Process $bombardier -ArgumentList @("-c", "$Connections", "-d", $Duration, "-o", "json", "-p", "r", "http://127.0.0.1:$p/") `
                -PassThru -NoNewWindow -RedirectStandardOutput "$env:TEMP\ab.json" -RedirectStandardError "$env:TEMP\ab.jerr"
            try { $b.ProcessorAffinity = [IntPtr]$clientMask } catch { }
            $b.WaitForExit()
            $j = Get-Content "$env:TEMP\ab.json" -Raw
            if ($j -match '"result"' -and $rep -gt 1) {
                $acc[$sz] = @($acc[$sz]) + [math]::Round((($j | ConvertFrom-Json).result.rps.mean * $sz / 1MB), 1)
            }
            try { if (-not $proc.HasExited) { $proc.Kill() } } catch { }
            Start-Sleep 2
        }
    }
    return $acc
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
    $hb = (Get-FileHash $exeBefore).Hash
    $ha = (Get-FileHash $exeAfter).Hash
    if ($hb -eq $ha) {
        Write-Host "ABORT: both sides produced an identical binary - there is nothing to compare." -ForegroundColor Red
        return
    }

    $rb = Measure-Side $exeBefore $PortBase
    $ra = Measure-Side $exeAfter ($PortBase + 500)

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
