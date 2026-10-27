<#
.SYNOPSIS
  Builds the Kickl Certificate Authority and keeps it healthy.

.DESCRIPTION
  A thin wrapper over tools\Kickl.Ca. It builds that tool itself (and rebuilds it if it is missing, out of date or
  broken), so you never have to.

  Commands
    setup       Build a working CA from nothing. Safe to run again: it never replaces a root key or a valid issuing key.
    health      Check everything and report. Exit code 0 ok, 1 warning, 2 bad.
    maintain    Health check, then repair what is safe. This is what the scheduled task runs.
    schedule    Register a daily maintenance task in Windows Task Scheduler.
    unschedule  Remove that task.
    status      Show the last maintenance result and whether anything needs a person.
    build       Just (re)build the CA tool.

  What "goes bad" and what happens
    issuing key expiring, expired, revoked, missing or mismatched   renewed (see ROOT KEY below)
    the CA tool missing, outdated or crashing                       rebuilt, then the command is retried once
    a ledger that fails its audit                                   REPORTED, never "repaired": it is evidence
    the shop changed its signing key                                REPORTED: only a person adds a shop (trust-shop)
    the CA website serving a different root, or not answering       REPORTED
  When a person is needed, NEEDS-ATTENTION.txt appears in the CA folder (and the command exits 2).

  ROOT KEY. Renewing the issuing key needs the root private key, which in production lives OFFLINE.
    -LocalRoot DIR   puts the root on THIS machine. Renewal is then fully automatic, but a break-in here also gets the
                     root. Fine for development or a deliberately single-box CA. The issuing key is renewed with no
                     human, and the old private key is destroyed.
    -RootPublic FILE uses an offline root. maintain prepares a renewal request, you sign it on the offline machine
                     (kickl-ca sign-renewal) and install it (kickl-ca install-renewal). Old seals keep verifying.

.EXAMPLE
  .\scripts\ca.ps1 setup -LocalRoot "$HOME\KICKL-CA-ROOT" -ShopUrl https://secupgrade.com/btcshop/ -PublicUrl https://secupgrade.com/secca/
  Builds a CA with a development root on this machine, trusting that shop.

.EXAMPLE
  .\scripts\ca.ps1 setup -RootPublic D:\ca-root\root-public.json -ShopUrl https://secupgrade.com/btcshop/
  Builds a CA that trusts an offline root. It stops at "needs a signature" and tells you the two commands to finish.

.EXAMPLE
  .\scripts\ca.ps1 schedule -At 03:30
  Checks and repairs the CA every day at 03:30.
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0, Mandatory = $true)]
    [ValidateSet('setup', 'health', 'maintain', 'schedule', 'unschedule', 'status', 'build')]
    [string]$Command,

    [string]$CaDir,
    [string]$LocalRoot,
    [string]$RootPublic,
    [string]$ShopUrl,
    [string]$ShopKeyFile,
    [string]$PublicUrl,
    [int]$Days = 90,
    [int]$RenewBefore = 21,
    [string]$At = '03:30',
    [switch]$DryRun,
    [switch]$Rebuild
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
$ToolProject = Join-Path $RepoRoot 'tools\Kickl.Ca\Kickl.Ca.csproj'
$ToolDll = Join-Path $RepoRoot 'tools\Kickl.Ca\bin\Release\net10.0\Kickl.Ca.dll'
if (-not $CaDir) { $CaDir = Join-Path $RepoRoot 'server\keys\ca' }
$TaskName = 'Kickl CA maintenance'

function Write-Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }

function Test-Dotnet {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw "The .NET 10 SDK is not installed or not on PATH. Install it from https://dotnet.microsoft.com/download and try again."
    }
}

# Is the built tool older than the code it is made from?
function Test-ToolStale {
    if (-not (Test-Path $ToolDll)) { return $true }
    $built = (Get-Item $ToolDll).LastWriteTimeUtc
    $sources = @(
        (Join-Path $RepoRoot 'tools\Kickl.Ca'),
        (Join-Path $RepoRoot 'src\Kickl.Core\Trust'),
        (Join-Path $RepoRoot 'src\Kickl.Core\Ownership'),
        (Join-Path $RepoRoot 'src\Kickl.Core\Registry')
    )
    foreach ($dir in $sources) {
        if (-not (Test-Path $dir)) { continue }
        $newer = Get-ChildItem $dir -Recurse -Include *.cs, *.csproj, *.json -File -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' -and $_.LastWriteTimeUtc -gt $built } |
            Select-Object -First 1
        if ($newer) { return $true }
    }
    return $false
}

function Build-Tool {
    Test-Dotnet
    Write-Step 'Building the CA tool'
    & dotnet build $ToolProject -c Release --nologo -v q | Out-Host
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $ToolDll)) {
        throw 'The CA tool did not build. Fix the build error above; the CA itself is untouched.'
    }
    # An up-to-date build leaves the file alone; mark it fresh so it is not rebuilt on every call.
    (Get-Item $ToolDll).LastWriteTimeUtc = [DateTime]::UtcNow
}

function Confirm-Tool {
    if ($Rebuild -or (Test-ToolStale)) { Build-Tool }
}

# Run the tool. Exit codes 0, 1 and 2 are answers; anything else means the tool itself is broken, so rebuild and retry once.
function Invoke-Ca {
    param([string[]]$ToolArgs)
    # Out-Host keeps the tool's report on screen; only the exit code is returned.
    & dotnet $ToolDll @ToolArgs | Out-Host
    $code = $LASTEXITCODE
    if ($code -gt 2 -or $code -lt 0) {
        Write-Warning "The CA tool failed (exit $code). Rebuilding it and trying once more."
        Build-Tool
        & dotnet $ToolDll @ToolArgs | Out-Host
        $code = $LASTEXITCODE
    }
    return [int]$code
}

function Update-AttentionFile($exitCode) {
    $flag = Join-Path $CaDir 'NEEDS-ATTENTION.txt'
    if ($exitCode -eq 2) {
        $status = Join-Path $CaDir 'status.json'
        $body = "The Kickl CA needs a person. Found $([DateTime]::Now.ToString('u')).`r`n`r`n"
        if (Test-Path $status) { $body += (Get-Content $status -Raw) }
        Set-Content -Path $flag -Value $body -Encoding UTF8
        Write-Warning "A person is needed. Details: $flag"
    }
    elseif (Test-Path $flag) {
        Remove-Item $flag -Force
    }
}

switch ($Command) {

    'build' {
        $Rebuild = $true
        Confirm-Tool
        Write-Host "Built $ToolDll"
    }

    'setup' {
        if (-not $LocalRoot -and -not $RootPublic -and -not (Test-Path (Join-Path $CaDir 'root-public.json'))) {
            Write-Host @"

Say where the root key lives:

  -LocalRoot <folder>    create (or reuse) the root on THIS machine. Simple, fully automatic renewal, but less secure:
                         anyone who gets into this machine also gets the root. Fine for development.
  -RootPublic <file>     use a root that stays OFFLINE; give its root-public.json. Renewals then need you to sign a
                         request on the offline machine about every 90 days.

Example:
  .\scripts\ca.ps1 setup -LocalRoot "`$HOME\KICKL-CA-ROOT" -ShopUrl https://secupgrade.com/btcshop/
"@
            exit 1
        }
        if ($LocalRoot -and $RootPublic) { throw 'Choose one: -LocalRoot or -RootPublic.' }

        Confirm-Tool
        $toolArgs = @('setup', '--ca-dir', $CaDir, '--days', $Days, '--renew-before', $RenewBefore)
        if ($LocalRoot)   { $toolArgs += @('--root-dir', $LocalRoot) }
        if ($RootPublic)  { $toolArgs += @('--root-public', $RootPublic) }
        if ($ShopUrl)     { $toolArgs += @('--shop-url', $ShopUrl) }
        if ($ShopKeyFile) { $toolArgs += @('--shop-key-file', $ShopKeyFile) }
        if ($PublicUrl)   { $toolArgs += @('--public-url', $PublicUrl) }

        Write-Step "Building the CA in $CaDir"
        $code = Invoke-Ca $toolArgs
        if ($code -eq 0) {
            Write-Host ''
            Write-Host 'Next:' -ForegroundColor Green
            Write-Host "  1. Copy $(Join-Path $CaDir 'root-public.json') to src\Kickl.Core\Trust\ca-root.json and rebuild the browser,"
            Write-Host '     so browsers trust this CA.'
            Write-Host '  2. Publish server\secca as your CA website (see docs\23-certificate-authority.md).'
            Write-Host '  3. Keep it healthy automatically:   .\scripts\ca.ps1 schedule'
        }
        exit $code
    }

    'health' {
        Confirm-Tool
        $code = Invoke-Ca @('health', '--ca-dir', $CaDir)
        exit $code
    }

    'maintain' {
        Confirm-Tool
        $toolArgs = @('maintain', '--ca-dir', $CaDir)
        if ($DryRun) { $toolArgs += '--dry-run' }
        $code = Invoke-Ca $toolArgs
        if (-not $DryRun) { Update-AttentionFile $code }
        exit $code
    }

    'schedule' {
        $script = $PSCommandPath
        $argument = "-NoProfile -ExecutionPolicy Bypass -File `"$script`" maintain -CaDir `"$CaDir`""
        $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $argument -WorkingDirectory $RepoRoot
        $trigger = New-ScheduledTaskTrigger -Daily -At $At
        $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Minutes 20)
        Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings `
            -Description 'Checks the Kickl CA and repairs what is safe to repair.' -Force | Out-Null
        Write-Host "Scheduled '$TaskName' daily at $At. Results: $(Join-Path $CaDir 'status.json') and maintain.log."
        Write-Host 'It runs while you are logged on. For an always-on server, edit the task to run whether or not you are logged on.'
    }

    'unschedule' {
        if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
            Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
            Write-Host "Removed '$TaskName'."
        }
        else {
            Write-Host "'$TaskName' is not scheduled."
        }
    }

    'status' {
        $statusFile = Join-Path $CaDir 'status.json'
        if (Test-Path $statusFile) {
            $s = Get-Content $statusFile -Raw | ConvertFrom-Json
            $when = [DateTimeOffset]::FromUnixTimeSeconds([long]$s.checkedAt).LocalDateTime
            Write-Host "Last check: $when   overall: $($s.overall)   needs a person: $($s.needsHuman)"
            foreach ($i in $s.items) { Write-Host ("  {0,-5} {1,-14} {2}" -f $i.level, $i.name, $i.detail) }
            foreach ($a in $s.actions) { Write-Host "  action: $a" }
        }
        else {
            Write-Host 'No maintenance has run yet. Try:  .\scripts\ca.ps1 maintain'
        }
        $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
        Write-Host ("Scheduled task: " + $(if ($task) { "yes ($($task.State))" } else { 'no' }))
        if (Test-Path (Join-Path $CaDir 'NEEDS-ATTENTION.txt')) { Write-Warning 'NEEDS-ATTENTION.txt is present.' }
    }
}
