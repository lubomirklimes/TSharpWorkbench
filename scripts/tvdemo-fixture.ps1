# Functions only: source selection/provisioning and execution evidence for one real TVDemo.
function Select-StableFrameworkTag([string[]]$Lines, [int]$Major) {
    $tags = @{}
    foreach ($line in $Lines) {
        if ($line -match '^([0-9a-f]{40})\s+refs/tags/(v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*))(\^\{\})?$') {
            $name = $Matches[2]; $numeric = [version]$name.Substring(1)
            if ($numeric.Major -ne $Major) { continue }
            if (!$tags.ContainsKey($name) -or $Matches[6]) {
                $tags[$name] = @{ tag = $name; commit = $Matches[1]; numeric = $numeric }
            }
        }
    }
    $latest = @($tags.Values | Sort-Object { $_.numeric } -Descending)
    if (!$latest.Count) { throw "No stable v$Major.x.y tag found at the authoritative repository." }
    return $latest[0]
}
function Assert-FixtureSelection($Selection, [string]$AppVersion) {
    if ($Selection.repository -cne 'https://github.com/lubomirklimes/TSharpVision.git' -or
        $Selection.commit -notmatch '^[0-9a-f]{40}$' -or $Selection.tag -notmatch '^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$' -or
        ([version]$Selection.tag.Substring(1)).Major -ne [int]$AppVersion.Split('.')[0] -or
        $Selection.appVersion -cne $AppVersion -or !$Selection.discoveredUtc -or
        $Selection.policy -cne 'latest-stable-matching-major') { throw 'Invalid or mismatched pinned TVDemo selection.' }
}
function Resolve-TvDemoSelection([string]$AppVersion, [string]$Path) {
    if (Test-Path -LiteralPath $Path) {
        $selection = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
        Assert-FixtureSelection $selection $AppVersion
        return $selection
    }
    $repository = 'https://github.com/lubomirklimes/TSharpVision.git'
    $lines = @(& git ls-remote --tags $repository)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot discover public TSharpVision tags; no substitute fixture is permitted.' }
    $latest = Select-StableFrameworkTag $lines ([int]$AppVersion.Split('.')[0])
    $selection = [ordered]@{ repository = $repository; tag = $latest.tag; commit = $latest.commit;
        discoveredUtc = [DateTime]::UtcNow.ToString('o'); appVersion = $AppVersion; policy = 'latest-stable-matching-major' }
    $selection | ConvertTo-Json | Set-Content -LiteralPath $Path
    return [pscustomobject]$selection
}
function Build-TvDemoFixture($Selection, [string]$Directory) {
    $source = Join-Path $Directory 'source'
    if (Test-Path -LiteralPath $source) { throw 'Fixture source already exists; choose a fresh candidate output root.' }
    New-Item -ItemType Directory -Force -Path $source | Out-Null
    $archive = Join-Path $Directory 'source.tar.gz'
    # codeload pins the resolved commit, never a mutable branch or a tag re-resolution.
    Invoke-WebRequest -Uri "https://codeload.github.com/lubomirklimes/TSharpVision/tar.gz/$($Selection.commit)" -OutFile $archive
    Invoke-Checked tar @('-xzf', $archive, '--strip-components=1', '-C', $source) | Out-Host
    $projects = @(Get-ChildItem -LiteralPath $source -Recurse -Filter 'TSharpVision.Samples.TVDemo.csproj' -File)
    if ($projects.Count -ne 1) { throw 'The pinned source must contain exactly one real TVDemo project.' }
    $fixtureBuild = Join-Path $Directory 'build'
    Invoke-Checked dotnet @('build', $projects[0].FullName, '-c', 'Release', '--artifacts-path', $fixtureBuild, '-m:1',
        '-p:NuGetAudit=false', "-p:SourceRevisionId=$($Selection.commit)", '-p:EnableSourceControlManagerQueries=false') | Out-Host
    $executable = Join-Path $fixtureBuild 'bin/TSharpVision.Samples.TVDemo/release/TSharpVision.Samples.TVDemo.exe'
    if (!(Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'Real TVDemo executable missing after build.' }
    return @{ target = $executable; project = [IO.Path]::GetRelativePath($source, $projects[0].FullName);
        sourceArchiveSha256 = (Get-FileHash -LiteralPath $archive).Hash.ToLowerInvariant() }
}
function Assert-TvDemoExecution([string]$ResultsPath) {
    $batch = Get-Content -LiteralPath $ResultsPath -Raw | ConvertFrom-Json
    $results = @($batch.results)
    if ($results.Count -ne 1 -or $results[0].scenarioId -cne 'release/tvdemo-about' -or $results[0].outcome -cne 'Passed') {
        throw 'Mandatory TVDemo scenario did not execute exactly once and PASS.'
    }
    $assertions = @($results[0].checkpoints | ForEach-Object { $_.assertions })
    if ($assertions.Count -lt 2 -or @($assertions | Where-Object { $_.outcome -cne 'PASS' }).Count -gt 0 -or
        @($assertions | Where-Object { $_.id -ceq 'about-title' -and $_.kind -ceq 'screen-contains' }).Count -ne 1 -or
        @($assertions | Where-Object { $_.id -ceq 'about-content' -and $_.kind -ceq 'screen-contains' }).Count -ne 1) {
        throw 'Mandatory TVDemo title/content assertions must actually execute and pass.'
    }
    return @{ outcome = 'Passed'; scenario = $results[0].scenarioId; checkpoints = @($results[0].checkpoints).Count;
        assertions = $assertions.Count; runRoot = $batch.runRoot; reason = $results[0].reason }
}
