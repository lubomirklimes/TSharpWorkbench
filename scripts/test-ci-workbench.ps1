. (Join-Path $PSScriptRoot 'ci-common.ps1')
. (Join-Path $PSScriptRoot 'tvdemo-fixture.ps1')
$checks = 0
function Check-Throws([scriptblock]$Body) {
    $threw = $false
    try { & $Body } catch { $threw = $true }
    if (!$threw) { throw 'Expected fail-closed rejection did not occur.' }
    $script:checks++
}
foreach ($file in Get-ChildItem $PSScriptRoot -Filter '*.ps1') {
    $tokens = $null; $errors = $null
    [System.Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$errors) | Out-Null
    if ($errors.Count) { throw ($errors | Out-String) }
    $checks++
}
Assert-AppVersion '1.2.3' 'v1.2.3'; $checks++
Assert-AppVersion '1.2.3-rc.1' 'v1.2.3-rc.1'; $checks++
Check-Throws { Assert-AppVersion '01.2.3' }
Check-Throws { Assert-AppVersion '1.2.3-01' }
Check-Throws { Assert-AppVersion '1.2.3' 'v1.2.4' }
Assert-ReleasePolicy 'workflow_dispatch' 'refs/heads/main' 'abc' 'abc' $true; $checks++
Assert-ReleasePolicy 'push' 'refs/tags/v1.2.3' 'abc' 'abc' $true; $checks++
Check-Throws { Assert-ReleasePolicy 'workflow_dispatch' 'refs/heads/master' 'abc' 'abc' $true }
Check-Throws { Assert-ReleasePolicy 'workflow_dispatch' 'refs/tags/v1.2.3' 'abc' 'abc' $true }
Check-Throws { Assert-ReleasePolicy 'push' 'refs/tags/v1.2.3' 'abc' 'abc' $false }
Check-Throws { Assert-ReleasePolicy 'push' 'refs/tags/v1.2.3' 'abc' 'def' $true }
Check-Throws { Assert-ReleasePolicy 'push' 'refs/heads/main' 'abc' 'abc' $true }
$testRoot = Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/ci-review/script-tests'
New-Item -ItemType Directory -Force $testRoot | Out-Null
$trx = Join-Path $testRoot 'gate.trx'
'<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><ResultSummary><Counters total="2" executed="2" passed="2" /></ResultSummary></TestRun>' | Set-Content $trx
Assert-NoSkippedTests $trx; $checks++
'<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><ResultSummary><Counters total="2" executed="1" passed="1" /></ResultSummary></TestRun>' | Set-Content $trx
Check-Throws { Assert-NoSkippedTests $trx }
Check-Throws { Assert-PackageFiles $testRoot (Get-NativeRid) }
Assert-AppVersion '1.2.0-preview.1' 'v1.2.0-preview.1'; $checks++
if ((Get-PrereleaseFlag '1.2.0-preview.1') -cne 'true' -or (Get-PrereleaseFlag '1.2.0') -cne 'false') { throw 'Preview/stable release flag mismatch.' }; $checks++
$sha = 'a' * 40; $peeled = 'b' * 40
$tags = @("$sha`trefs/tags/v1.9.0", "$sha`trefs/tags/v1.10.0", "$peeled`trefs/tags/v1.10.0^{}",
    "$sha`trefs/tags/v1.11.0-preview.1", "$sha`trefs/tags/v2.0.0", "$sha`trefs/tags/v1.010.0")
$latest = Select-StableFrameworkTag $tags 1
if ($latest.tag -cne 'v1.10.0' -or $latest.commit -cne $peeled) { throw 'Numeric stable-tag/peeled-commit selection failed.' }; $checks++
Check-Throws { Select-StableFrameworkTag @("$sha`trefs/tags/v2.0.0", "$sha`trefs/tags/v1.2.0-rc.1") 1 }
$selection = [pscustomobject]@{ repository = 'https://github.com/lubomirklimes/TSharpVision.git'; tag = 'v1.2.0'; commit = $sha;
    discoveredUtc = '2026-01-01T00:00:00Z'; appVersion = '1.2.0-preview.1'; policy = 'latest-stable-matching-major' }
Assert-FixtureSelection $selection '1.2.0-preview.1'; $checks++
Check-Throws { Assert-FixtureSelection $selection '2.0.0' }
$batchFile = Join-Path $testRoot 'tvdemo-batch.json'
@{ results = @() } | ConvertTo-Json | Set-Content $batchFile
Check-Throws { Assert-TvDemoExecution $batchFile }
@{ results = @(@{ scenarioId = 'release/tvdemo-about'; outcome = 'Passed'; checkpoints = @() }) } |
    ConvertTo-Json -Depth 6 | Set-Content $batchFile
Check-Throws { Assert-TvDemoExecution $batchFile }
@{ runRoot = 'test'; results = @(@{ scenarioId = 'release/tvdemo-about'; outcome = 'Passed'; reason = 'executed';
    checkpoints = @(@{ assertions = @(@{ id = 'about-title'; kind = 'screen-contains'; outcome = 'PASS' },
        @{ id = 'about-content'; kind = 'screen-contains'; outcome = 'FAIL' }) }) }) } |
    ConvertTo-Json -Depth 8 | Set-Content $batchFile
Check-Throws { Assert-TvDemoExecution $batchFile }
$short = '/tmp/tsw-0123456789ab'
Assert-CiUnixTempDirectory $short; $checks++
$socketBytes = Get-DiagnosticSocketBytes $short
if ($socketBytes -ne 84 -or (Get-DiagnosticSocketBytes ('/private' + $short)) -gt 96) { throw 'Unexpected diagnostic endpoint budget.' }; $checks++
Assert-DiagnosticSocketBudget ('/' + ('x' * 32)); $checks++
Check-Throws { Assert-DiagnosticSocketBudget ('/' + ('x' * 33)) }
Check-Throws { Assert-DiagnosticSocketBudget ('/' + ('é' * 17)) }
Check-Throws { Assert-CiUnixTempDirectory '/tmp' }
Check-Throws { Assert-CiUnixTempDirectory '/tmp/tsw-../../outside' }
Check-Throws { Assert-CiUnixTempDirectory '/tmp/tsw-short' }
Check-Throws { Assert-DiagnosticSocketBudget '/var/folders/20/jp1_0n3n7kndh6rnbqb5344m0000gn/T' }
$unixEnvironment = Get-CiTempEnvironment $short $true
foreach ($key in @('TMPDIR','TMP','TEMP')) { if ($unixEnvironment[$key] -cne $short) { throw 'Unix temp environment mismatch.' }; $checks++ }
$windowsEnvironment = Get-CiTempEnvironment $testRoot $false
if ($windowsEnvironment.ContainsKey('TMPDIR') -or $windowsEnvironment.TEMP -cne $testRoot -or $windowsEnvironment.TMP -cne $testRoot) { throw 'Windows temp behavior changed.' }; $checks++
$originalEnvironment = @{}
foreach ($key in @('TMPDIR','TMP','TEMP')) { $originalEnvironment[$key] = [Environment]::GetEnvironmentVariable($key, 'Process') }
$scopePath = Join-Path $testRoot 'scope-temp'; New-Item -ItemType Directory -Force $scopePath | Out-Null
$scope = Enter-CiTempScope $scopePath
try {
    $start = [Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    foreach ($argument in @('-NoProfile','-NonInteractive','-Command', '[Console]::Out.Write((@{TMPDIR=$env:TMPDIR; TMP=$env:TMP; TEMP=$env:TEMP} | ConvertTo-Json -Compress))')) { $start.ArgumentList.Add($argument) }
    $child = [Diagnostics.Process]::Start($start)
    try {
        if (!$child.WaitForExit(15000)) { $child.Kill($true); throw 'Child temp inheritance probe timed out.' }
        $inherited = $child.StandardOutput.ReadToEnd() | ConvertFrom-Json
        if ($child.ExitCode -ne 0) { throw 'Child temp inheritance probe failed.' }
        foreach ($key in @('TMPDIR','TMP','TEMP')) {
            if ($inherited.$key -cne [Environment]::GetEnvironmentVariable($key, 'Process')) { throw "Child did not inherit $key" }
            $checks++
        }
    } finally { $child.Dispose() }
} finally { Exit-CiTempScope $scope }
foreach ($key in $originalEnvironment.Keys) {
    if ([Environment]::GetEnvironmentVariable($key, 'Process') -cne $originalEnvironment[$key]) { throw "Temp scope did not restore $key" }
    $checks++
}
if ($scope.CreatedUnixDirectory -and (Test-Path -LiteralPath $scope.Directory)) { throw 'Owned Unix temp root was not cleaned up.' }; $checks++
Check-Throws {
    $failedScope = Enter-CiTempScope $scopePath
    try { throw 'Simulated phase failure' } finally { Exit-CiTempScope $failedScope }
}
foreach ($key in $originalEnvironment.Keys) {
    if ([Environment]::GetEnvironmentVariable($key, 'Process') -cne $originalEnvironment[$key]) { throw "Failed phase did not restore $key" }
    $checks++
}
@{ results = @(@{ scenarioId = 'sample/dialog/basic'; outcome = 'Passed'; checkpoints = @() }) } |
    ConvertTo-Json -Depth 6 | Set-Content $batchFile
Check-Throws { Assert-SampleScenarioSmoke $batchFile }
@{ runRoot = 'test'; results = @(@{ scenarioId = 'sample/dialog/basic'; outcome = 'Passed'; checkpoints = @(@{ assertions = @(@{ outcome = 'PASS' }) }) }) } |
    ConvertTo-Json -Depth 8 | Set-Content $batchFile
$sampleEvidence = Assert-SampleScenarioSmoke $batchFile
if ($sampleEvidence.checkpoints -ne 1 -or $sampleEvidence.assertions -ne 1) { throw 'Scenario evidence counts were not checked.' }; $checks++
Write-Output "$checks script checks passed."
