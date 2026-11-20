[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('flow', 'donchian-30m', 'donchian-1h')]
    [string]$Profile,
    [switch]$EnableTelegram,
    [string]$DotnetPath = 'dotnet',
    [string]$AppPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$settings = switch ($Profile) {
    'flow' {
        @{ Config = 'signal-only.flow-25.example.json'; State = 'artifacts/flow-25-selected-v1' }
    }
    'donchian-30m' {
        @{ Config = 'signal-only.donchian-flow-30m.example.json'; State = 'artifacts/donchian-flow-30m-25-sl13-tp89' }
    }
    'donchian-1h' {
        @{ Config = 'signal-only.donchian-flow.example.json'; State = 'artifacts/donchian-flow-25-sl13-tp89' }
    }
}

if ([string]::IsNullOrWhiteSpace($AppPath)) {
    $AppPath = Join-Path $root 'src/TradingBot.App/bin/Release/net10.0/TradingBot.App.dll'
}
if (-not (Test-Path -LiteralPath $AppPath -PathType Leaf)) {
    [Console]::Error.WriteLine('Build the Release application before starting a profile.')
    exit 2
}
if ($EnableTelegram -and ([string]::IsNullOrWhiteSpace($env:TRADINGBOT_TELEGRAM_BOT_TOKEN) -or
        [string]::IsNullOrWhiteSpace($env:TRADINGBOT_TELEGRAM_CHAT_ID))) {
    [Console]::Error.WriteLine('Telegram environment variables are required when delivery is enabled.')
    exit 2
}

$stateDirectory = Join-Path $root $settings.State
New-Item -ItemType Directory -Force -Path $stateDirectory | Out-Null
$lockPath = Join-Path $stateDirectory 'research-profile.lock'
try {
    $lock = [System.IO.File]::Open($lockPath, [System.IO.FileMode]::OpenOrCreate,
        [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
}
catch [System.IO.IOException] {
    [Console]::Error.WriteLine('This profile is already running through the research-profile launcher.')
    exit 1
}

Push-Location $root
try {
    $commandArgs = @($AppPath, 'signal-only', '--config', $settings.Config)
    if ($EnableTelegram) {
        $commandArgs += @('--telegram-state', (Join-Path $stateDirectory 'telegram-delivery.sqlite'))
    }
    & $DotnetPath @commandArgs
    $result = $LASTEXITCODE
}
finally {
    Pop-Location
    $lock.Dispose()
}
exit $result
