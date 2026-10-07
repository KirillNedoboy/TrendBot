[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$RepositoryPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($RepositoryPath)) {
    $repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
}
else {
    $repo = (Resolve-Path -LiteralPath $RepositoryPath).Path
}

function Require-File([string]$relativePath) {
    $path = Join-Path $repo $relativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required file is missing: $relativePath"
    }
}

$required = @(
    'AGENTS.md', 'HANDOFF.md', 'README.md', 'global.json', 'TradingBot.slnx',
    'Directory.Build.props', 'Directory.Packages.props',
    'docs/PROJECT_SPEC.md', 'docs/EXECUTION_PLAN.md', 'docs/TASKS.md',
    'docs/sources/FINAL_TECH_SPEC.md', 'docs/decisions/README.md',
    'docs/decisions/ADR-011-data-quality-observability.md',
    'docs/decisions/ADR-012-promotion-gates.md'
)
foreach ($relative in $required) { Require-File $relative }

$spec = Get-Content -LiteralPath (Join-Path $repo 'docs/PROJECT_SPEC.md') -Raw
$plan = Get-Content -LiteralPath (Join-Path $repo 'docs/EXECUTION_PLAN.md') -Raw
$tasksText = Get-Content -LiteralPath (Join-Path $repo 'docs/TASKS.md') -Raw

$requirementMatches = [regex]::Matches($spec, '(?m)^\|\s*(REQ-[A-Z0-9-]+)\s*\|')
$requirementIds = @($requirementMatches | ForEach-Object { $_.Groups[1].Value })
if ($requirementIds.Count -eq 0) { throw 'No stable requirements were found in PROJECT_SPEC.md.' }
$duplicateRequirements = @($requirementIds | Group-Object | Where-Object Count -gt 1)
if ($duplicateRequirements.Count -gt 0) {
    throw "Duplicate requirement IDs: $(($duplicateRequirements | ForEach-Object Name) -join ', ')"
}
$knownRequirements = @{}
foreach ($requirement in $requirementIds) { $knownRequirements[$requirement] = $true }

$planLines = @($plan -split '\r?\n')
$phaseHeadingLines = @($planLines | Where-Object { $_ -match '^## Phase' })
foreach ($line in $phaseHeadingLines) {
    if ($line -notmatch '^## Phase\s+(?<id>\d{2})(?:\s|—)') {
        throw "Malformed phase heading: $line"
    }
}
$phaseMatches = [regex]::Matches($plan, '(?m)^## Phase\s+(?<id>\d{2})(?:\s|—)')
$phaseIds = @($phaseMatches | ForEach-Object { $_.Groups['id'].Value })
if ((@($phaseIds | Sort-Object -Unique)).Count -ne $phaseIds.Count) {
    throw 'Duplicate phase IDs detected.'
}
$expectedPhaseIds = @(0..18 | ForEach-Object { '{0:D2}' -f $_ })
$missingPhases = @($expectedPhaseIds | Where-Object { $phaseIds -notcontains $_ })
$extraPhases = @($phaseIds | Where-Object { $expectedPhaseIds -notcontains $_ })
if ($missingPhases.Count -gt 0 -or $extraPhases.Count -gt 0) {
    throw "Phase IDs must preserve 00–18. Missing: $($missingPhases -join ', '); extra: $($extraPhases -join ', ')."
}

$milestoneHeadingLines = @($planLines | Where-Object { $_ -match '^### P' })
foreach ($line in $milestoneHeadingLines) {
    if ($line -notmatch '^###\s+P\d{2}-M\d{2}(?:\s|—)') {
        throw "Malformed milestone heading: $line"
    }
}
$milestoneMatches = [regex]::Matches($plan, '(?m)^###\s+(?<id>P\d{2}-M\d{2})(?:\s|—)')
$milestoneIds = @($milestoneMatches | ForEach-Object { $_.Groups['id'].Value })
$duplicateMilestones = @($milestoneIds | Group-Object | Where-Object Count -gt 1)
if ($duplicateMilestones.Count -gt 0) {
    throw "Duplicate milestone IDs: $(($duplicateMilestones | ForEach-Object Name) -join ', ')"
}
$milestoneSet = @{}
foreach ($milestone in $milestoneIds) {
    $milestoneSet[$milestone] = 0
    $phasePart = $milestone.Substring(1, 2)
    if ($phaseIds -notcontains $phasePart) {
        throw "Milestone references unknown phase: $milestone"
    }
}
if (-not $plan.Contains('first `READY` task in document order')) {
    throw 'Execution plan does not document startup selection as first READY in document order.'
}
if ($plan -notmatch '(?i)zero\s+`READY`\s+tasks\s+is\s+valid\s+only\s+when') {
    throw 'Execution plan does not document the explicit-blocker rule for zero READY tasks.'
}

$taskLines = @($tasksText -split '\r?\n' | Where-Object { $_ -match '^\|\s*P' })
if ($taskLines.Count -eq 0) { throw 'No task rows were found in TASKS.md.' }
$taskPattern = [regex]'^\|\s*(?<id>P\d{2}-M\d{2}-T\d{3})\s*\|(?<status>[^|]*)\|(?<deps>[^|]*)\|(?<goal>[^|]*)\|(?<requirements>[^|]*)\|(?<acceptance>[^|]*)\|(?<validation>[^|]*)\|(?<evidence>[^|]*)\|\s*$'
$tasks = @()
foreach ($line in $taskLines) {
    $match = $taskPattern.Match($line)
    if (-not $match.Success) { throw "Malformed task row: $line" }

    $id = $match.Groups['id'].Value
    $status = $match.Groups['status'].Value.Trim()
    $dependencyText = $match.Groups['deps'].Value.Trim()
    $goal = $match.Groups['goal'].Value.Trim()
    $requirementText = $match.Groups['requirements'].Value.Trim()
    $acceptance = $match.Groups['acceptance'].Value.Trim()
    $validation = $match.Groups['validation'].Value.Trim()
    $evidence = $match.Groups['evidence'].Value.Trim()

    if ([string]::IsNullOrWhiteSpace($goal) -or [string]::IsNullOrWhiteSpace($acceptance) -or
        [string]::IsNullOrWhiteSpace($validation) -or [string]::IsNullOrWhiteSpace($evidence)) {
        throw "$id is missing goal/scope, acceptance, validation, or blocker/evidence text."
    }

    $dependencies = @()
    if ($dependencyText -notin @('', '—', '-')) {
        $dependencyMatches = [regex]::Matches($dependencyText, 'P\d{2}-M\d{2}-T\d{3}')
        if ($dependencyMatches.Count -eq 0) { throw "$id has malformed dependencies: $dependencyText" }
        $dependencies = @($dependencyMatches | ForEach-Object Value)
    }

    $taskRequirementMatches = [regex]::Matches($requirementText, 'REQ-[A-Z0-9-]+')
    if ($taskRequirementMatches.Count -eq 0) { throw "$id has no requirement reference." }
    $taskRequirements = @($taskRequirementMatches | ForEach-Object Value | Sort-Object -Unique)

    $tasks += [pscustomobject]@{
        Id = $id
        Status = $status
        Dependencies = $dependencies
        Goal = $goal
        Requirements = $taskRequirements
        Acceptance = $acceptance
        Validation = $validation
        Evidence = $evidence
    }
}

$duplicateTasks = @($tasks.Id | Group-Object | Where-Object Count -gt 1)
if ($duplicateTasks.Count -gt 0) {
    throw "Duplicate task IDs: $(($duplicateTasks | ForEach-Object Name) -join ', ')"
}
$tasksById = @{}
foreach ($task in $tasks) {
    $tasksById[$task.Id] = $task
    if ($task.Id -notmatch '^(?<phaseMilestone>P\d{2}-M\d{2})-T\d{3}$') {
        throw "Malformed task ID: $($task.Id)"
    }
    $phaseMilestone = $Matches['phaseMilestone']
    if (-not $milestoneSet.ContainsKey($phaseMilestone)) {
        throw "$($task.Id) references unknown milestone $phaseMilestone"
    }
    $milestoneSet[$phaseMilestone]++
    foreach ($requirement in $task.Requirements) {
        if (-not $knownRequirements.ContainsKey($requirement)) {
            throw "$($task.Id) references unknown requirement: $requirement"
        }
    }
}
$emptyMilestones = @($milestoneSet.GetEnumerator() | Where-Object Value -eq 0 | ForEach-Object Key)
if ($emptyMilestones.Count -gt 0) { throw "Milestones without task rows: $($emptyMilestones -join ', ')" }
$planTaskReferences = @([regex]::Matches($plan, '(?<![A-Z0-9-])P\d{2}-M\d{2}-T\d{3}(?![A-Z0-9-])') | ForEach-Object Value | Sort-Object -Unique)
foreach ($planTaskReference in $planTaskReferences) {
    if (-not $tasksById.ContainsKey($planTaskReference)) {
        throw "Execution plan references unknown task: $planTaskReference"
    }
}
foreach ($task in $tasks) {
    if ($planTaskReferences -notcontains $task.Id) {
        throw "Task is missing from execution plan: $($task.Id)"
    }
}

$coveredRequirements = @{}
foreach ($task in $tasks) {
    foreach ($requirement in $task.Requirements) { $coveredRequirements[$requirement] = $true }
}
$uncoveredRequirements = @($requirementIds | Where-Object { -not $coveredRequirements.ContainsKey($_) })
if ($uncoveredRequirements.Count -gt 0) {
    throw "Requirements not covered by any task: $($uncoveredRequirements -join ', ')"
}

$validStatuses = @('BLOCKED', 'READY', 'IN_PROGRESS', 'DONE')
foreach ($task in $tasks) {
    if ($validStatuses -notcontains $task.Status) { throw "$($task.Id) has invalid status: $($task.Status)" }
    foreach ($dependency in $task.Dependencies) {
        if (-not $tasksById.ContainsKey($dependency)) {
            throw "$($task.Id) depends on missing task $dependency"
        }
    }

    $dependenciesDone = $true
    foreach ($dependency in $task.Dependencies) {
        if ($tasksById[$dependency].Status -ne 'DONE') { $dependenciesDone = $false }
    }
    if (($task.Status -in @('READY', 'IN_PROGRESS', 'DONE')) -and -not $dependenciesDone) {
        throw "$($task.Id) is $($task.Status) while a dependency is not DONE."
    }
    if ($task.Status -eq 'DONE' -and $task.Evidence -notmatch '(?i)Evidence\s*:') {
        throw "$($task.Id) is DONE without evidence text."
    }
    if ($task.Evidence -match '(?i)Unresolved blocker\s*:' -and $task.Status -ne 'BLOCKED') {
        throw "$($task.Id) has an unresolved blocker but status is $($task.Status)."
    }
    if ($task.Status -in @('READY', 'IN_PROGRESS') -and $task.Evidence -match '(?i)(?:Blocked by|blocked until)') {
        throw "$($task.Id) is $($task.Status) but its blocker/evidence text still marks it blocked."
    }
    if ($task.Status -eq 'BLOCKED' -and $dependenciesDone -and $task.Evidence -notmatch '(?i)Unresolved blocker\s*:') {
        throw "$($task.Id) is BLOCKED with all dependencies DONE but has no explicit unresolved blocker."
    }
}

# Kahn-style dependency cycle check.
$remaining = @{}
foreach ($task in $tasks) { $remaining[$task.Id] = @($task.Dependencies) }
while ($remaining.Count -gt 0) {
    $free = @($remaining.Keys | Where-Object { $remaining[$_].Count -eq 0 })
    if ($free.Count -eq 0) { throw "Task dependency cycle detected involving: $($remaining.Keys -join ', ')" }
    foreach ($id in $free) { $remaining.Remove($id) }
    foreach ($id in @($remaining.Keys)) {
        $remaining[$id] = @($remaining[$id] | Where-Object { $free -notcontains $_ })
    }
}

$ready = @($tasks | Where-Object Status -eq 'READY')
if ($ready.Count -eq 0) {
    $explicitBlockers = @($tasks | Where-Object { $_.Status -eq 'BLOCKED' -and $_.Evidence -match '(?i)Unresolved blocker\s*:' })
    if ($explicitBlockers.Count -eq 0) {
        throw 'No READY task exists and no explicit unresolved blocker explains the empty ready set.'
    }
}

$source = Join-Path $repo 'docs/sources/FINAL_TECH_SPEC.md'
$sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
if ($sourceHash -ne '77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54') {
    throw "Canonical source spec hash mismatch: $sourceHash"
}

$buildProps = Get-Content -LiteralPath (Join-Path $repo 'Directory.Build.props') -Raw
if ($buildProps -notmatch '<Nullable>enable</Nullable>' -or
    $buildProps -notmatch '<EnableNETAnalyzers>true</EnableNETAnalyzers>' -or
    $buildProps -notmatch '<TreatWarningsAsErrors>true</TreatWarningsAsErrors>' -or
    $buildProps -notmatch '<Deterministic>true</Deterministic>') {
    throw 'Directory.Build.props does not contain the required deterministic/analyzer policy.'
}
$packageProps = Get-Content -LiteralPath (Join-Path $repo 'Directory.Packages.props') -Raw
if ($packageProps -notmatch '<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>') {
    throw 'Directory.Packages.props does not enable central package versions.'
}

$solution = Get-Content -LiteralPath (Join-Path $repo 'TradingBot.slnx') -Raw
$productionProjects = [regex]::Matches($solution, '(?m)^\s*<Project Path="src/[^/]+/[^/]+\.csproj"')
$testProjects = [regex]::Matches($solution, '(?m)^\s*<Project Path="tests/[^/]+/[^/]+\.csproj"')
if ($productionProjects.Count -ne 5 -or $testProjects.Count -ne 5) {
    throw "Required project graph mismatch: production=$($productionProjects.Count); tests=$($testProjects.Count)."
}
if ($solution -notmatch 'TradingBot\.Exchange\.Binance' -or
    $solution -notmatch 'TradingBot\.IntegrationTests' -or
    $solution -notmatch 'TradingBot\.Replay\.Tests') {
    throw 'Required exchange/integration/replay projects are missing from the solution.'
}

$gitIgnore = Get-Content -LiteralPath (Join-Path $repo '.gitignore') -Raw
if ($gitIgnore -notmatch '(?m)^graft/$') { throw 'graft/ is not ignored.' }

$readyIds = @($ready | ForEach-Object Id)
$readyText = if ($readyIds.Count -eq 0) { 'none (explicit unresolved blocker)' } else { $readyIds -join ',' }
Write-Output "docs validation passed: phases=$($phaseIds.Count); milestones=$($milestoneIds.Count); tasks=$($tasks.Count); ready=$readyText"
