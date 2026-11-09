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

function Set-TaskField([string]$path, [string]$taskId, [string]$columnName,
    [string]$value) {
    $lines = [System.IO.File]::ReadAllLines($path)
    $headerIndex = -1
    for ($index = 0; $index -lt $lines.Count; $index++) {
        if ($lines[$index] -match '^\|\s*ID\s*\|') {
            $headerIndex = $index
            break
        }
    }
    if ($headerIndex -lt 0) { throw 'Task table header was not found.' }

    $headers = @($lines[$headerIndex].Trim('|').Split('|') |
        ForEach-Object { $_.Trim() })
    $columnIndex = [array]::IndexOf([string[]]$headers, $columnName)
    if ($columnIndex -lt 0) { throw "Task column '$columnName' was not found." }

    $taskPattern = '^\|\s*' + [regex]::Escape($taskId) + '\s*\|'
    $rowIndex = -1
    $matchCount = 0
    for ($index = 0; $index -lt $lines.Count; $index++) {
        if ($lines[$index] -match $taskPattern) {
            $rowIndex = $index
            $matchCount++
        }
    }
    if ($matchCount -ne 1) { throw "Expected one task row for '$taskId', found $matchCount" }

    $cells = $lines[$rowIndex].Split('|')
    $cellIndex = $columnIndex + 1
    if ($cellIndex -ge ($cells.Count - 1)) {
        throw "Task row '$taskId' does not contain column '$columnName'."
    }
    $cells[$cellIndex] = " $value "
    $lines[$rowIndex] = [string]::Join('|', $cells)

    $encoding = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllLines($path, $lines, $encoding)
}

function Get-TaskRows([string]$path) {
    $lines = [System.IO.File]::ReadAllLines($path)
    $headerIndex = -1
    for ($index = 0; $index -lt $lines.Count; $index++) {
        if ($lines[$index] -match '^\|\s*ID\s*\|') {
            $headerIndex = $index
            break
        }
    }
    if ($headerIndex -lt 0) { throw 'Task table header was not found.' }

    $headers = @($lines[$headerIndex].Trim('|').Split('|') |
        ForEach-Object { $_.Trim() })
    $idIndex = [array]::IndexOf([string[]]$headers, 'ID') + 1
    $statusIndex = [array]::IndexOf([string[]]$headers, 'Status') + 1
    $dependencyIndex = [array]::IndexOf([string[]]$headers, 'Dependencies') + 1
    if ($idIndex -le 0 -or $statusIndex -le 0 -or $dependencyIndex -le 0) {
        throw 'Task table is missing an ID, Status, or Dependencies column.'
    }

    $rows = [System.Collections.Generic.List[object]]::new()
    foreach ($line in $lines) {
        if ($line -notmatch '^\|\s*P\d{2}-M\d{2}-T\d{3}\s*\|') { continue }
        $cells = $line.Split('|')
        $maximumIndex = [Math]::Max([Math]::Max($idIndex, $statusIndex), $dependencyIndex)
        if (($cells.Count - 1) -le $maximumIndex) {
            throw 'Task row does not contain its ID, Status, or Dependencies cell.'
        }
        $rows.Add([pscustomobject]@{
            Id = $cells[$idIndex].Trim()
            Status = $cells[$statusIndex].Trim()
            Dependencies = $cells[$dependencyIndex].Trim()
        })
    }
    return $rows.ToArray()
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
    Invoke-Case 'baseline' $baseline $true ''

    $crlfIgnore = New-Fixture 'crlf-gitignore'
    $ignorePath = Join-Path $crlfIgnore '.gitignore'
    $ignore = [System.IO.File]::ReadAllText($ignorePath)
    $ignore = $ignore.Replace("`r`n", "`n").Replace("`r", "`n").Replace("`n", "`r`n")
    [System.IO.File]::WriteAllText($ignorePath, $ignore,
        [System.Text.UTF8Encoding]::new($false))
    Invoke-Case 'CRLF .gitignore' $crlfIgnore $true ''

    $duplicate = New-Fixture 'duplicate-ids'
    $duplicateTasks = Join-Path $duplicate 'docs/TASKS.md'
    $duplicateLine = Get-Content -LiteralPath $duplicateTasks | Where-Object { $_ -match '^\|\s*P00-M01-T001\s*\|' } | Select-Object -First 1
    Add-Content -LiteralPath $duplicateTasks -Value $duplicateLine
    Invoke-Case 'duplicate IDs' $duplicate $false 'Duplicate task IDs'

    $unknownReference = New-Fixture 'unknown-reference'
    Set-TaskField (Join-Path $unknownReference 'docs/TASKS.md') 'P00-M01-T001' 'Requirements' 'REQ-UNKNOWN-999'
    Invoke-Case 'unknown requirement reference' $unknownReference $false 'unknown requirement'

    $uncovered = New-Fixture 'uncovered-requirement'
    $uncoveredTasks = Join-Path $uncovered 'docs/TASKS.md'
    Set-TaskField $uncoveredTasks 'P01-M01-T001' 'Requirements' 'REQ-ARCH-001;REQ-ARCH-004;REQ-BOOT-001'
    Set-TaskField $uncoveredTasks 'P01-M02-T004' 'Requirements' 'REQ-CI-001;REQ-DOC-001'
    Invoke-Case 'uncovered requirement' $uncovered $false 'not covered'

    $cycle = New-Fixture 'dependency-cycle'
    Set-TaskField (Join-Path $cycle 'docs/TASKS.md') 'P17-M01-T002' 'Dependencies' 'P17-M01-T002'
    Invoke-Case 'dependency cycle' $cycle $false 'cycle'

    $invalidStatus = New-Fixture 'invalid-status'
    Set-TaskField (Join-Path $invalidStatus 'docs/TASKS.md') 'P17-M01-T001' 'Status' 'PAUSED'
    Invoke-Case 'invalid status' $invalidStatus $false 'invalid status'

    $multipleReady = New-Fixture 'multiple-ready'
    $multipleTasks = Join-Path $multipleReady 'docs/TASKS.md'
    Set-TaskField $multipleTasks 'P17-M01-T001' 'Status' 'DONE'
    Set-TaskField $multipleTasks 'P17-M01-T001' 'Blockers / Evidence' 'Evidence: fixture completion'
    Set-TaskField $multipleTasks 'P17-M01-T002' 'Status' 'READY'
    Set-TaskField $multipleTasks 'P17-M01-T002' 'Blockers / Evidence' 'Evidence: fixture candidate'
    Set-TaskField $multipleTasks 'P17-M01-T003' 'Status' 'READY'
    Set-TaskField $multipleTasks 'P17-M01-T003' 'Dependencies' 'P17-M01-T001'
    Set-TaskField $multipleTasks 'P17-M01-T003' 'Blockers / Evidence' 'Evidence: fixture candidate'
    Invoke-Case 'multiple READY tasks' $multipleReady $true 'ready=P17-M01-T002,P17-M01-T003'

    $zeroReady = New-Fixture 'explainable-zero-ready'
    Invoke-Case 'explainable zero READY' $zeroReady $true ''

    $unexplainedZeroReady = New-Fixture 'unexplained-zero-ready'
    $unexplainedTasks = Join-Path $unexplainedZeroReady 'docs/TASKS.md'
    $taskRows = @(Get-TaskRows $unexplainedTasks)
    $tasksById = @{}
    foreach ($task in $taskRows) { $tasksById[$task.Id] = $task }
    foreach ($task in $taskRows) {
        if ($task.Status -ne 'BLOCKED') { continue }
        $dependencies = @([regex]::Matches($task.Dependencies, 'P\d{2}-M\d{2}-T\d{3}') |
            ForEach-Object Value)
        $dependenciesDone = $true
        foreach ($dependency in $dependencies) {
            if ($tasksById[$dependency].Status -ne 'DONE') { $dependenciesDone = $false }
        }

        Set-TaskField $unexplainedTasks $task.Id 'Blockers / Evidence' 'Evidence: fixture candidate'
        if ($dependenciesDone) {
            Set-TaskField $unexplainedTasks $task.Id 'Status' 'IN_PROGRESS'
        }
    }
    Invoke-Case 'unexplained zero READY' $unexplainedZeroReady $false ''

    $transition = New-Fixture 'next-task-transition'
    $transitionTasks = Join-Path $transition 'docs/TASKS.md'
    Set-TaskField $transitionTasks 'P17-M01-T001' 'Status' 'DONE'
    Set-TaskField $transitionTasks 'P17-M01-T001' 'Blockers / Evidence' 'Evidence: transition candidate'
    Set-TaskField $transitionTasks 'P17-M01-T002' 'Status' 'READY'
    Set-TaskField $transitionTasks 'P17-M01-T002' 'Blockers / Evidence' 'Evidence: transition candidate'
    Invoke-Case 'next-task transition' $transition $true 'ready=P17-M01-T002'

    Write-Output 'All validator fixture cases passed.'
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}
