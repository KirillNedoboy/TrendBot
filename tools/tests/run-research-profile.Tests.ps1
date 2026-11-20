[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$AppPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$source = Join-Path (Split-Path -Parent $PSScriptRoot) 'run-research-profile.ps1'
$root = Join-Path ([System.IO.Path]::GetTempPath()) ('tradingbot-profile-test-' + [Guid]::NewGuid().ToString('N'))
$tools = Join-Path $root 'tools'
$runner = Join-Path $tools 'run-research-profile.ps1'
$pwsh = (Get-Process -Id $PID).Path
$profiles = @(
    @{ Name = 'flow'; State = 'artifacts/flow-25-selected-v1' },
    @{ Name = 'donchian-30m'; State = 'artifacts/donchian-flow-30m-25-sl13-tp89' },
    @{ Name = 'donchian-1h'; State = 'artifacts/donchian-flow-25-sl13-tp89' }
)

try {
    New-Item -ItemType Directory -Force -Path $tools | Out-Null
    Copy-Item -LiteralPath $source -Destination $runner
    Remove-Item Env:TRADINGBOT_TELEGRAM_BOT_TOKEN -ErrorAction Ignore
    Remove-Item Env:TRADINGBOT_TELEGRAM_CHAT_ID -ErrorAction Ignore

    foreach ($profile in $profiles) {
        $state = Join-Path $root $profile.State
        & $pwsh -NoProfile -File $runner -Profile $profile.Name -AppPath $AppPath -EnableTelegram 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 2 -or (Test-Path -LiteralPath $state)) {
            throw 'Missing Telegram credentials must fail before state creation.'
        }

        New-Item -ItemType Directory -Force -Path $state | Out-Null
        $lockPath = Join-Path $state 'research-profile.lock'
        $lock = [System.IO.File]::Open($lockPath, [System.IO.FileMode]::OpenOrCreate,
            [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
        try {
            & $pwsh -NoProfile -File $runner -Profile $profile.Name -AppPath $AppPath 2>&1 | Out-Null
            if ($LASTEXITCODE -ne 1) {
                throw 'A second launcher must reject an already locked profile.'
            }
            if (Test-Path -LiteralPath (Join-Path $state 'signal-journal.sqlite')) {
                throw 'A rejected launcher must not open the signal journal.'
            }
        }
        finally {
            $lock.Dispose()
        }
    }
    Write-Output 'research profile launcher tests passed: 3 profiles, credential and lock guards'
}
finally {
    if (Test-Path -LiteralPath $root) {
        Remove-Item -LiteralPath $root -Recurse -Force
    }
}
