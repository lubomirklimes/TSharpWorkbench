Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
function Invoke-Checked([string]$File, [string[]]$Arguments) {
    if (Get-Variable -Name CiLog -Scope Script -ErrorAction SilentlyContinue) {
        & $File @Arguments 2>&1 | Tee-Object -FilePath $script:CiLog -Append
    } else {
        & $File @Arguments
    }
    if ($LASTEXITCODE -ne 0) { throw "$File failed (exit $LASTEXITCODE): $($Arguments -join ' ')" }
}
function Assert-AppVersion([string]$Version, [string]$Tag = '') {
    if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*))*)?$') {
        throw "Application version must be SemVer without build metadata: $Version"
    }
    if ($Tag -and $Tag -cne "v$Version") { throw "Tag '$Tag' does not match application version v$Version" }
}
function Get-PrereleaseFlag([string]$Version) {
    Assert-AppVersion $Version
    return $Version.Contains('-').ToString().ToLowerInvariant()
}
function Get-NativeRid {
    $arch = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
    if ($IsWindows) { return "win-$arch" }
    if ($IsLinux) { return "linux-$arch" }
    if ($IsMacOS) { return "osx-$arch" }
    throw 'Unsupported runner OS'
}
function Assert-ReleasePolicy([string]$Event, [string]$Ref, [string]$Head, [string]$Source, [bool]$OnMain) {
    if (!$Head -or $Head -cne $Source -or !$OnMain) { throw 'Release source must be the selected commit on origin/main history.' }
    if ($Event -eq 'workflow_dispatch') {
        if ($Ref -cne 'refs/heads/main') { throw 'Manual candidates must be dispatched from main.' }
    } elseif ($Event -eq 'push') {
        if ($Ref -notmatch '^refs/tags/v') { throw 'Publishing requires a version tag.' }
    } else { throw "Unsupported release event: $Event" }
}
function Assert-NoSkippedTests([string]$Path) {
    [xml]$trx = Get-Content -LiteralPath $Path -Raw
    $ns = [System.Xml.XmlNamespaceManager]::new($trx.NameTable)
    $ns.AddNamespace('t', 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
    $c = $trx.SelectSingleNode('//t:Counters', $ns)
    if (!$c -or [int]$c.total -eq 0 -or [int]$c.executed -ne [int]$c.total -or [int]$c.passed -ne [int]$c.total) {
        throw "Gate requires nonempty, all-executed, all-passing TRX: $Path"
    }
}
function Assert-PackageFiles([string]$Directory, [string]$Rid) {
    $exe = if ($Rid.StartsWith('win-')) { 'TSharpWorkbench.exe' } else { 'TSharpWorkbench' }
    foreach ($relative in @($exe, 'TSharpWorkbench.dll', 'TSharpWorkbench.deps.json', 'TSharpWorkbench.runtimeconfig.json',
        'TSharpVision.dll', 'TSharpVision.Drivers.Terminal.dll', 'LICENSE', 'README.release.md',
        'Help/TSharpWorkbench.hlp', 'Help/TSharpWorkbench.hlp.sha256',
        'proxy-payload/TSharpWorkbench.Proxy.dll', 'proxy-payload/TSharpWorkbench.Testing.dll',
        'proxy-payload/TSharpVision.Drivers.SDL.dll', 'proxy-payload/payload-manifest.json')) {
        if (!(Test-Path -LiteralPath (Join-Path $Directory $relative) -PathType Leaf)) { throw "Package missing $relative" }
    }
    $payload = Join-Path $Directory 'proxy-payload'
    $manifest = Get-Content (Join-Path $payload 'payload-manifest.json') -Raw | ConvertFrom-Json
    if ($manifest.kind -ne 'publish' -or $manifest.frameworkMode -ne 'package') { throw 'Expected published package-mode Proxy manifest' }
    if (@($manifest.rids) -notcontains $Rid) { throw "Proxy has no native closure for $Rid" }
    foreach ($f in $manifest.files) {
        $full = [IO.Path]::GetFullPath((Join-Path $payload $f.path))
        if (!$full.StartsWith([IO.Path]::GetFullPath($payload) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe payload path' }
        if (!(Test-Path -LiteralPath $full) -or (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash -cne $f.sha256.ToUpperInvariant()) { throw "Proxy hash mismatch: $($f.path)" }
    }
    foreach ($native in Get-ChildItem (Join-Path $Directory 'runtimes') -Directory -ErrorAction SilentlyContinue) {
        if ($native.Name -ne $Rid) { throw "Foreign native assets: $($native.Name)" }
    }
    foreach ($native in Get-ChildItem (Join-Path $payload 'runtimes') -Directory -ErrorAction SilentlyContinue) {
        if ($native.Name -ne $Rid) { throw "Foreign Proxy native assets: $($native.Name)" }
    }
}
function Get-DiagnosticSocketBytes([string]$TempDirectory) {
    $socket = $TempDirectory.TrimEnd('/') + '/CoreFxPipe_tsharp-diagnostics-' + ('0' * 32)
    return [Text.Encoding]::UTF8.GetByteCount($socket)
}
function Assert-DiagnosticSocketBudget([string]$TempDirectory) {
    # Below Darwin's 104-byte limit, including room for /tmp -> /private/tmp.
    if ((Get-DiagnosticSocketBytes $TempDirectory) -gt 96) { throw 'Diagnostic Unix socket path exceeds the 96-byte CI budget.' }
}
function Assert-CiUnixTempDirectory([string]$Directory) {
    if ($Directory -cnotmatch '^/tmp/tsw-[A-Za-z0-9]{12}$') { throw 'Expected an exclusively created /tmp/tsw-<12 random characters> directory.' }
    Assert-DiagnosticSocketBudget $Directory
}
function Get-CiTempEnvironment([string]$Directory, [bool]$Unix) {
    if ($Unix) {
        Assert-CiUnixTempDirectory $Directory
        return @{ TMPDIR = $Directory; TMP = $Directory; TEMP = $Directory }
    }
    return @{ TMP = $Directory; TEMP = $Directory }
}
function Enter-CiTempScope([string]$WindowsDirectory) {
    $original = @{}
    foreach ($name in @('TMPDIR','TMP','TEMP')) { $original[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
    $directory = $WindowsDirectory
    $createdUnixDirectory = $false
    $token = [Guid]::NewGuid().ToString('N')
    if (!$IsWindows) {
        # mktemp atomically creates a private 0700 directory; do not inherit macOS /var/folders TMPDIR.
        $created = @(& mktemp -d '/tmp/tsw-XXXXXXXXXXXX')
        if ($LASTEXITCODE -ne 0 -or $created.Count -ne 1) { throw 'Cannot create private Unix CI temp directory.' }
        $directory = "$($created[0])".Trim()
        Assert-CiUnixTempDirectory $directory
        if ((Get-Item -LiteralPath $directory -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Unix CI temp root must not be a symlink.' }
        [IO.File]::WriteAllText((Join-Path $directory '.tsw-ci-owner'), $token)
        $createdUnixDirectory = $true
    }
    $scope = [pscustomobject]@{ Directory = $directory; Original = $original; Token = $token; CreatedUnixDirectory = $createdUnixDirectory }
    try {
        if ($createdUnixDirectory) {
            $mode = [IO.File]::GetUnixFileMode($directory)
            if ([int]$mode -ne 448) { throw 'Unix CI temp directory must have mode 0700.' }
        }
        $environment = Get-CiTempEnvironment $directory $createdUnixDirectory
        foreach ($name in $environment.Keys) { [Environment]::SetEnvironmentVariable($name, $environment[$name], 'Process') }
        return $scope
    } catch {
        Exit-CiTempScope $scope
        throw
    }
}
function Exit-CiTempScope($Scope) {
    try {
        if ($Scope.CreatedUnixDirectory -and (Test-Path -LiteralPath $Scope.Directory)) {
            Assert-CiUnixTempDirectory $Scope.Directory
            if ((Get-Item -LiteralPath $Scope.Directory -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Refusing cleanup of a replaced Unix CI temp root.' }
            $marker = Join-Path $Scope.Directory '.tsw-ci-owner'
            if (!(Test-Path -LiteralPath $marker -PathType Leaf) -or [IO.File]::ReadAllText($marker) -cne $Scope.Token) { throw 'Refusing cleanup of a Unix temp directory not owned by this invocation.' }
            # Literal, validated, exclusively created root only. Never remove /tmp or a caller-supplied directory.
            Remove-Item -LiteralPath $Scope.Directory -Recurse -Force
        }
    } finally {
        foreach ($name in $Scope.Original.Keys) {
            $value = $Scope.Original[$name]
            if ($null -eq $value) { $value = [NullString]::Value }
            [Environment]::SetEnvironmentVariable($name, $value, 'Process')
        }
    }
}
function Assert-SampleScenarioSmoke([string]$ResultsPath) {
    $batch = Get-Content -LiteralPath $ResultsPath -Raw | ConvertFrom-Json
    $results = @($batch.results)
    if ($results.Count -ne 1 -or $results[0].scenarioId -cne 'sample/dialog/basic' -or $results[0].outcome -cne 'Passed') { throw 'Extracted-package sample scenario must execute exactly once and PASS.' }
    $checkpoints = @($results[0].checkpoints)
    $assertions = @($checkpoints | ForEach-Object { $_.assertions })
    if (!$checkpoints.Count -or !$assertions.Count -or @($assertions | Where-Object { $_.outcome -cne 'PASS' }).Count) { throw 'Extracted-package scenario must execute real checkpoints and passing assertions.' }
    return @{ scenario = $results[0].scenarioId; checkpoints = $checkpoints.Count; assertions = $assertions.Count; outcome = 'Passed'; runRoot = $batch.runRoot }
}
