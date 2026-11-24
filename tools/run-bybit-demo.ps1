[CmdletBinding()]
param(
    [ValidateSet('Check', 'Run')]
    [string]$Mode = 'Check',
    [string]$ConfigPath = 'bybit-demo.example.json',
    [string]$DotnetPath = 'dotnet',
    [switch]$EnableDemoOrders,
    [switch]$ExternalSignals
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($DotnetPath -eq 'dotnet' -and -not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    $executable = if ($IsWindows) { 'dotnet.exe' } else { 'dotnet' }
    $localDotnet = Join-Path (Join-Path $HOME '.dotnet') $executable
    if (Test-Path -LiteralPath $localDotnet -PathType Leaf) { $DotnetPath = $localDotnet }
}
$root = Split-Path -Parent $PSScriptRoot
$app = Join-Path $root 'src/TradingBot.App/bin/Release/net10.0/TradingBot.App.dll'
if (-not (Test-Path -LiteralPath $app -PathType Leaf)) {
    throw 'Build TradingBot.App in Release before running the demo command.'
}
if ($Mode -eq 'Run' -and -not $EnableDemoOrders) {
    throw 'Run requires -EnableDemoOrders. Check never uses private credentials or submits orders.'
}
$oldKey = $env:TRADINGBOT_BYBIT_DEMO_API_KEY
$oldSecret = $env:TRADINGBOT_BYBIT_DEMO_API_SECRET
Push-Location $root
try {
    $arguments = @($app, 'bybit-demo', $Mode.ToLowerInvariant(), '--config', $ConfigPath)
    if ($Mode -eq 'Run') {
        if ([string]::IsNullOrWhiteSpace($oldKey)) {
            $env:TRADINGBOT_BYBIT_DEMO_API_KEY = Read-Host 'Bybit Demo API key (local input only)' -MaskInput
        }
        if ([string]::IsNullOrWhiteSpace($oldSecret)) {
            $env:TRADINGBOT_BYBIT_DEMO_API_SECRET = Read-Host 'Bybit Demo API secret (local input only)' -MaskInput
        }
        $arguments += '--confirm-demo-orders'
        if ($ExternalSignals) { $arguments += '--external-signals' }
    }
    & $DotnetPath @arguments
    $result = $LASTEXITCODE
}
finally {
    $env:TRADINGBOT_BYBIT_DEMO_API_KEY = $oldKey
    $env:TRADINGBOT_BYBIT_DEMO_API_SECRET = $oldSecret
    Pop-Location
}
exit $result
