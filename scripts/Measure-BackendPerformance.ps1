[CmdletBinding()]
param(
    [ValidateRange(5, 101)]
    [int] $Rounds = 5,

    [ValidateRange(1, 1000000000)]
    [int] $Iterations = 1000000,

    [string] $Configuration = 'Release',

    [ValidateRange(1, 101)]
    [int] $ColdSamples = 9,

    [string] $RuntimeIdentifier = '',

    [switch] $NoBuild
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $repositoryRoot `
    'benchmarks/Lunil.Runtime.Benchmarks/Lunil.Runtime.Benchmarks.csproj'
$stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss', [Globalization.CultureInfo]::InvariantCulture)
if ([string]::IsNullOrWhiteSpace($RuntimeIdentifier)) {
    $effectiveRid = [System.Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier
    if ([string]::IsNullOrWhiteSpace($effectiveRid)) {
        $os = if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
                [System.Runtime.InteropServices.OSPlatform]::Windows)) {
            'win'
        }
        elseif ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
                [System.Runtime.InteropServices.OSPlatform]::Linux)) {
            'linux'
        }
        elseif ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
                [System.Runtime.InteropServices.OSPlatform]::OSX)) {
            'osx'
        }
        else {
            throw 'Unable to derive the runtime identifier operating system.'
        }
        $architecture = switch (
            [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()) {
            'X64' { 'x64' }
            'Arm64' { 'arm64' }
            'X86' { 'x86' }
            'Arm' { 'arm' }
            default { throw "Unsupported runtime architecture: $_" }
        }
        $effectiveRid = "$os-$architecture"
    }
}
else {
    $effectiveRid = $RuntimeIdentifier.Trim().ToLowerInvariant()
}
$effectiveRid = $effectiveRid.Trim().ToLowerInvariant()
$outputDirectory = Join-Path $repositoryRoot "artifacts/backend-performance/$effectiveRid/$stamp"
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

function Get-Median([double[]] $Values) {
    if ($Values.Count -eq 0) { return 0.0 }
    $ordered = @($Values | Sort-Object)
    $middle = [Math]::Floor($ordered.Count / 2)
    if (($ordered.Count % 2) -eq 0) {
        return ($ordered[$middle - 1] + $ordered[$middle]) / 2.0
    }

    return $ordered[$middle]
}

function Get-BootstrapMedianInterval([double[]] $Values, [int] $Samples = 10000) {
    if ($Values.Count -eq 0) {
        return [pscustomobject]@{ Lower = 0.0; Upper = 0.0 }
    }

    $random = [Random]::new(1729)
    $medians = [double[]]::new($Samples)
    $resample = [double[]]::new($Values.Count)
    for ($sample = 0; $sample -lt $Samples; $sample++) {
        for ($index = 0; $index -lt $Values.Count; $index++) {
            $resample[$index] = $Values[$random.Next($Values.Count)]
        }

        $medians[$sample] = Get-Median $resample
    }

    [Array]::Sort($medians)
    return [pscustomobject]@{
        Lower = $medians[[Math]::Floor(0.025 * ($Samples - 1))]
        Upper = $medians[[Math]::Ceiling(0.975 * ($Samples - 1))]
    }
}

function ConvertTo-BackendRecord([string] $Line, [int] $Round) {
    $values = @{}
    foreach ($part in $Line.Substring('backend_evidence '.Length).Split(',')) {
        $pair = $part.Trim().Split('=', 2)
        if ($pair.Count -ne 2) { throw "Malformed backend evidence field: $part" }
        $values[$pair[0]] = $pair[1]
    }

    return [pscustomobject]@{
        Round = $Round
        Workload = $values.workload
        Name = $values.name
        Rid = $effectiveRid
        Operations = [int]$values.operations
        StartupMedianMs = [double]::Parse($values.startup_median_ms, [Globalization.CultureInfo]::InvariantCulture)
        StartupP95Ms = [double]::Parse($values.startup_p95_ms, [Globalization.CultureInfo]::InvariantCulture)
        WarmNsOp = [double]::Parse($values.warm_ns_op, [Globalization.CultureInfo]::InvariantCulture)
        AllocatedOp = [double]::Parse($values.allocated_op, [Globalization.CultureInfo]::InvariantCulture)
        AllocationSlopeBytesIteration = [double]::Parse(
            $values.allocation_slope_bytes_iteration,
            [Globalization.CultureInfo]::InvariantCulture)
        CompilationP95Ms = [double]::Parse($values.compilation_p95_ms, [Globalization.CultureInfo]::InvariantCulture)
        Tier1P95Ms = [double]::Parse($values.tier1_p95_ms, [Globalization.CultureInfo]::InvariantCulture)
        Tier2P95Ms = [double]::Parse($values.tier2_p95_ms, [Globalization.CultureInfo]::InvariantCulture)
        LoopOsrP95Ms = [double]::Parse($values.loop_osr_p95_ms, [Globalization.CultureInfo]::InvariantCulture)
        CanonicalVerifyP95Ms = [double]::Parse($values.canonical_verify_p95_ms, [Globalization.CultureInfo]::InvariantCulture)
        CfgLivenessP95Ms = [double]::Parse($values.cfg_liveness_p95_ms, [Globalization.CultureInfo]::InvariantCulture)
        MethodPlanP95Ms = [double]::Parse($values.method_plan_p95_ms, [Globalization.CultureInfo]::InvariantCulture)
        PlanVerifyP95Ms = [double]::Parse($values.plan_verify_p95_ms, [Globalization.CultureInfo]::InvariantCulture)
        ReflectionEmitP95Ms = [double]::Parse($values.reflection_emit_p95_ms, [Globalization.CultureInfo]::InvariantCulture)
        DelegateCreateP95Ms = [double]::Parse($values.delegate_create_p95_ms, [Globalization.CultureInfo]::InvariantCulture)
        CompileAllocatedP95Bytes = [double]::Parse($values.compile_allocated_p95_bytes, [Globalization.CultureInfo]::InvariantCulture)
        Tier2IrVerifyP95Ms = [double]::Parse($values.tier2_ir_verify_p95_ms, [Globalization.CultureInfo]::InvariantCulture)
        Tier2LivenessP95Ms = [double]::Parse($values.tier2_liveness_p95_ms, [Globalization.CultureInfo]::InvariantCulture)
        Tier2LivenessCacheHitRate = [double]::Parse($values.tier2_liveness_cache_hit_rate, [Globalization.CultureInfo]::InvariantCulture)
        Tier2OptimizationPlanP95Ms = [double]::Parse($values.tier2_optimization_plan_p95_ms, [Globalization.CultureInfo]::InvariantCulture)
        Tier2CilEmitP95Ms = [double]::Parse($values.tier2_cil_emit_p95_ms, [Globalization.CultureInfo]::InvariantCulture)
        Tier2DelegateCreateP95Ms = [double]::Parse($values.tier2_delegate_create_p95_ms, [Globalization.CultureInfo]::InvariantCulture)
        Tier2CompileAllocatedP95Bytes = [double]::Parse($values.tier2_compile_allocated_p95_bytes, [Globalization.CultureInfo]::InvariantCulture)
        LoopOsrIrVerifyP95Ms = [double]::Parse($values.loop_osr_ir_verify_p95_ms, [Globalization.CultureInfo]::InvariantCulture)
        LoopOsrAnalysisP95Ms = [double]::Parse($values.loop_osr_analysis_p95_ms, [Globalization.CultureInfo]::InvariantCulture)
        LoopOsrLivenessCacheHitRate = [double]::Parse($values.loop_osr_liveness_cache_hit_rate, [Globalization.CultureInfo]::InvariantCulture)
        LoopOsrSpecializationPlanP95Ms = [double]::Parse($values.loop_osr_specialization_plan_p95_ms, [Globalization.CultureInfo]::InvariantCulture)
        LoopOsrCilEmitP95Ms = [double]::Parse($values.loop_osr_cil_emit_p95_ms, [Globalization.CultureInfo]::InvariantCulture)
        LoopOsrDelegateCreateP95Ms = [double]::Parse($values.loop_osr_delegate_create_p95_ms, [Globalization.CultureInfo]::InvariantCulture)
        LoopOsrCompileAllocatedP95Bytes = [double]::Parse($values.loop_osr_compile_allocated_p95_bytes, [Globalization.CultureInfo]::InvariantCulture)
        Tier2CodeKind = $values.tier2_code_kind
        Tier2OptimizationCount = [int]$values.tier2_optimization_count
        Tier2SpecializedOptimizationCount = [int]$values.tier2_specialized_optimization_count
        Tier2DeoptSiteCount = [int]$values.tier2_deopt_site_count
        Tier2ManagedCompilationCount = [int]$values.tier2_managed_compilation_count
        Tier2CompilationQueued = [long]$values.tier2_compilation_queued
        Tier2EligibilityEvaluated = [long]$values.tier2_eligibility_evaluated
        Tier2EligibilityAccepted = [long]$values.tier2_eligibility_accepted
        Tier2EligibilityRejected = [long]$values.tier2_eligibility_rejected
        LoopOsrCodeKind = $values.loop_osr_code_kind
        LoopOsrSpecializedInstructionCount = [int]$values.loop_osr_specialized_instruction_count
        LoopOsrGuardCount = [int]$values.loop_osr_guard_count
        LoopOsrManagedCompilationCount = [int]$values.loop_osr_managed_compilation_count
        LoopOsrEligibilityEvaluated = [long]$values.loop_osr_eligibility_evaluated
        LoopOsrEligibilityAccepted = [long]$values.loop_osr_eligibility_accepted
        LoopOsrEligibilityRejected = [long]$values.loop_osr_eligibility_rejected
        CompiledInvocations = [long]$values.compiled_invocations
        CompiledInstructions = [long]$values.compiled_instructions
        SchedulerExits = [long]$values.scheduler_exits
        InstructionsPerSchedulerExit = [double]::Parse($values.instructions_per_scheduler_exit, [Globalization.CultureInfo]::InvariantCulture)
        PlanDirectInstructions = [long]$values.plan_direct_instructions
        PlanSlowPathInstructions = [long]$values.plan_slow_path_instructions
        PlanInstructions = [long]$values.plan_instructions
        RssPeakDeltaBytes = [long]$values.rss_peak_delta_bytes
        EstimatedCodeBytes = [long]$values.estimated_code_bytes
    }
}

function ConvertTo-EligibilityRecord([string] $Line, [int] $Round) {
    $values = @{}
    foreach ($part in $Line.Substring('backend_eligibility '.Length).Split(',')) {
        $pair = $part.Trim().Split('=', 2)
        if ($pair.Count -ne 2) { throw "Malformed eligibility field: $part" }
        $values[$pair[0]] = $pair[1]
    }

    return [pscustomobject]@{
        Round = $Round
        Rid = $effectiveRid
        Workload = $values.workload
        AutoEligible = [bool]::Parse($values.auto_eligible)
        Compilable = [bool]::Parse($values.compilable)
        Reason = $values.reason
        BreakEven = $values.break_even
        DirectCoverage = [double]::Parse(
            $values.direct_coverage,
            [Globalization.CultureInfo]::InvariantCulture)
        SlowPathDensity = [double]::Parse(
            $values.slow_path_density,
            [Globalization.CultureInfo]::InvariantCulture)
        SchedulerBoundaryDensity = [double]::Parse(
            $values.scheduler_boundary_density,
            [Globalization.CultureInfo]::InvariantCulture)
        EstimatedCodeBytes = [long]$values.estimated_code_bytes
    }
}

$records = [Collections.Generic.List[object]]::new()
$eligibilityRecords = [Collections.Generic.List[object]]::new()
Push-Location $repositoryRoot
try {
    for ($round = 1; $round -le $Rounds; $round++) {
        Write-Host "Backend performance evidence round $round/$Rounds"
        $arguments = @(
            'run', '--project', $project,
            '--configuration', $Configuration
        )
        if (-not [string]::IsNullOrWhiteSpace($RuntimeIdentifier)) {
            $arguments += @('--runtime', $effectiveRid)
        }
        if ($NoBuild) {
            $arguments += '--no-build'
        }
        $arguments += @(
            '--', '--backend-only', "--cold-samples=$ColdSamples", $Iterations
        )
        $lines = & dotnet @arguments
        if ($LASTEXITCODE -ne 0) {
            throw "Backend performance runner failed in round $round."
        }

        $runFile = Join-Path $outputDirectory ("round-{0:D3}.txt" -f $round)
        $lines | Set-Content -LiteralPath $runFile -Encoding utf8
        foreach ($line in $lines | Where-Object { $_ -like 'backend_evidence *' }) {
            $records.Add((ConvertTo-BackendRecord $line $round))
        }
        foreach ($line in $lines | Where-Object { $_ -like 'backend_eligibility *' }) {
            $eligibilityRecords.Add((ConvertTo-EligibilityRecord $line $round))
        }
    }
}
finally {
    Pop-Location
}

$expectedRecords = $Rounds * 6 * 5
if ($records.Count -ne $expectedRecords) {
    throw "Expected $expectedRecords backend evidence records, found $($records.Count)."
}
$expectedEligibilityRecords = $Rounds * 6
if ($eligibilityRecords.Count -ne $expectedEligibilityRecords) {
    throw "Expected $expectedEligibilityRecords eligibility records, found $($eligibilityRecords.Count)."
}

$records | Export-Csv -LiteralPath (Join-Path $outputDirectory 'runs.csv') `
    -NoTypeInformation -Encoding utf8
$eligibilityRecords | Export-Csv -LiteralPath (Join-Path $outputDirectory 'eligibility.csv') `
    -NoTypeInformation -Encoding utf8
$summary = foreach ($group in $records | Group-Object Workload, Name | Sort-Object Name) {
    $first = $group.Group[0]
    $speedups = [Collections.Generic.List[double]]::new()
    $allocationRatios = [Collections.Generic.List[double]]::new()
    foreach ($record in $group.Group) {
        $interpreter = $records | Where-Object {
            $_.Round -eq $record.Round -and
            $_.Workload -eq $record.Workload -and
            $_.Name -eq 'interpreter'
        } | Select-Object -First 1
        if ($null -eq $interpreter) {
            throw "Missing interpreter pair for round $($record.Round), workload $($record.Workload)."
        }

        $speedups.Add($interpreter.WarmNsOp / $record.WarmNsOp)
        $allocationRatios.Add($(if ($interpreter.AllocatedOp -eq 0) {
            0.0
        }
        else {
            $record.AllocatedOp / $interpreter.AllocatedOp
        }))
    }

    $speedupInterval = Get-BootstrapMedianInterval $speedups.ToArray()
    $eligibility = $eligibilityRecords | Where-Object {
        $_.Workload -eq $first.Workload
    } | Select-Object -First 1
    [pscustomobject]@{
        Rid = $effectiveRid
        Workload = $first.Workload
        Name = $first.Name
        Rounds = $group.Count
        StartupMedianMs = Get-Median @($group.Group.StartupMedianMs)
        StartupP95Ms = Get-Median @($group.Group.StartupP95Ms)
        WarmNsOp = Get-Median @($group.Group.WarmNsOp)
        SpeedupVsInterpreterMedian = Get-Median $speedups.ToArray()
        SpeedupVsInterpreterCi95Lower = $speedupInterval.Lower
        SpeedupVsInterpreterCi95Upper = $speedupInterval.Upper
        AllocatedOp = Get-Median @($group.Group.AllocatedOp)
        AllocationRatioVsInterpreterMedian = Get-Median $allocationRatios.ToArray()
        AllocationSlopeBytesIteration = Get-Median @($group.Group.AllocationSlopeBytesIteration)
        CompilationP95Ms = Get-Median @($group.Group.CompilationP95Ms)
        Tier1P95Ms = Get-Median @($group.Group.Tier1P95Ms)
        Tier2P95Ms = Get-Median @($group.Group.Tier2P95Ms)
        LoopOsrP95Ms = Get-Median @($group.Group.LoopOsrP95Ms)
        PlanVerifyP95Ms = Get-Median @($group.Group.PlanVerifyP95Ms)
        ReflectionEmitP95Ms = Get-Median @($group.Group.ReflectionEmitP95Ms)
        CompileAllocatedP95Bytes = Get-Median @($group.Group.CompileAllocatedP95Bytes)
        Tier2IrVerifyP95Ms = Get-Median @($group.Group.Tier2IrVerifyP95Ms)
        Tier2LivenessP95Ms = Get-Median @($group.Group.Tier2LivenessP95Ms)
        Tier2LivenessCacheHitRate = Get-Median @($group.Group.Tier2LivenessCacheHitRate)
        Tier2OptimizationPlanP95Ms = Get-Median @($group.Group.Tier2OptimizationPlanP95Ms)
        Tier2CilEmitP95Ms = Get-Median @($group.Group.Tier2CilEmitP95Ms)
        Tier2DelegateCreateP95Ms = Get-Median @($group.Group.Tier2DelegateCreateP95Ms)
        Tier2CompileAllocatedP95Bytes = Get-Median @($group.Group.Tier2CompileAllocatedP95Bytes)
        LoopOsrIrVerifyP95Ms = Get-Median @($group.Group.LoopOsrIrVerifyP95Ms)
        LoopOsrAnalysisP95Ms = Get-Median @($group.Group.LoopOsrAnalysisP95Ms)
        LoopOsrLivenessCacheHitRate = Get-Median @($group.Group.LoopOsrLivenessCacheHitRate)
        LoopOsrSpecializationPlanP95Ms = Get-Median @(
            $group.Group.LoopOsrSpecializationPlanP95Ms)
        LoopOsrCilEmitP95Ms = Get-Median @($group.Group.LoopOsrCilEmitP95Ms)
        LoopOsrDelegateCreateP95Ms = Get-Median @($group.Group.LoopOsrDelegateCreateP95Ms)
        LoopOsrCompileAllocatedP95Bytes = Get-Median @(
            $group.Group.LoopOsrCompileAllocatedP95Bytes)
        InstructionsPerSchedulerExit = Get-Median @($group.Group.InstructionsPerSchedulerExit)
        RssPeakDeltaBytes = Get-Median @($group.Group.RssPeakDeltaBytes)
        EstimatedCodeBytes = Get-Median @($group.Group.EstimatedCodeBytes)
        Tier2CodeKind = @($group.Group.Tier2CodeKind | Sort-Object -Unique) -join '+'
        Tier2OptimizationCount = Get-Median @($group.Group.Tier2OptimizationCount)
        Tier2SpecializedOptimizationCount = Get-Median @(
            $group.Group.Tier2SpecializedOptimizationCount)
        Tier2DeoptSiteCount = Get-Median @($group.Group.Tier2DeoptSiteCount)
        Tier2ManagedCompilationCount = Get-Median @($group.Group.Tier2ManagedCompilationCount)
        Tier2CompilationQueued = Get-Median @($group.Group.Tier2CompilationQueued)
        Tier2EligibilityEvaluated = Get-Median @($group.Group.Tier2EligibilityEvaluated)
        Tier2EligibilityAccepted = Get-Median @($group.Group.Tier2EligibilityAccepted)
        Tier2EligibilityRejected = Get-Median @($group.Group.Tier2EligibilityRejected)
        LoopOsrCodeKind = @($group.Group.LoopOsrCodeKind | Sort-Object -Unique) -join '+'
        LoopOsrSpecializedInstructionCount = Get-Median @(
            $group.Group.LoopOsrSpecializedInstructionCount)
        LoopOsrGuardCount = Get-Median @($group.Group.LoopOsrGuardCount)
        LoopOsrManagedCompilationCount = Get-Median @(
            $group.Group.LoopOsrManagedCompilationCount)
        LoopOsrEligibilityEvaluated = Get-Median @(
            $group.Group.LoopOsrEligibilityEvaluated)
        LoopOsrEligibilityAccepted = Get-Median @(
            $group.Group.LoopOsrEligibilityAccepted)
        LoopOsrEligibilityRejected = Get-Median @(
            $group.Group.LoopOsrEligibilityRejected)
        AutoEligible = $eligibility.AutoEligible
        EligibilityReason = $eligibility.Reason
        BreakEven = $eligibility.BreakEven
    }
}
$summary | ConvertTo-Json | Set-Content `
    -LiteralPath (Join-Path $outputDirectory 'summary.json') -Encoding utf8
$tier1Arithmetic = $summary | Where-Object {
    $_.Workload -eq 'arithmetic' -and $_.Name -eq 'tier1'
} | Select-Object -First 1
$tier2Arithmetic = $summary | Where-Object {
    $_.Workload -eq 'arithmetic' -and $_.Name -eq 'tier2'
} | Select-Object -First 1
$loopOsrArithmetic = $summary | Where-Object {
    $_.Workload -eq 'arithmetic' -and $_.Name -eq 'loop_osr'
} | Select-Object -First 1
$negativeGateFailures = [Collections.Generic.List[string]]::new()
$tier2NegativeGateFailures = [Collections.Generic.List[string]]::new()
$tier2NegativeComparisons = [Collections.Generic.List[object]]::new()
foreach ($workload in @('lua_calls', 'table_access', 'metamethod', 'coroutine_error_hook')) {
    $eligibility = $eligibilityRecords | Where-Object { $_.Workload -eq $workload } |
        Select-Object -First 1
    $tier1 = $summary | Where-Object {
        $_.Workload -eq $workload -and $_.Name -eq 'tier1'
    } | Select-Object -First 1
    if ($eligibility.AutoEligible -and $tier1.SpeedupVsInterpreterCi95Lower -lt 0.95) {
        $negativeGateFailures.Add(
            "$workload is Auto-eligible with speedup CI lower $($tier1.SpeedupVsInterpreterCi95Lower).")
    }

    $tier2 = $summary | Where-Object {
        $_.Workload -eq $workload -and $_.Name -eq 'tier2'
    } | Select-Object -First 1
    if ($tier2.Tier2ManagedCompilationCount -ne 0) {
        $tier2NegativeGateFailures.Add(
            "$workload installed $($tier2.Tier2ManagedCompilationCount) managed Tier 2 methods.")
    }

    $rolloutRatios = [Collections.Generic.List[double]]::new()
    foreach ($record in $records | Where-Object {
        $_.Workload -eq $workload -and $_.Name -eq 'tier2'
    }) {
        $tier1Record = $records | Where-Object {
            $_.Round -eq $record.Round -and
            $_.Workload -eq $workload -and
            $_.Name -eq 'tier1'
        } | Select-Object -First 1
        if ($null -eq $tier1Record) {
            throw "Missing Tier 1 rollout pair for round $($record.Round), workload $workload."
        }

        $rolloutRatios.Add($tier1Record.WarmNsOp / $record.WarmNsOp)
    }

    $rolloutInterval = Get-BootstrapMedianInterval $rolloutRatios.ToArray()
    $tier2NegativeComparisons.Add([pscustomobject]@{
        Workload = $workload
        SpeedupVsTier1Median = Get-Median $rolloutRatios.ToArray()
        SpeedupVsTier1Ci95Lower = $rolloutInterval.Lower
        SpeedupVsTier1Ci95Upper = $rolloutInterval.Upper
    })
    $rolloutMedian = Get-Median $rolloutRatios.ToArray()
    if ($rolloutMedian -lt 0.90) {
        $tier2NegativeGateFailures.Add(
            "$workload Tier 2-default/Tier 1 median speedup is $rolloutMedian.")
    }
}

$decision = [pscustomobject]@{
    Rid = $effectiveRid
    Rounds = $Rounds
    ColdSamplesPerProcess = $ColdSamples
    ArithmeticSpeedupMedian = $tier1Arithmetic.SpeedupVsInterpreterMedian
    ArithmeticSpeedupCi95Lower = $tier1Arithmetic.SpeedupVsInterpreterCi95Lower
    ArithmeticSpeedupCi95Upper = $tier1Arithmetic.SpeedupVsInterpreterCi95Upper
    ArithmeticAllocationSlopeBytesIteration = $tier1Arithmetic.AllocationSlopeBytesIteration
    Tier1CompilationP95Ms = $tier1Arithmetic.CompilationP95Ms
    NegativeWorkloadGateFailures = $negativeGateFailures.ToArray()
    QualifiesThisRid =
        $tier1Arithmetic.SpeedupVsInterpreterMedian -ge 2.0 -and
        [Math]::Abs($tier1Arithmetic.AllocationSlopeBytesIteration) -le 0.01 -and
        $tier1Arithmetic.CompilationP95Ms -lt 5.0 -and
        $negativeGateFailures.Count -eq 0
}
$decision | ConvertTo-Json | Set-Content `
    -LiteralPath (Join-Path $outputDirectory 'tier1-decision.json') -Encoding utf8
$tier2Decision = [pscustomobject]@{
    Rid = $effectiveRid
    Rounds = $Rounds
    ColdSamplesPerProcess = $ColdSamples
    ArithmeticSpeedupMedian = $tier2Arithmetic.SpeedupVsInterpreterMedian
    ArithmeticSpeedupCi95Lower = $tier2Arithmetic.SpeedupVsInterpreterCi95Lower
    ArithmeticSpeedupCi95Upper = $tier2Arithmetic.SpeedupVsInterpreterCi95Upper
    ArithmeticAllocationSlopeBytesIteration = $tier2Arithmetic.AllocationSlopeBytesIteration
    Tier2CompilationP95Ms = $tier2Arithmetic.Tier2P95Ms
    Tier2CodeKind = $tier2Arithmetic.Tier2CodeKind
    Tier2OptimizationCount = $tier2Arithmetic.Tier2OptimizationCount
    Tier2SpecializedOptimizationCount = $tier2Arithmetic.Tier2SpecializedOptimizationCount
    Tier2DeoptSiteCount = $tier2Arithmetic.Tier2DeoptSiteCount
    Tier2IrVerifyP95Ms = $tier2Arithmetic.Tier2IrVerifyP95Ms
    Tier2LivenessP95Ms = $tier2Arithmetic.Tier2LivenessP95Ms
    Tier2LivenessCacheHitRate = $tier2Arithmetic.Tier2LivenessCacheHitRate
    Tier2OptimizationPlanP95Ms = $tier2Arithmetic.Tier2OptimizationPlanP95Ms
    Tier2CilEmitP95Ms = $tier2Arithmetic.Tier2CilEmitP95Ms
    Tier2DelegateCreateP95Ms = $tier2Arithmetic.Tier2DelegateCreateP95Ms
    Tier2CompileAllocatedP95Bytes = $tier2Arithmetic.Tier2CompileAllocatedP95Bytes
    Tier2EligibilityEvaluated = $tier2Arithmetic.Tier2EligibilityEvaluated
    Tier2EligibilityAccepted = $tier2Arithmetic.Tier2EligibilityAccepted
    Tier2EligibilityRejected = $tier2Arithmetic.Tier2EligibilityRejected
    Tier2ManagedCompilationCount = $tier2Arithmetic.Tier2ManagedCompilationCount
    NegativeWorkloadComparisons = $tier2NegativeComparisons.ToArray()
    NegativeWorkloadGateFailures = $tier2NegativeGateFailures.ToArray()
    QualifiesThisRid =
        $tier2Arithmetic.SpeedupVsInterpreterMedian -ge 4.0 -and
        $tier2Arithmetic.SpeedupVsInterpreterCi95Lower -ge 4.0 -and
        [Math]::Abs($tier2Arithmetic.AllocationSlopeBytesIteration) -le 0.01 -and
        $tier2Arithmetic.Tier2P95Ms -lt 10.0 -and
        $tier2Arithmetic.Tier2CodeKind -eq 'ExactNumericSpecializedCil' -and
        $tier2Arithmetic.Tier2SpecializedOptimizationCount -gt 0 -and
        $tier2Arithmetic.Tier2ManagedCompilationCount -eq 0 -and
        $tier2Arithmetic.Tier2EligibilityAccepted -gt 0 -and
        $tier2NegativeGateFailures.Count -eq 0
}
$tier2Decision | ConvertTo-Json | Set-Content `
    -LiteralPath (Join-Path $outputDirectory 'tier2-decision.json') -Encoding utf8
$loopOsrNegativeGateFailures = [Collections.Generic.List[string]]::new()
$loopOsrWorkloadComparisons = foreach ($workload in @(
    'lua_calls', 'table_access', 'metamethod', 'coroutine_error_hook')) {
    $record = $summary | Where-Object {
        $_.Workload -eq $workload -and $_.Name -eq 'loop_osr'
    } | Select-Object -First 1
    $ratios = [Collections.Generic.List[double]]::new()
    foreach ($run in $records | Where-Object {
        $_.Workload -eq $workload -and $_.Name -eq 'loop_osr'
    }) {
        $baseline = $records | Where-Object {
            $_.Round -eq $run.Round -and
            $_.Workload -eq $workload -and
            $_.Name -eq 'loop_osr_off'
        } | Select-Object -First 1
        if ($null -eq $baseline) {
            throw "Missing Loop OSR-off pair for round $($run.Round), workload $workload."
        }

        $ratios.Add($baseline.WarmNsOp / $run.WarmNsOp)
    }

    $interval = Get-BootstrapMedianInterval $ratios.ToArray()
    $median = Get-Median $ratios.ToArray()
    if ($median -lt 0.90) {
        $loopOsrNegativeGateFailures.Add(
            "$workload Loop OSR-on/off median speedup is $median.")
    }

    [pscustomobject]@{
        Workload = $workload
        SpeedupVsDisabledMedian = $median
        SpeedupVsDisabledCi95Lower = $interval.Lower
        SpeedupVsDisabledCi95Upper = $interval.Upper
        CodeKind = $record.LoopOsrCodeKind
        ManagedCompilationCount = $record.LoopOsrManagedCompilationCount
    }
}
$loopOsrArithmeticRatios = [Collections.Generic.List[double]]::new()
foreach ($run in $records | Where-Object {
    $_.Workload -eq 'arithmetic' -and $_.Name -eq 'loop_osr'
}) {
    $baseline = $records | Where-Object {
        $_.Round -eq $run.Round -and
        $_.Workload -eq 'arithmetic' -and
        $_.Name -eq 'loop_osr_off'
    } | Select-Object -First 1
    if ($null -eq $baseline) {
        throw "Missing arithmetic Loop OSR-off pair for round $($run.Round)."
    }

    $loopOsrArithmeticRatios.Add($baseline.WarmNsOp / $run.WarmNsOp)
}
$loopOsrArithmeticInterval = Get-BootstrapMedianInterval $loopOsrArithmeticRatios.ToArray()
$loopOsrDecision = [pscustomobject]@{
    Rid = $effectiveRid
    Rounds = $Rounds
    ColdSamplesPerProcess = $ColdSamples
    ArithmeticSpeedupMedian = $loopOsrArithmetic.SpeedupVsInterpreterMedian
    ArithmeticSpeedupCi95Lower = $loopOsrArithmetic.SpeedupVsInterpreterCi95Lower
    ArithmeticSpeedupCi95Upper = $loopOsrArithmetic.SpeedupVsInterpreterCi95Upper
    ArithmeticSpeedupVsDisabledMedian = Get-Median $loopOsrArithmeticRatios.ToArray()
    ArithmeticSpeedupVsDisabledCi95Lower = $loopOsrArithmeticInterval.Lower
    ArithmeticSpeedupVsDisabledCi95Upper = $loopOsrArithmeticInterval.Upper
    ArithmeticAllocationSlopeBytesIteration =
        $loopOsrArithmetic.AllocationSlopeBytesIteration
    LoopOsrCompilationP95Ms = $loopOsrArithmetic.LoopOsrP95Ms
    LoopOsrCodeKind = $loopOsrArithmetic.LoopOsrCodeKind
    LoopOsrSpecializedInstructionCount =
        $loopOsrArithmetic.LoopOsrSpecializedInstructionCount
    LoopOsrGuardCount = $loopOsrArithmetic.LoopOsrGuardCount
    LoopOsrManagedCompilationCount = $loopOsrArithmetic.LoopOsrManagedCompilationCount
    LoopOsrEligibilityEvaluated = $loopOsrArithmetic.LoopOsrEligibilityEvaluated
    LoopOsrEligibilityAccepted = $loopOsrArithmetic.LoopOsrEligibilityAccepted
    LoopOsrEligibilityRejected = $loopOsrArithmetic.LoopOsrEligibilityRejected
    LoopOsrIrVerifyP95Ms = $loopOsrArithmetic.LoopOsrIrVerifyP95Ms
    LoopOsrAnalysisP95Ms = $loopOsrArithmetic.LoopOsrAnalysisP95Ms
    LoopOsrLivenessCacheHitRate = $loopOsrArithmetic.LoopOsrLivenessCacheHitRate
    LoopOsrSpecializationPlanP95Ms =
        $loopOsrArithmetic.LoopOsrSpecializationPlanP95Ms
    LoopOsrCilEmitP95Ms = $loopOsrArithmetic.LoopOsrCilEmitP95Ms
    LoopOsrDelegateCreateP95Ms = $loopOsrArithmetic.LoopOsrDelegateCreateP95Ms
    LoopOsrCompileAllocatedP95Bytes =
        $loopOsrArithmetic.LoopOsrCompileAllocatedP95Bytes
    NegativeWorkloadComparisons = @($loopOsrWorkloadComparisons)
    NegativeWorkloadGateFailures = $loopOsrNegativeGateFailures.ToArray()
    QualifiesThisRid =
        (Get-Median $loopOsrArithmeticRatios.ToArray()) -ge 2.0 -and
        $loopOsrArithmeticInterval.Lower -ge 1.5 -and
        [Math]::Abs($loopOsrArithmetic.AllocationSlopeBytesIteration) -le 0.01 -and
        $loopOsrArithmetic.LoopOsrP95Ms -lt 10.0 -and
        $loopOsrArithmetic.LoopOsrCodeKind -eq 'GuardedExactNumericCil' -and
        $loopOsrArithmetic.LoopOsrSpecializedInstructionCount -gt 0 -and
        $loopOsrArithmetic.LoopOsrManagedCompilationCount -eq 0 -and
        $loopOsrArithmetic.LoopOsrEligibilityAccepted -gt 0 -and
        $loopOsrArithmetic.LoopOsrLivenessCacheHitRate -eq 1.0 -and
        $loopOsrArithmetic.LoopOsrCompileAllocatedP95Bytes -lt 65536 -and
        $loopOsrNegativeGateFailures.Count -eq 0
}
$loopOsrDecision | ConvertTo-Json -Depth 8 | Set-Content `
    -LiteralPath (Join-Path $outputDirectory 'loop-osr-decision.json') -Encoding utf8
$summary | Format-Table -AutoSize
$decision | Format-List
$tier2Decision | Format-List
$loopOsrDecision | Format-List
Write-Host "Backend performance evidence written to $outputDirectory"
