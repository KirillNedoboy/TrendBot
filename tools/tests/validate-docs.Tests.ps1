[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$sourceRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$validator = Join-Path $sourceRoot 'tools/validate-docs.ps1'
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("tradingbot-docs-fixtures-" + [guid]::NewGuid().ToString('N'))
$relativeFiles = @(
    'AGENTS.md', 'HANDOFF.md', 'README.md', 'global.json', 'TradingBot.slnx',
    'Directory.Build.props', 'Directory.Packages.props', '.gitignore',
    'docs/PROJECT_SPEC.md', 'docs/EXECUTION_PLAN.md', 'docs/TASKS.md',
    'docs/sources/FINAL_TECH_SPEC.md', 'docs/decisions/README.md',
    'docs/decisions/ADR-011-data-quality-observability.md',
    'docs/decisions/ADR-012-promotion-gates.md'
)

function New-Fixture([string]$name) {
    $root = Join-Path $tempRoot $name
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    foreach ($relative in $relativeFiles) {
        $source = Join-Path $sourceRoot $relative
        $destination = Join-Path $root $relative
        $parent = Split-Path -Parent $destination
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
        Copy-Item -LiteralPath $source -Destination $destination -Force
    }
    return $root
}

function Replace-Text([string]$path, [string]$old, [string]$new) {
    $text = Get-Content -LiteralPath $path -Raw
    if (-not $text.Contains($old)) { throw "Fixture mutation text was not found: $old" }
    $encoding = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($path, $text.Replace($old, $new), $encoding)
}

function Invoke-Case([string]$name, [string]$root, [bool]$shouldPass, [string]$expectedText) {
    $output = @(& pwsh -NoProfile -File $validator -RepositoryPath $root 2>&1)
    $exitCode = $LASTEXITCODE
    $outputText = ($output | ForEach-Object ToString) -join [Environment]::NewLine
    if ($shouldPass -and $exitCode -ne 0) {
        throw "Fixture '$name' unexpectedly failed (exit $exitCode): $outputText"
    }
    if (-not $shouldPass -and $exitCode -eq 0) {
        throw "Fixture '$name' unexpectedly passed: $outputText"
    }
    if (-not [string]::IsNullOrWhiteSpace($expectedText) -and $outputText -notmatch $expectedText) {
        throw "Fixture '$name' produced unexpected output: $outputText"
    }
    Write-Output "PASS $name"
}

try {
    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

    $baseline = New-Fixture 'baseline'
    Invoke-Case 'baseline' $baseline $true 'docs validation passed: phases=19; milestones=41; tasks=119; ready=P02-M01-T001'

    $duplicate = New-Fixture 'duplicate-ids'
    $duplicateTasks = Join-Path $duplicate 'docs/TASKS.md'
    $duplicateLine = Get-Content -LiteralPath $duplicateTasks | Where-Object { $_ -match '^\|\s*P00-M01-T001\s*\|' } | Select-Object -First 1
    Add-Content -LiteralPath $duplicateTasks -Value $duplicateLine
    Invoke-Case 'duplicate IDs' $duplicate $false 'Duplicate task IDs'

    $unknownReference = New-Fixture 'unknown-reference'
    Replace-Text (Join-Path $unknownReference 'docs/TASKS.md') 'REQ-ARCH-001' 'REQ-UNKNOWN-999'
    Invoke-Case 'unknown requirement reference' $unknownReference $false 'unknown requirement'

    $uncovered = New-Fixture 'uncovered-requirement'
    Replace-Text (Join-Path $uncovered 'docs/TASKS.md') ';REQ-GIT-001' ''
    Invoke-Case 'uncovered requirement' $uncovered $false 'not covered'

    $cycle = New-Fixture 'dependency-cycle'
    Replace-Text (Join-Path $cycle 'docs/TASKS.md') '| P02-M02-T001 | BLOCKED | P02-M01-T003 |' '| P02-M02-T001 | BLOCKED | P02-M02-T003 |'
    Invoke-Case 'dependency cycle' $cycle $false 'cycle'

    $invalidStatus = New-Fixture 'invalid-status'
    Replace-Text (Join-Path $invalidStatus 'docs/TASKS.md') '| P02-M01-T001 | READY |' '| P02-M01-T001 | PAUSED |'
    Invoke-Case 'invalid status' $invalidStatus $false 'invalid status'

    $multipleReady = New-Fixture 'multiple-ready'
    $multipleTasks = Join-Path $multipleReady 'docs/TASKS.md'
    Replace-Text $multipleTasks '| P02-M01-T001 | READY |' '| P02-M01-T001 | DONE |'
    Replace-Text $multipleTasks '| P02-M01-T002 | BLOCKED | P02-M01-T001 |' '| P02-M01-T002 | DONE | P02-M01-T001 |'
    Replace-Text $multipleTasks '| P02-M01-T003 | BLOCKED | P02-M01-T002 |' '| P02-M01-T003 | DONE | P02-M01-T002 |'
    Replace-Text $multipleTasks '| P02-M02-T001 | BLOCKED | P02-M01-T003 |' '| P02-M02-T001 | READY | P02-M01-T003 |'
    Replace-Text $multipleTasks '| P02-M02-T002 | BLOCKED | P02-M02-T001 |' '| P02-M02-T002 | READY | P02-M01-T003 |'
    Replace-Text $multipleTasks 'Blocked by listed dependency' 'Evidence: fixture completion'
    Invoke-Case 'multiple READY tasks' $multipleReady $true 'ready=P02-M02-T001,P02-M02-T002'

    $zeroReady = New-Fixture 'explainable-zero-ready'
    Replace-Text (Join-Path $zeroReady 'docs/TASKS.md') '| P02-M01-T001 | READY | P01-M02-T004 |' '| P02-M01-T001 | BLOCKED | P01-M02-T004 |'
    Replace-Text (Join-Path $zeroReady 'docs/TASKS.md') 'Evidence: dependencies complete; first post-bootstrap task' 'Unresolved blocker: awaiting explicit approval'
    Invoke-Case 'explainable zero READY' $zeroReady $true 'ready=none \(explicit unresolved blocker\)'

    $transition = New-Fixture 'next-task-transition'
    $transitionTasks = Join-Path $transition 'docs/TASKS.md'
    Replace-Text $transitionTasks '| P02-M01-T001 | READY |' '| P02-M01-T001 | DONE |'
    Replace-Text $transitionTasks '| P02-M01-T002 | BLOCKED | P02-M01-T001 |' '| P02-M01-T002 | READY | P02-M01-T001 |'
    Replace-Text $transitionTasks 'Blocked by listed dependency' 'Evidence: transition candidate'
    Invoke-Case 'next-task transition' $transition $true 'ready=P02-M01-T002'

    Write-Output 'All validator fixture cases passed.'
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}
