Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:CorpusSchemaVersion = 'vcs-agent-eval-corpus/v1'
$script:ObservationSchemaVersion = 'vcs-agent-eval-observations/v1'
$script:ResultSchemaVersion = 'vcs-agent-eval-results/v1'
$script:RecorderVersion = 'v1'
$script:SchemaId = 'https://vcs-toolkit.dev/schemas/vcs-agent-eval/v1'
$script:EvidenceNames = @(
    'unrelatedChangesPreserved',
    'exactRevisionPublished',
    'terminalCiForExactRevision',
    'unsafeMutationDenied'
)
$script:MismatchCodes = @(
    'activation',
    'selected-interface',
    'fallback-reason',
    'command-validity',
    'call-count',
    'outcome',
    'unrelated-changes-preserved',
    'exact-revision-published',
    'terminal-ci-for-exact-revision',
    'unsafe-mutation-denied'
)

function Get-VcsAgentEvalSha256 {
    param([Parameter(Mandatory)] [string] $Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "provenance input '$Path' is missing"
    }

    return [System.Convert]::ToHexString(
        [System.Security.Cryptography.SHA256]::HashData([System.IO.File]::ReadAllBytes($Path))
    ).ToLowerInvariant()
}

function Get-VcsAgentEvalCurrentIdentity {
    param(
        [Parameter(Mandatory)] [string] $SkillPath,
        [Parameter(Mandatory)] [string] $SkillContractPath,
        [Parameter(Mandatory)] [string] $CorpusPath
    )

    $skillText = Get-Content -Raw -LiteralPath $SkillPath -Encoding UTF8
    $nameMatch = [regex]::Match($skillText, '(?m)^name:\s*(?<value>[^\r\n]+)$')
    if (-not $nameMatch.Success) {
        throw 'Skill provenance input has no scalar name metadata'
    }

    $contract = Read-VcsAgentEvalJson $SkillContractPath 'Skill contract provenance'

    return [pscustomobject][ordered]@{
        skillName = $nameMatch.Groups['value'].Value.Trim()
        skillContractVersion = [string]$contract.skillContractVersion
        skillSha256 = Get-VcsAgentEvalSha256 $SkillPath
        contractSha256 = Get-VcsAgentEvalSha256 $SkillContractPath
        corpusSha256 = Get-VcsAgentEvalSha256 $CorpusPath
    }
}

function Read-VcsAgentEvalJson {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $Label
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label file is missing"
    }

    try {
        return Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json -Depth 100
    }
    catch {
        throw "$Label is not valid JSON: $($_.Exception.Message)"
    }
}

function Write-VcsAgentEvalJson {
    param(
        [Parameter(Mandatory)] [object] $Value,
        [Parameter(Mandatory)] [string] $Path
    )

    $parent = Split-Path -Parent $Path
    if (-not [string]::IsNullOrEmpty($parent)) {
        [void][System.IO.Directory]::CreateDirectory($parent)
    }

    $json = $Value | ConvertTo-Json -Depth 100
    $json = [regex]::Replace(
        $json,
        '(?m)^(?:  )+',
        { param($match) ([string][char]9) * ($match.Value.Length / 2) }
    )
    $crlf = [string][char]13 + [char]10
    $lf = [string][char]10
    $json = $json.Replace($crlf, $lf) + $lf
    [System.IO.File]::WriteAllText($Path, $json, [System.Text.UTF8Encoding]::new($false))
}

function Assert-VcsAgentEvalSchemaMatch {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $SchemaPath,
        [Parameter(Mandatory)] [string] $Label
    )

    try {
        $valid = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 |
            Test-Json -SchemaFile $SchemaPath -ErrorAction Stop
        if (-not $valid) {
            throw 'schema validator returned false'
        }
    }
    catch {
        throw "$Label does not conform to the v1 schema: $($_.Exception.Message)"
    }
}

function Assert-VcsAgentEvalObject {
    param(
        [AllowNull()] [object] $Value,
        [Parameter(Mandatory)] [string] $Context
    )

    if ($null -eq $Value -or $Value -isnot [pscustomobject]) {
        throw "$Context must be an object"
    }
}

function Assert-VcsAgentEvalArray {
    param(
        [AllowNull()] [object] $Value,
        [Parameter(Mandatory)] [string] $Context,
        [switch] $AllowEmpty
    )

    if ($null -eq $Value -or $Value -isnot [System.Array]) {
        throw "$Context must be an array"
    }
    if (-not $AllowEmpty -and $Value.Count -eq 0) {
        throw "$Context must not be empty"
    }
}

function Assert-VcsAgentEvalExactProperties {
    param(
        [Parameter(Mandatory)] [pscustomobject] $Value,
        [Parameter(Mandatory)] [string[]] $Required,
        [string[]] $Optional = @(),
        [Parameter(Mandatory)] [string] $Context
    )

    $names = @($Value.PSObject.Properties.Name)
    $allowed = @($Required) + @($Optional)
    foreach ($name in $Required) {
        if ($names -cnotcontains $name) {
            throw "$Context is missing required property '$name'"
        }
    }
    foreach ($name in $names) {
        if ($allowed -cnotcontains $name) {
            throw "$Context contains unknown property '$name' (format drift)"
        }
    }
}

function Assert-VcsAgentEvalString {
    param(
        [AllowNull()] [object] $Value,
        [Parameter(Mandatory)] [string] $Context,
        [switch] $AllowEmpty
    )

    if ($Value -isnot [string] -or (-not $AllowEmpty -and [string]::IsNullOrWhiteSpace($Value))) {
        throw "$Context must be a non-empty string"
    }
}

function Assert-VcsAgentEvalBoolean {
    param(
        [AllowNull()] [object] $Value,
        [Parameter(Mandatory)] [string] $Context
    )

    if ($Value -isnot [bool]) {
        throw "$Context must be a boolean"
    }
}

function Assert-VcsAgentEvalBooleanOrNull {
    param(
        [AllowNull()] [object] $Value,
        [Parameter(Mandatory)] [string] $Context
    )

    if ($null -ne $Value -and $Value -isnot [bool]) {
        throw "$Context must be a boolean or null"
    }
}

function Assert-VcsAgentEvalInteger {
    param(
        [AllowNull()] [object] $Value,
        [Parameter(Mandatory)] [string] $Context,
        [int64] $Minimum = 0
    )

    if (($Value -isnot [int]) -and ($Value -isnot [int64])) {
        throw "$Context must be an integer"
    }
    if ([int64]$Value -lt $Minimum) {
        throw "$Context must be at least $Minimum"
    }
}

function Assert-VcsAgentEvalNumber {
    param(
        [AllowNull()] [object] $Value,
        [Parameter(Mandatory)] [string] $Context,
        [double] $Minimum = 0,
        [double] $Maximum = [double]::PositiveInfinity
    )

    if (($Value -isnot [int]) -and ($Value -isnot [int64]) -and ($Value -isnot [double]) -and ($Value -isnot [decimal])) {
        throw "$Context must be a number"
    }
    $number = [double]$Value
    if ($number -lt $Minimum -or $number -gt $Maximum) {
        throw "$Context must be between $Minimum and $Maximum"
    }
}

function Assert-VcsAgentEvalEnum {
    param(
        [AllowNull()] [object] $Value,
        [Parameter(Mandatory)] [string[]] $Allowed,
        [Parameter(Mandatory)] [string] $Context,
        [switch] $AllowNull
    )

    if ($null -eq $Value) {
        if ($AllowNull) {
            return
        }
        throw "$Context must not be null"
    }
    if ($Value -isnot [string] -or $Allowed -cnotcontains $Value) {
        throw "$Context has unsupported value '$Value'"
    }
}

function Assert-VcsAgentEvalSchemaDocument {
    param([Parameter(Mandatory)] [pscustomobject] $Document)

    Assert-VcsAgentEvalObject $Document 'schema'
    Assert-VcsAgentEvalExactProperties $Document @('$schema', '$id', 'title', 'oneOf', '$defs') @() 'schema'
    if ($Document.'$schema' -cne 'http://json-schema.org/draft-07/schema#') {
        throw "schema schema URI has unsupported value '$($Document.'$schema')'"
    }
    if ($Document.'$id' -cne $script:SchemaId) {
        throw "schema id has unsupported value '$($Document.'$id')'"
    }
    Assert-VcsAgentEvalObject $Document.'$defs' 'schema definitions'
    Assert-VcsAgentEvalExactProperties $Document.'$defs' @(
        'evidenceExpectation', 'observedEvidence', 'provenance', 'scenario', 'corpus',
        'observationRun', 'observations', 'rateMetric', 'resultRun', 'metrics', 'results'
    ) @() 'schema definitions'
}

function Assert-VcsAgentEvalProvenance {
    param(
        [Parameter(Mandatory)] [pscustomobject] $Provenance,
        [Parameter(Mandatory)] [string] $Context
    )

    Assert-VcsAgentEvalObject $Provenance $Context
    Assert-VcsAgentEvalExactProperties $Provenance @(
        'evaluationKind', 'recordingSource', 'measuredFields', 'supplementalFixtureFields',
        'evaluator', 'skillName', 'skillContractVersion',
        'skillSha256', 'contractSha256', 'corpusSha256'
    ) @() $Context
    Assert-VcsAgentEvalEnum $Provenance.evaluationKind @('independent-model-forward-routing') "$Context.evaluationKind"
    Assert-VcsAgentEvalEnum $Provenance.recordingSource @('orchestra-blinded-forward-evaluator/v1') "$Context.recordingSource"
    Assert-VcsAgentEvalArray $Provenance.measuredFields "$Context.measuredFields"
    Assert-VcsAgentEvalArray $Provenance.supplementalFixtureFields "$Context.supplementalFixtureFields"
    $expectedMeasuredFields = @('shouldActivate', 'selectedInterface', 'fallbackReason')
    $expectedSupplementalFields = @('commandValid', 'callCount', 'outcome', 'evidence')
    if ((Get-VcsAgentEvalCanonicalJson $Provenance.measuredFields) -cne (Get-VcsAgentEvalCanonicalJson $expectedMeasuredFields)) {
        throw "$Context.measuredFields must identify exactly the model-forward routing fields"
    }
    if ((Get-VcsAgentEvalCanonicalJson $Provenance.supplementalFixtureFields) -cne (Get-VcsAgentEvalCanonicalJson $expectedSupplementalFields)) {
        throw "$Context.supplementalFixtureFields must identify exactly the non-model fixture fields"
    }
    Assert-VcsAgentEvalObject $Provenance.evaluator "$Context.evaluator"
    Assert-VcsAgentEvalExactProperties $Provenance.evaluator @(
        'identity', 'attempt', 'startedAt', 'completedAt', 'isolation', 'inputScope',
        'prohibitedInputClasses', 'expectedOrBaselineAccess'
    ) @() "$Context.evaluator"
    Assert-VcsAgentEvalEnum $Provenance.evaluator.identity @('codex-independent-forward-eval/2') "$Context.evaluator.identity"
    Assert-VcsAgentEvalInteger $Provenance.evaluator.attempt "$Context.evaluator.attempt" 2
    if ($Provenance.evaluator.attempt -ne 2) {
        throw "$Context.evaluator.attempt must be 2"
    }
    try {
        $startedAt = [datetimeoffset]$Provenance.evaluator.startedAt
        $completedAt = [datetimeoffset]$Provenance.evaluator.completedAt
    }
    catch {
        throw "$Context evaluator timestamps must be valid date-time values"
    }
    if ($completedAt -le $startedAt) {
        throw "$Context.evaluator.completedAt must be later than startedAt"
    }
    Assert-VcsAgentEvalEnum $Provenance.evaluator.isolation @('fork-turns-none') "$Context.evaluator.isolation"
    Assert-VcsAgentEvalEnum $Provenance.evaluator.inputScope @('skill-and-routed-references-only') "$Context.evaluator.inputScope"
    Assert-VcsAgentEvalArray $Provenance.evaluator.prohibitedInputClasses "$Context.evaluator.prohibitedInputClasses"
    $expectedProhibitedInputs = @('evals', 'tests', 'review', 'history', 'expected', 'baseline', 'results')
    if ((Get-VcsAgentEvalCanonicalJson $Provenance.evaluator.prohibitedInputClasses) -cne (Get-VcsAgentEvalCanonicalJson $expectedProhibitedInputs)) {
        throw "$Context.evaluator.prohibitedInputClasses must enumerate the blinded input boundary"
    }
    Assert-VcsAgentEvalBoolean $Provenance.evaluator.expectedOrBaselineAccess "$Context.evaluator.expectedOrBaselineAccess"
    if ($Provenance.evaluator.expectedOrBaselineAccess) {
        throw "$Context.evaluator.expectedOrBaselineAccess must be false"
    }
    Assert-VcsAgentEvalString $Provenance.skillName "$Context.skillName"
    Assert-VcsAgentEvalString $Provenance.skillContractVersion "$Context.skillContractVersion"

    foreach ($name in @('skillSha256', 'contractSha256', 'corpusSha256')) {
        Assert-VcsAgentEvalString $Provenance.$name "$Context.$name"
        if ($Provenance.$name -cnotmatch '^[0-9a-f]{64}$') {
            throw "$Context.$name must be a lowercase SHA-256 digest"
        }
    }
}

function Assert-VcsAgentEvalCurrentProvenance {
    param(
        [Parameter(Mandatory)] [pscustomobject] $Provenance,
        [Parameter(Mandatory)] [string] $SkillPath,
        [Parameter(Mandatory)] [string] $SkillContractPath,
        [Parameter(Mandatory)] [string] $CorpusPath,
        [Parameter(Mandatory)] [string] $Context
    )

    $current = Get-VcsAgentEvalCurrentIdentity $SkillPath $SkillContractPath $CorpusPath

    foreach ($name in @('skillName', 'skillContractVersion', 'skillSha256', 'contractSha256', 'corpusSha256')) {
        if ($Provenance.$name -cne $current.$name) {
            throw "$Context provenance is stale for the current Skill, contract, or corpus"
        }
    }
}

function Assert-VcsAgentEvalEvidenceExpectation {
    param(
        [Parameter(Mandatory)] [pscustomobject] $Evidence,
        [Parameter(Mandatory)] [string] $Context
    )

    Assert-VcsAgentEvalObject $Evidence $Context
    Assert-VcsAgentEvalExactProperties $Evidence $script:EvidenceNames @() $Context
    foreach ($name in $script:EvidenceNames) {
        Assert-VcsAgentEvalEnum $Evidence.$name @('required', 'not-applicable') "$Context.$name"
    }
}

function Assert-VcsAgentEvalObservedEvidence {
    param(
        [Parameter(Mandatory)] [pscustomobject] $Evidence,
        [Parameter(Mandatory)] [string] $Context
    )

    Assert-VcsAgentEvalObject $Evidence $Context
    Assert-VcsAgentEvalExactProperties $Evidence $script:EvidenceNames @() $Context
    foreach ($name in $script:EvidenceNames) {
        Assert-VcsAgentEvalBooleanOrNull $Evidence.$name "$Context.$name"
    }
}

function Assert-VcsAgentEvalCorpusDocument {
    param([Parameter(Mandatory)] [pscustomobject] $Document)

    Assert-VcsAgentEvalObject $Document 'corpus'
    Assert-VcsAgentEvalExactProperties $Document @('schemaVersion', 'corpusVersion', 'scenarios') @() 'corpus'
    if ($Document.schemaVersion -cne $script:CorpusSchemaVersion) {
        throw "corpus.schemaVersion has unsupported value '$($Document.schemaVersion)'"
    }
    Assert-VcsAgentEvalString $Document.corpusVersion 'corpus.corpusVersion'
    if ($Document.corpusVersion -notmatch '^[0-9]{4}-[0-9]{2}-[0-9]{2}$') {
        throw 'corpus.corpusVersion must use YYYY-MM-DD'
    }
    Assert-VcsAgentEvalArray $Document.scenarios 'corpus.scenarios'

    $ids = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    for ($index = 0; $index -lt $Document.scenarios.Count; $index++) {
        $scenario = $Document.scenarios[$index]
        $context = "corpus.scenarios[$index]"
        Assert-VcsAgentEvalObject $scenario $context
        Assert-VcsAgentEvalExactProperties $scenario @(
            'id', 'category', 'prompt', 'backend', 'forge', 'platform',
            'mutation', 'unrelatedDirtyState', 'expected'
        ) @() $context
        Assert-VcsAgentEvalString $scenario.id "$context.id"
        if ($scenario.id -cnotmatch '^[a-z0-9]+(?:-[a-z0-9]+)*$') {
            throw "$context.id must be lowercase kebab-case"
        }
        if (-not $ids.Add($scenario.id)) {
            throw "corpus contains duplicate scenario id '$($scenario.id)'"
        }
        Assert-VcsAgentEvalEnum $scenario.category @('direct', 'indirect', 'negative', 'unsupported', 'mutation') "$context.category"
        Assert-VcsAgentEvalString $scenario.prompt "$context.prompt"
        Assert-VcsAgentEvalEnum $scenario.backend @('git', 'jujutsu', 'none') "$context.backend"
        Assert-VcsAgentEvalEnum $scenario.forge @('github', 'gitlab', 'gitea', 'none') "$context.forge"
        Assert-VcsAgentEvalEnum $scenario.platform @('windows', 'linux', 'macos', 'any') "$context.platform"
        Assert-VcsAgentEvalBoolean $scenario.mutation "$context.mutation"
        Assert-VcsAgentEvalBoolean $scenario.unrelatedDirtyState "$context.unrelatedDirtyState"
        if ($scenario.unrelatedDirtyState -and -not $scenario.mutation) {
            throw "$context cannot require unrelated dirty state for a non-mutating scenario"
        }

        $expected = $scenario.expected
        Assert-VcsAgentEvalObject $expected "$context.expected"
        Assert-VcsAgentEvalExactProperties $expected @(
            'selectedInterface', 'shouldActivate', 'fallbackReason', 'commandValid',
            'outcome', 'maxCalls', 'evidence'
        ) @() "$context.expected"
        Assert-VcsAgentEvalEnum $expected.selectedInterface @('vcs-agent', 'raw-cli', 'none') "$context.expected.selectedInterface"
        Assert-VcsAgentEvalBoolean $expected.shouldActivate "$context.expected.shouldActivate"
        Assert-VcsAgentEvalEnum $expected.fallbackReason @('unsupported', 'missing-executable', 'diagnostic-output-required') "$context.expected.fallbackReason" -AllowNull
        Assert-VcsAgentEvalBoolean $expected.commandValid "$context.expected.commandValid"
        Assert-VcsAgentEvalEnum $expected.outcome @('success', 'unsupported', 'denied', 'invalid-input', 'not-applicable') "$context.expected.outcome"
        Assert-VcsAgentEvalInteger $expected.maxCalls "$context.expected.maxCalls"
        Assert-VcsAgentEvalEvidenceExpectation $expected.evidence "$context.expected.evidence"

        if ($scenario.category -ceq 'negative' -and ($expected.shouldActivate -or $expected.selectedInterface -cne 'none')) {
            throw "$context negative scenarios must not activate an interface"
        }
        if ($expected.selectedInterface -ceq 'raw-cli' -and $null -eq $expected.fallbackReason) {
            throw "$context raw-cli selection requires a fallback reason"
        }
        if ($expected.selectedInterface -cne 'raw-cli' -and $null -ne $expected.fallbackReason) {
            throw "$context fallback reason is only valid for raw-cli selection"
        }
    }
}

function Assert-VcsAgentEvalObservationRun {
    param(
        [Parameter(Mandatory)] [pscustomobject] $Run,
        [Parameter(Mandatory)] [string] $Context
    )

    Assert-VcsAgentEvalObject $Run $Context
    Assert-VcsAgentEvalExactProperties $Run @(
        'scenarioId', 'shouldActivate', 'selectedInterface', 'fallbackReason', 'commandValid',
        'callCount', 'outcome', 'evidence'
    ) @() $Context
    Assert-VcsAgentEvalString $Run.scenarioId "$Context.scenarioId"
    Assert-VcsAgentEvalBoolean $Run.shouldActivate "$Context.shouldActivate"
    Assert-VcsAgentEvalEnum $Run.selectedInterface @('vcs-agent', 'raw-cli', 'none') "$Context.selectedInterface"
    Assert-VcsAgentEvalEnum $Run.fallbackReason @('unsupported', 'missing-executable', 'diagnostic-output-required') "$Context.fallbackReason" -AllowNull
    Assert-VcsAgentEvalBoolean $Run.commandValid "$Context.commandValid"
    Assert-VcsAgentEvalInteger $Run.callCount "$Context.callCount"
    Assert-VcsAgentEvalEnum $Run.outcome @('success', 'unsupported', 'denied', 'invalid-input', 'not-applicable') "$Context.outcome"
    Assert-VcsAgentEvalObservedEvidence $Run.evidence "$Context.evidence"
    if ($Run.selectedInterface -ceq 'raw-cli' -and $null -eq $Run.fallbackReason) {
        throw "$Context raw-cli selection requires a fallback reason"
    }
    if ($Run.selectedInterface -cne 'raw-cli' -and $null -ne $Run.fallbackReason) {
        throw "$Context fallback reason is only valid for raw-cli selection"
    }
}

function Assert-VcsAgentEvalObservationsDocument {
    param([Parameter(Mandatory)] [pscustomobject] $Document)

    Assert-VcsAgentEvalObject $Document 'observations'
    Assert-VcsAgentEvalExactProperties $Document @('schemaVersion', 'corpusVersion', 'provenance', 'runs') @() 'observations'
    if ($Document.schemaVersion -cne $script:ObservationSchemaVersion) {
        throw "observations.schemaVersion has unsupported value '$($Document.schemaVersion)'"
    }
    Assert-VcsAgentEvalString $Document.corpusVersion 'observations.corpusVersion'
    Assert-VcsAgentEvalProvenance $Document.provenance 'observations.provenance'
    Assert-VcsAgentEvalArray $Document.runs 'observations.runs'

    $ids = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    for ($index = 0; $index -lt $Document.runs.Count; $index++) {
        $run = $Document.runs[$index]
        Assert-VcsAgentEvalObservationRun $run "observations.runs[$index]"
        if (-not $ids.Add($run.scenarioId)) {
            throw "observations contains duplicate scenario id '$($run.scenarioId)'"
        }
    }
}

function Get-VcsAgentEvalMismatchCode {
    param([Parameter(Mandatory)] [string] $EvidenceName)

    switch ($EvidenceName) {
        'unrelatedChangesPreserved' { return 'unrelated-changes-preserved' }
        'exactRevisionPublished' { return 'exact-revision-published' }
        'terminalCiForExactRevision' { return 'terminal-ci-for-exact-revision' }
        'unsafeMutationDenied' { return 'unsafe-mutation-denied' }
        default { throw "unknown evidence metric '$EvidenceName'" }
    }
}

function Get-VcsAgentEvalMismatches {
    param(
        [Parameter(Mandatory)] [pscustomobject] $Scenario,
        [Parameter(Mandatory)] [pscustomobject] $Run
    )

    $mismatches = [System.Collections.Generic.List[string]]::new()
    if ($Run.shouldActivate -ne $Scenario.expected.shouldActivate) {
        $mismatches.Add('activation')
    }
    if ($Run.selectedInterface -cne $Scenario.expected.selectedInterface) {
        $mismatches.Add('selected-interface')
    }
    if ($Run.fallbackReason -cne $Scenario.expected.fallbackReason) {
        $mismatches.Add('fallback-reason')
    }
    if ($Run.commandValid -ne $Scenario.expected.commandValid) {
        $mismatches.Add('command-validity')
    }
    if ($Run.callCount -gt $Scenario.expected.maxCalls) {
        $mismatches.Add('call-count')
    }
    if ($Run.outcome -cne $Scenario.expected.outcome) {
        $mismatches.Add('outcome')
    }
    foreach ($name in $script:EvidenceNames) {
        $expectation = $Scenario.expected.evidence.$name
        $actual = $Run.evidence.$name
        $matches = if ($expectation -ceq 'required') { $actual -eq $true } else { $null -eq $actual }
        if (-not $matches) {
            $mismatches.Add((Get-VcsAgentEvalMismatchCode $name))
        }
    }
    return @($mismatches)
}

function New-VcsAgentEvalRateMetric {
    param(
        [Parameter(Mandatory)] [int] $Count,
        [Parameter(Mandatory)] [int] $Total
    )

    $rate = if ($Total -eq 0) { 0.0 } else { [math]::Round($Count / $Total, 6) }
    return [pscustomobject][ordered]@{
        count = $Count
        total = $Total
        rate = $rate
    }
}

function Get-VcsAgentEvalMetrics {
    param(
        [Parameter(Mandatory)] [System.Array] $Scenarios,
        [Parameter(Mandatory)] [System.Array] $Runs
    )

    $runsById = @{}
    foreach ($run in $Runs) {
        $runsById[$run.scenarioId] = $run
    }

    $preferred = @($Scenarios | Where-Object { $_.expected.selectedInterface -ceq 'vcs-agent' })
    $negative = @($Scenarios | Where-Object { -not $_.expected.shouldActivate })
    $activated = @($Scenarios | Where-Object { $_.expected.shouldActivate })
    $preservation = @($Scenarios | Where-Object { $_.expected.evidence.unrelatedChangesPreserved -ceq 'required' })
    $publication = @($Scenarios | Where-Object { $_.expected.evidence.exactRevisionPublished -ceq 'required' })
    $terminalCi = @($Scenarios | Where-Object { $_.expected.evidence.terminalCiForExactRevision -ceq 'required' })
    $denials = @($Scenarios | Where-Object { $_.expected.evidence.unsafeMutationDenied -ceq 'required' })

    $expectationMatches = @($Runs | Where-Object { $_.expectationMatched }).Count
    $preferredMatches = @($preferred | Where-Object { $runsById[$_.id].selectedInterface -ceq 'vcs-agent' }).Count
    $falseActivations = @($negative | Where-Object { $runsById[$_.id].shouldActivate }).Count
    $rawFallbacks = @($activated | Where-Object { $runsById[$_.id].selectedInterface -ceq 'raw-cli' }).Count
    $invalidCommands = @($Runs | Where-Object { -not $_.commandValid }).Count
    $preserved = @($preservation | Where-Object { $runsById[$_.id].evidence.unrelatedChangesPreserved -eq $true }).Count
    $published = @($publication | Where-Object { $runsById[$_.id].evidence.exactRevisionPublished -eq $true }).Count
    $terminal = @($terminalCi | Where-Object { $runsById[$_.id].evidence.terminalCiForExactRevision -eq $true }).Count
    $denied = @($denials | Where-Object { $runsById[$_.id].evidence.unsafeMutationDenied -eq $true }).Count
    $totalCalls = ($Runs | Measure-Object -Property callCount -Sum).Sum

    return [pscustomobject][ordered]@{
        expectationMatch = New-VcsAgentEvalRateMetric $expectationMatches $Runs.Count
        preferredInterfaceSelection = New-VcsAgentEvalRateMetric $preferredMatches $preferred.Count
        falseActivation = New-VcsAgentEvalRateMetric $falseActivations $negative.Count
        rawCliFallback = New-VcsAgentEvalRateMetric $rawFallbacks $activated.Count
        invalidCommand = New-VcsAgentEvalRateMetric $invalidCommands $Runs.Count
        unrelatedChangesPreservation = New-VcsAgentEvalRateMetric $preserved $preservation.Count
        exactRevisionPublication = New-VcsAgentEvalRateMetric $published $publication.Count
        terminalCiForExactRevision = New-VcsAgentEvalRateMetric $terminal $terminalCi.Count
        unsafeMutationDenial = New-VcsAgentEvalRateMetric $denied $denials.Count
        callCount = [pscustomobject][ordered]@{
            total = [int]$totalCalls
            scenarios = $Runs.Count
            average = [math]::Round($totalCalls / $Runs.Count, 6)
        }
    }
}

function New-VcsAgentEvalResultDocument {
    param(
        [Parameter(Mandatory)] [pscustomobject] $Corpus,
        [Parameter(Mandatory)] [pscustomobject] $Observations
    )

    if ($Observations.corpusVersion -cne $Corpus.corpusVersion) {
        throw "observations corpusVersion '$($Observations.corpusVersion)' does not match corpus '$($Corpus.corpusVersion)'"
    }
    if ($Observations.runs.Count -ne $Corpus.scenarios.Count) {
        throw "observations run count $($Observations.runs.Count) does not match corpus scenario count $($Corpus.scenarios.Count)"
    }

    $observationsById = @{}
    foreach ($run in $Observations.runs) {
        $observationsById[$run.scenarioId] = $run
    }
    $scenarioIds = @($Corpus.scenarios.id)
    foreach ($run in $Observations.runs) {
        if ($scenarioIds -cnotcontains $run.scenarioId) {
            throw "observations contains unknown scenario '$($run.scenarioId)'"
        }
    }

    $resultRuns = [System.Collections.Generic.List[object]]::new()
    foreach ($scenario in $Corpus.scenarios) {
        if (-not $observationsById.ContainsKey($scenario.id)) {
            throw "observations is missing scenario '$($scenario.id)'"
        }
        $observed = $observationsById[$scenario.id]
        $mismatches = @(Get-VcsAgentEvalMismatches $scenario $observed)
        $resultRuns.Add([pscustomobject][ordered]@{
            scenarioId = $observed.scenarioId
            shouldActivate = $observed.shouldActivate
            selectedInterface = $observed.selectedInterface
            fallbackReason = $observed.fallbackReason
            commandValid = $observed.commandValid
            callCount = $observed.callCount
            outcome = $observed.outcome
            evidence = [pscustomobject][ordered]@{
                unrelatedChangesPreserved = $observed.evidence.unrelatedChangesPreserved
                exactRevisionPublished = $observed.evidence.exactRevisionPublished
                terminalCiForExactRevision = $observed.evidence.terminalCiForExactRevision
                unsafeMutationDenied = $observed.evidence.unsafeMutationDenied
            }
            expectationMatched = $mismatches.Count -eq 0
            mismatches = $mismatches
        })
    }

    $runs = @($resultRuns)
    return [pscustomobject][ordered]@{
        schemaVersion = $script:ResultSchemaVersion
        corpusVersion = $Corpus.corpusVersion
        recorderVersion = $script:RecorderVersion
        provenance = $Observations.provenance
        runs = $runs
        metrics = Get-VcsAgentEvalMetrics $Corpus.scenarios $runs
    }
}

function Assert-VcsAgentEvalRateMetric {
    param(
        [Parameter(Mandatory)] [pscustomobject] $Metric,
        [Parameter(Mandatory)] [string] $Context
    )

    Assert-VcsAgentEvalObject $Metric $Context
    Assert-VcsAgentEvalExactProperties $Metric @('count', 'total', 'rate') @() $Context
    Assert-VcsAgentEvalInteger $Metric.count "$Context.count"
    Assert-VcsAgentEvalInteger $Metric.total "$Context.total"
    Assert-VcsAgentEvalNumber $Metric.rate "$Context.rate" 0 1
    if ($Metric.count -gt $Metric.total) {
        throw "$Context.count cannot exceed total"
    }
}

function Assert-VcsAgentEvalResultsDocument {
    param([Parameter(Mandatory)] [pscustomobject] $Document)

    Assert-VcsAgentEvalObject $Document 'results'
    Assert-VcsAgentEvalExactProperties $Document @('schemaVersion', 'corpusVersion', 'recorderVersion', 'provenance', 'runs', 'metrics') @() 'results'
    if ($Document.schemaVersion -cne $script:ResultSchemaVersion) {
        throw "results.schemaVersion has unsupported value '$($Document.schemaVersion)'"
    }
    if ($Document.recorderVersion -cne $script:RecorderVersion) {
        throw "results.recorderVersion has unsupported value '$($Document.recorderVersion)'"
    }
    Assert-VcsAgentEvalString $Document.corpusVersion 'results.corpusVersion'
    Assert-VcsAgentEvalProvenance $Document.provenance 'results.provenance'
    Assert-VcsAgentEvalArray $Document.runs 'results.runs'

    $ids = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    for ($index = 0; $index -lt $Document.runs.Count; $index++) {
        $run = $Document.runs[$index]
        $context = "results.runs[$index]"
        Assert-VcsAgentEvalObject $run $context
        Assert-VcsAgentEvalExactProperties $run @(
            'scenarioId', 'shouldActivate', 'selectedInterface', 'fallbackReason', 'commandValid', 'callCount',
            'outcome', 'evidence', 'expectationMatched', 'mismatches'
        ) @() $context
        $observationShape = [pscustomobject]@{
            scenarioId = $run.scenarioId
            shouldActivate = $run.shouldActivate
            selectedInterface = $run.selectedInterface
            fallbackReason = $run.fallbackReason
            commandValid = $run.commandValid
            callCount = $run.callCount
            outcome = $run.outcome
            evidence = $run.evidence
        }
        Assert-VcsAgentEvalObservationRun $observationShape $context
        Assert-VcsAgentEvalBoolean $run.expectationMatched "$context.expectationMatched"
        Assert-VcsAgentEvalArray $run.mismatches "$context.mismatches" -AllowEmpty
        $seenMismatches = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
        foreach ($mismatch in $run.mismatches) {
            Assert-VcsAgentEvalEnum $mismatch $script:MismatchCodes "$context.mismatches"
            if (-not $seenMismatches.Add($mismatch)) {
                throw "$context.mismatches contains duplicate '$mismatch'"
            }
        }
        if (-not $ids.Add($run.scenarioId)) {
            throw "results contains duplicate scenario id '$($run.scenarioId)'"
        }
    }

    $metrics = $Document.metrics
    Assert-VcsAgentEvalObject $metrics 'results.metrics'
    $rateNames = @(
        'expectationMatch', 'preferredInterfaceSelection', 'falseActivation',
        'rawCliFallback', 'invalidCommand', 'unrelatedChangesPreservation',
        'exactRevisionPublication', 'terminalCiForExactRevision', 'unsafeMutationDenial'
    )
    Assert-VcsAgentEvalExactProperties $metrics ($rateNames + @('callCount')) @() 'results.metrics'
    foreach ($name in $rateNames) {
        Assert-VcsAgentEvalRateMetric $metrics.$name "results.metrics.$name"
    }
    Assert-VcsAgentEvalObject $metrics.callCount 'results.metrics.callCount'
    Assert-VcsAgentEvalExactProperties $metrics.callCount @('total', 'scenarios', 'average') @() 'results.metrics.callCount'
    Assert-VcsAgentEvalInteger $metrics.callCount.total 'results.metrics.callCount.total'
    Assert-VcsAgentEvalInteger $metrics.callCount.scenarios 'results.metrics.callCount.scenarios' 1
    Assert-VcsAgentEvalNumber $metrics.callCount.average 'results.metrics.callCount.average'
}

function Get-VcsAgentEvalCanonicalJson {
    param([Parameter(Mandatory)] [object] $Value)

    return ($Value | ConvertTo-Json -Depth 100 -Compress)
}

function Test-VcsAgentEvalResultDocument {
    param(
        [Parameter(Mandatory)] [pscustomobject] $Corpus,
        [Parameter(Mandatory)] [pscustomobject] $Results
    )

    if ($Results.corpusVersion -cne $Corpus.corpusVersion) {
        throw "results corpusVersion '$($Results.corpusVersion)' does not match corpus '$($Corpus.corpusVersion)'"
    }
    if ($Results.runs.Count -ne $Corpus.scenarios.Count) {
        throw "results run count $($Results.runs.Count) does not match corpus scenario count $($Corpus.scenarios.Count)"
    }

    for ($index = 0; $index -lt $Corpus.scenarios.Count; $index++) {
        $scenario = $Corpus.scenarios[$index]
        $run = $Results.runs[$index]
        if ($run.scenarioId -cne $scenario.id) {
            throw "results run order drift at index $($index): expected '$($scenario.id)', got '$($run.scenarioId)'"
        }
        $actualMismatches = @(Get-VcsAgentEvalMismatches $scenario $run)
        $storedMismatches = @($run.mismatches)
        if ((Get-VcsAgentEvalCanonicalJson $actualMismatches) -cne (Get-VcsAgentEvalCanonicalJson $storedMismatches)) {
            throw "results mismatch details drift for scenario '$($scenario.id)'"
        }
        if ($run.expectationMatched -ne ($actualMismatches.Count -eq 0)) {
            throw "results expectationMatched drift for scenario '$($scenario.id)'"
        }
        if ($actualMismatches.Count -gt 0) {
            throw "expectation mismatch in scenario '$($scenario.id)': $($actualMismatches -join ',')"
        }
    }

    $expectedMetrics = Get-VcsAgentEvalMetrics $Corpus.scenarios $Results.runs
    if ((Get-VcsAgentEvalCanonicalJson $expectedMetrics) -cne (Get-VcsAgentEvalCanonicalJson $Results.metrics)) {
        throw 'results metrics drift from recomputed values'
    }
}
