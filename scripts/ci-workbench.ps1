[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Version','Source','Restore','Build','Test','FixtureResolve','Fixture','ReleaseGate','Package','Smoke')][string]$Phase,
    [ValidateSet('win-x64','linux-x64','osx-x64','osx-arm64')][string]$Rid,
    [string]$OutputRoot = 'artifacts/github',
    [string]$Tag = '',
    [string]$FixtureSelectionFile = ''
)
. (Join-Path $PSScriptRoot 'ci-common.ps1')
. (Join-Path $PSScriptRoot 'tvdemo-fixture.ps1')
$repo = Split-Path $PSScriptRoot -Parent
Set-Location -LiteralPath $repo
if (!$Rid) { $Rid = Get-NativeRid }
if ($Rid -ne (Get-NativeRid)) { throw 'Use a native runner matching the RID.' }
$out = [IO.Path]::GetFullPath((Join-Path $repo $OutputRoot))
if (!$out.StartsWith($repo + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'OutputRoot must stay inside this checkout.' }
New-Item -ItemType Directory -Force -Path $out,(Join-Path $out 'tmp') | Out-Null
$script:CiLog = Join-Path $out "ci-$Phase.log"
$tempScope = Enter-CiTempScope (Join-Path $out 'tmp')
try {
    @{ directory = $tempScope.Directory; privateUnix = $tempScope.CreatedUnixDirectory;
        socketPathBytes = $(if ($tempScope.CreatedUnixDirectory) { Get-DiagnosticSocketBytes $tempScope.Directory } else { $null }) } |
        ConvertTo-Json | Set-Content (Join-Path $out "ci-temp-$Phase.log")
$build = Join-Path $out 'build'
$testProject = 'tests/TSharpWorkbench.Tests/TSharpWorkbench.Tests.csproj'
$appProject = 'src/TSharpWorkbench/TSharpWorkbench.csproj'
$common = @('--artifacts-path', $build, '-m:1', '-p:NuGetAudit=false')
$version = (& dotnet msbuild $appProject -nologo -getProperty:Version).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot determine application version.' }
Assert-AppVersion $version $Tag
function Test-Selection([string]$Filter, [string]$Name) {
    $results = Join-Path $out 'test-results'
    Invoke-Checked dotnet (@('test', $testProject, '-c', 'Release', '--no-build', '--no-restore', '--filter', $Filter,
        '--logger', "trx;LogFileName=$Name.trx", '--results-directory', $results, '--blame-hang-timeout', '2m') + $common)
    Assert-NoSkippedTests (Join-Path $results "$Name.trx")
}
function Complete-PackageSmoke {
    $extracted = Join-Path $out "extracted/$Rid"
    $dist = Join-Path $out 'distribution'
    $stem = "TSharpWorkbench-$version-$Rid"
    $extension = if ($IsWindows) { 'zip' } else { 'tar.gz' }
    $archive = Join-Path $dist "$stem.$extension"
    $manifestPath = Join-Path $dist "$stem.manifest.json"
    Assert-PackageFiles $extracted $Rid
    $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
    foreach ($f in $manifest.files) {
        if ((Get-FileHash -LiteralPath (Join-Path $extracted $f.path)).Hash.ToLowerInvariant() -cne $f.sha256) { throw "Extracted hash mismatch: $($f.path)" }
    }
    $exe = Join-Path $extracted $(if ($IsWindows) { 'TSharpWorkbench.exe' } else { 'TSharpWorkbench' })
    $help = & $exe help 2>&1; $code = $LASTEXITCODE
    $help | Set-Content (Join-Path $out "archive-smoke.$Rid.txt")
    if ($code -ne 0 -or ($help -join "`n") -notmatch 'Commands:\s') { throw 'Extracted package CLI help failed.' }
    $launcher = Join-Path $extracted $(if ($IsWindows) { 'workbench.cmd' } else { 'workbench.sh' })
    Invoke-Checked $launcher @('help')
    $sample = Join-Path $build ('bin/TSharpWorkbench.Tests/release/SampleTarget/TSharpWorkbench.SampleTarget' + $(if ($IsWindows) { '.exe' } else { '' }))
    if (!(Test-Path -LiteralPath $sample -PathType Leaf)) { throw 'SampleTarget missing: extracted-package IPC smoke may not be skipped.' }
    $smokeRoot = Join-Path $out 'scenario-smoke'
    $smokeResults = Join-Path $smokeRoot 'batch.json'
    Invoke-Checked $exe @('run', '--target', $sample, '--scenario', 'sample/dialog/basic', '--size', '80x25', '--output', $smokeRoot, '--results', $smokeResults)
    Assert-SampleScenarioSmoke $smokeResults | ConvertTo-Json | Set-Content (Join-Path $smokeRoot 'summary.json')
    $sums = @($archive, $manifestPath) | ForEach-Object { "$((Get-FileHash -LiteralPath $_).Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($_))" }
    [IO.File]::WriteAllText((Join-Path $dist "SHA256SUMS-$Rid"), ($sums -join "`n") + "`n")
}
switch ($Phase) {
    Smoke { Complete-PackageSmoke }
    Version {
        Write-Output "version=$version"
        $pre = Get-PrereleaseFlag $version
        if ($env:GITHUB_OUTPUT) { "version=$version`nprerelease=$pre" | Add-Content -LiteralPath $env:GITHUB_OUTPUT }
    }
    Source {
        $head = (& git rev-parse HEAD).Trim(); if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve HEAD' }
        & git rev-parse --verify refs/remotes/origin/main | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'origin/main missing; checkout must fetch full history.' }
        & git merge-base --is-ancestor $head refs/remotes/origin/main
        $onMain = $LASTEXITCODE -eq 0
        Assert-ReleasePolicy $env:GITHUB_EVENT_NAME $env:GITHUB_REF $head $env:GITHUB_SHA $onMain
        if ($env:GITHUB_REF_TYPE -eq 'tag') {
            $tagCommit = (& git rev-parse "$($env:GITHUB_REF)^{commit}").Trim()
            if ($LASTEXITCODE -ne 0 -or $tagCommit -cne $head) { throw 'Tag commit differs from selected source.' }
            Assert-AppVersion $version $env:GITHUB_REF_NAME
        }
        @{ commit = $head; main = (& git rev-parse refs/remotes/origin/main).Trim(); policy = 'main ancestor allowed; tagged commit preserved' } |
            ConvertTo-Json | Set-Content (Join-Path $out 'release-source.json')
    }
    Restore { Invoke-Checked dotnet (@('restore', $testProject, '--locked-mode') + $common) }
    Build { Invoke-Checked dotnet (@('build', $testProject, '-c', 'Release', '--no-restore') + $common) }
    Test {
        $env:TSWB_PROXY_PAYLOAD = Join-Path $build 'bin/TSharpWorkbench/release/proxy-payload'
        Test-Selection 'FullyQualifiedName~CheckpointDetectorTests|FullyQualifiedName~CheckpointPlannerTests|FullyQualifiedName~RecordingConsumptionTests|FullyQualifiedName~InspectionContractTests|FullyQualifiedName~PreferencesAndArtifactNamesTests|FullyQualifiedName~FrameworkLifecycleCompatibilityTests|FullyQualifiedName~CompatibilityTests.MajorDecidesAndBuildMetadataIsInformationOnly|FullyQualifiedName~FixturePackageTests.LoadedFrameworkIsTheQualifiedPackageCandidate' 'headless'
        Test-Selection 'FullyQualifiedName~PublishTests.BuildPayloadManifestListsEveryFileWithItsHashAndNuGetOrgProvenance|FullyQualifiedName~PublishTests.LeanPublishedLayoutLoadsForItsRidAndOffersNoNativeAssetsForOthers|FullyQualifiedName~PublishTests.PayloadWithoutManifestIsRefused|FullyQualifiedName~PublishTests.IncompletePayloadIsRefusedNamingTheMissingFile|FullyQualifiedName~PublishTests.TamperedPayloadIsRefusedNamingTheModifiedFile|FullyQualifiedName~PublishTests.ManifestThatOmitsAStagedFileIsRefused' 'payload-integrity'
    }
    FixtureResolve {
        $selection = Resolve-TvDemoSelection $version (Join-Path $out 'fixture-selection.json')
        Write-Output "Selected $($selection.tag) at $($selection.commit) ($($selection.discoveredUtc))"
    }
    Fixture {
        if (!$IsWindows) { throw 'The mandatory TVDemo integration is qualified on Windows only.' }
        $selectionPath = Join-Path $out 'fixture-selection.json'
        if ($FixtureSelectionFile) {
            $selection = Get-Content -LiteralPath $FixtureSelectionFile -Raw | ConvertFrom-Json
            Assert-FixtureSelection $selection $version
            if ([IO.Path]::GetFullPath($FixtureSelectionFile) -ne $selectionPath) { Copy-Item -LiteralPath $FixtureSelectionFile -Destination $selectionPath }
        } else {
            if ($env:GITHUB_ACTIONS -eq 'true') { throw 'Hosted Fixture requires the selection resolved by the validate job.' }
            $selection = Resolve-TvDemoSelection $version $selectionPath
        }
        $record = [ordered]@{ status = 'FAILED'; selection = $selection; reason = 'Fixture provisioning did not complete.' }
        try {
            $fixture = Build-TvDemoFixture $selection (Join-Path $out 'tvdemo')
            $record.target = $fixture.target; $record.project = $fixture.project
            $record.sourceArchiveSha256 = $fixture.sourceArchiveSha256
            $record.status = 'PROVISIONED'; $record.reason = ''
        } catch { $record.reason = $_.Exception.Message; throw }
        finally { $record | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $out 'fixture.json') }
    }
    ReleaseGate {
        if (!$IsWindows) { throw 'The mandatory TVDemo integration runs on Windows.' }
        $record = [ordered]@{ status = 'FAILED'; reason = 'Gate did not complete.' }
        try {
            $fixture = Get-Content (Join-Path $out 'fixture.json') -Raw | ConvertFrom-Json
            Assert-FixtureSelection $fixture.selection $version
            $record.selection = $fixture.selection
            $record.sourceArchiveSha256 = $fixture.sourceArchiveSha256
            if ($fixture.status -cne 'PROVISIONED' -or !(Test-Path -LiteralPath $fixture.target -PathType Leaf)) { throw 'The pinned real TVDemo fixture is not provisioned.' }
            $cli = Join-Path $build 'bin/TSharpWorkbench/release/TSharpWorkbench.exe'
            $preparedRoot = Join-Path $out 'prepared-tvdemo'
            $prepareOutput = @(& $cli prepare $fixture.target --root $preparedRoot --execute 2>&1)
            $prepareExit = $LASTEXITCODE
            $prepareOutput | Set-Content (Join-Path $out 'tvdemo-prepare.log')
            if ($prepareExit -ne 0) { throw 'Real TVDemo Proxy preparation failed; see tvdemo-prepare.log.' }
            $preparedLine = @($prepareOutput | Where-Object { "$_" -match '^Prepared executable: ' })
            if ($preparedLine.Count -ne 1) { throw 'Preparation did not report exactly one prepared TVDemo executable.' }
            $prepared = "$($preparedLine[0])".Substring('Prepared executable: '.Length)
            if (!(Test-Path -LiteralPath $prepared -PathType Leaf)) { throw 'Prepared TVDemo executable missing.' }
            $scenario = Join-Path $repo 'tests/fixtures/tvdemo-about.scenario.json'
            $record.target = $prepared; $record.scenario = $scenario
            $project = Join-Path $out 'tvdemo.twproj'
            @{ schema = 'TSharpVision.TestWorkbench'; version = 1; name = 'Real TVDemo release integration'; target = $prepared; outputRoot = (Join-Path $out 'tvdemo-results');
                scenarioFiles = @($scenario); selectedIds = @('release/tvdemo-about'); screenSizes = @('80x25'); proxyBackend = 'NullDriver' } |
                ConvertTo-Json -Depth 5 | Set-Content $project
            $results = Join-Path $out 'tvdemo-batch.json'
            Invoke-Checked $cli @('run', $project, '--scenario', 'release/tvdemo-about', '--size', '80x25', '--timeout', '5000', '--scenario-timeout', '30', '--results', $results)
            $record.execution = Assert-TvDemoExecution $results
            $record.status = 'PASSED'; $record.reason = ''; $record.commit = (& git rev-parse HEAD).Trim()
        } catch { $record.reason = $_.Exception.Message; throw }
        finally { $record | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $out 'release-gate.json') }
    }
    Package {
        $publish = Join-Path $out "publish/$Rid"; $extracted = Join-Path $out "extracted/$Rid"
        if ((Test-Path $publish) -or (Test-Path $extracted)) { throw 'Package directories already exist; use a fresh OutputRoot.' }
        # Keep checked-in portable locks immutable; expand RID graphs into disposable locks.
        Invoke-Checked dotnet (@('restore', $appProject, '--locked-mode') + $common)
        $lockRoot = Join-Path $out "rid-locks/$Rid"
        New-Item -ItemType Directory -Force $lockRoot | Out-Null
        $projects = @('TSharpWorkbench','TSharpWorkbench.Testing','TSharpWorkbench.Proxy','TSharpWorkbench.Storage')
        foreach ($project in $projects) { Copy-Item -LiteralPath "src/$project/packages.lock.json" -Destination (Join-Path $lockRoot "$project.lock.json") }
        $ridProps = @("-p:CiRidLockRoot=$lockRoot", "-p:CustomBeforeMicrosoftCommonTargets=$(Join-Path $PSScriptRoot 'ci-rid-restore.targets')")
        Invoke-Checked dotnet (@('restore', $appProject, '-r', $Rid, '-p:RestoreLockedMode=false') + $common + $ridProps)
        foreach ($project in $projects) {
            $a = Get-Content "src/$project/packages.lock.json" -Raw | ConvertFrom-Json -AsHashtable
            $b = Get-Content (Join-Path $lockRoot "$project.lock.json") -Raw | ConvertFrom-Json -AsHashtable
            if (($a.dependencies.'net10.0' | ConvertTo-Json -Depth 50 -Compress) -cne ($b.dependencies.'net10.0' | ConvertTo-Json -Depth 50 -Compress)) { throw "RID restore changed locked portable dependencies: $project" }
        }
        Invoke-Checked dotnet (@('restore', $appProject, '-r', $Rid, '--locked-mode') + $common + $ridProps)
        Invoke-Checked dotnet (@('publish', $appProject, '-c', 'Release', '-r', $Rid, '--self-contained', 'false', '--no-restore', '-o', $publish,
            "-p:WorkbenchPayloadRuntimeIdentifiers=$Rid") + $common + $ridProps)
        if ($IsWindows) {
            '@echo off', 'if not defined TSHARPVISION_DRIVER set TSHARPVISION_DRIVER=Win32ConsoleDriver', '"%~dp0TSharpWorkbench.exe" %*' | Set-Content (Join-Path $publish 'workbench.cmd')
        } else {
            $launcher = @('#!/bin/sh','set -eu','CDPATH= cd -- "$(dirname -- "$0")"',
                'export TSHARPVISION_DRIVER="${TSHARPVISION_DRIVER:-AnsiTerminalDriver}"','exec ./TSharpWorkbench "$@"') -join "`n"
            [IO.File]::WriteAllText((Join-Path $publish 'workbench.sh'), $launcher + "`n")
            Invoke-Checked chmod @('+x', (Join-Path $publish 'TSharpWorkbench'), (Join-Path $publish 'workbench.sh'))
        }
        @("TSharpWorkbench $version ($Rid), framework-dependent; requires matching .NET 10 runtime.",
            'Keep the whole directory, including Help and proxy-payload. See LICENSE.',
            'CLI: TSharpWorkbench help. TUI: workbench.cmd on Windows; ./workbench.sh on Linux/macOS.',
            'Linux/macOS TUI uses AnsiTerminalDriver in an interactive terminal. Windows uses Win32ConsoleDriver.',
            'CLI smoke is not physical UI/input, SDL/GPU or native rendering qualification.',
            'macOS archives are not signed/notarized; follow organization policy for trusted unsigned software.') | Set-Content (Join-Path $publish 'README.release.md')
        Assert-PackageFiles $publish $Rid
        $dist = Join-Path $out 'distribution'; New-Item -ItemType Directory -Force $dist | Out-Null
        $stem = "TSharpWorkbench-$version-$Rid"; $manifestPath = Join-Path $dist "$stem.manifest.json"
        $files = @(Get-ChildItem -LiteralPath $publish -File -Recurse | Sort-Object FullName | ForEach-Object {
            @{ path = [IO.Path]::GetRelativePath($publish, $_.FullName).Replace('\','/'); size = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant() }
        })
        @{ version = $version; rid = $Rid; selfContained = $false; commit = (& git rev-parse HEAD).Trim(); files = $files;
            fixtureSelection = $(if (Test-Path (Join-Path $out 'fixture-selection.json')) { Get-Content (Join-Path $out 'fixture-selection.json') -Raw | ConvertFrom-Json } else { $null });
            releaseQualification = $(if (Test-Path (Join-Path $out 'release-gate.json')) { Get-Content (Join-Path $out 'release-gate.json') -Raw | ConvertFrom-Json } else { $null }) } | ConvertTo-Json -Depth 10 | Set-Content $manifestPath
        if ($IsWindows) {
            $archive = Join-Path $dist "$stem.zip"; Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $archive
            Expand-Archive -LiteralPath $archive -DestinationPath $extracted
        } else {
            $archive = Join-Path $dist "$stem.tar.gz"; Invoke-Checked tar @('-czf', $archive, '-C', $publish, '.')
            New-Item -ItemType Directory -Force $extracted | Out-Null; Invoke-Checked tar @('-xzf', $archive, '-C', $extracted)
            Invoke-Checked test @('-x', (Join-Path $extracted 'TSharpWorkbench'))
            Invoke-Checked test @('-x', (Join-Path $extracted 'workbench.sh'))
        }
        Assert-PackageFiles $extracted $Rid
        foreach ($f in $files) {
            if ((Get-FileHash -LiteralPath (Join-Path $extracted $f.path)).Hash.ToLowerInvariant() -cne $f.sha256) { throw "Extracted hash mismatch: $($f.path)" }
        }
        Complete-PackageSmoke
    }
}
} finally {
    Exit-CiTempScope $tempScope
}
