[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$source = Join-Path (Split-Path -Parent $PSScriptRoot) 'connect-bybit-demo.ps1'
$tokens = $null
$errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($source, [ref]$tokens, [ref]$errors)
if ($errors.Count -ne 0) { throw 'Credential input script has syntax errors.' }
$definition = $ast.Find({ param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
    $node.Name -eq 'ConvertTo-DemoCredentialPayload'
}, $true)
if ($null -eq $definition) { throw 'Credential input converter is unavailable.' }
. ([scriptblock]::Create($definition.Extent.Text))

$passed = 0
$packet = ConvertTo-DemoCredentialPayload ("`t synthetic-key " + [char]0x00A0) "`r`nsynthetic!_+secret~`t" $true | ConvertFrom-Json
if ($packet.apiKey -cne 'synthetic-key' -or $packet.apiSecret -cne 'synthetic!_+secret~') {
    throw 'Pasted edge whitespace must be removed without changing credential characters.'
}
$passed++

$cases = @(
    @{ Key = 'synthetic-key'; Secret = 'synthetic-secret'; Consent = $false; Issue = 'missingConsent' },
    @{ Key = ''; Secret = 'synthetic-secret'; Consent = $true; Issue = 'invalidKey' },
    @{ Key = " `t "; Secret = 'synthetic-secret'; Consent = $true; Issue = 'invalidKey' },
    @{ Key = 'synthetic key'; Secret = 'synthetic-secret'; Consent = $true; Issue = 'invalidKey' },
    @{ Key = ('synthetic-' + [char]0x2603); Secret = 'synthetic-secret'; Consent = $true; Issue = 'invalidKey' },
    @{ Key = 'synthetic-key'; Secret = "synthetic`nsecret"; Consent = $true; Issue = 'invalidSecret' },
    @{ Key = 'synthetic-key'; Secret = ([string][char]0x2022) * 20; Consent = $true; Issue = 'invalidSecret' },
    @{ Key = 'x' * 4097; Secret = 'synthetic-secret'; Consent = $true; Issue = 'invalidKey' },
    @{ Key = 'synthetic-key'; Secret = 'x' * 4097; Consent = $true; Issue = 'invalidSecret' }
)
foreach ($case in $cases) {
    try {
        $null = ConvertTo-DemoCredentialPayload $case.Key $case.Secret $case.Consent
        throw 'Invalid input unexpectedly produced a credential payload.'
    }
    catch [ArgumentException] {
        if ($_.Exception.ParamName -ne $case.Issue) {
            throw 'Input rejection did not identify the correct field or consent requirement.'
        }
    }
    $passed++
}
$telegramPacket = ConvertTo-DemoCredentialPayload ("`t123456:" + ('a' * 32) + ' ') " -1001234567890`n" $true $true | ConvertFrom-Json
if ($telegramPacket.telegramBotToken -cne ('123456:' + ('a' * 32)) -or
    $telegramPacket.telegramChatId -cne '-1001234567890') {
    throw 'Telegram input must preserve the token and signed numeric destination.'
}
$passed++
foreach ($case in @(
    @{ Key = '123456:' + ('a' * 32); Secret = '123456'; Consent = $false; Issue = 'missingConsent' },
    @{ Key = 'not-a-token'; Secret = '123456'; Consent = $true; Issue = 'invalidKey' },
    @{ Key = '123456:' + ('a' * 32); Secret = '@somewhere'; Consent = $true; Issue = 'invalidSecret' },
    @{ Key = '123456:' + ('a' * 32); Secret = '0'; Consent = $true; Issue = 'invalidSecret' },
    @{ Key = '123456:' + ('a' * 32); Secret = '-100 123'; Consent = $true; Issue = 'invalidSecret' }
)) {
    try {
        $null = ConvertTo-DemoCredentialPayload $case.Key $case.Secret $case.Consent $true
        throw 'Invalid Telegram input unexpectedly produced a credential payload.'
    }
    catch [ArgumentException] {
        if ($_.Exception.ParamName -ne $case.Issue) { throw 'Telegram rejection identified the wrong field.' }
    }
    $passed++
}
Write-Output "credential input tests passed: $passed; synthetic data only, no window, SSH or API calls"
