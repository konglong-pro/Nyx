[CmdletBinding()]
param(
    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string] $Version = '0.1.0-beta.1',

    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string] $OutputDirectory = 'artifacts\release',

    [Parameter()]
    [switch] $NoRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-DotNet {
    param(
        [Parameter(Mandatory)]
        [string] $Executable,

        [Parameter(Mandatory)]
        [string[]] $ArgumentList
    )

    & $Executable @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet exited with code $LASTEXITCODE."
    }
}

function Remove-VerifiedStagingDirectory {
    param(
        [Parameter(Mandatory)]
        [string] $Path,

        [Parameter(Mandatory)]
        [string] $ExpectedParent
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $fullParent = [System.IO.Path]::GetFullPath($ExpectedParent).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $requiredPrefix = $fullParent + [System.IO.Path]::DirectorySeparatorChar

    if (-not $fullPath.StartsWith($requiredPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove staging directory outside '$fullParent': '$fullPath'."
    }

    if ([System.IO.Directory]::Exists($fullPath)) {
        [System.IO.Directory]::Delete($fullPath, $true)
    }
}

if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$') {
    throw "Version '$Version' is not a safe semantic-version value."
}

$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectFile = Join-Path $projectRoot 'src\Nyx.App\Nyx.App.csproj'
$nuGetConfig = Join-Path $projectRoot 'NuGet.Config'
$localDotNet = Join-Path $projectRoot '.dotnet\dotnet.exe'
$dotNet = if (Test-Path -LiteralPath $localDotNet -PathType Leaf) {
    $localDotNet
}
else {
    $command = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $command) {
        throw 'The .NET SDK was not found. Install the SDK selected by global.json or place it in .dotnet.'
    }

    $command.Source
}

if (-not (Test-Path -LiteralPath $projectFile -PathType Leaf)) {
    throw "Application project not found: '$projectFile'."
}

$outputPath = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
    [System.IO.Path]::GetFullPath($OutputDirectory)
}
else {
    [System.IO.Path]::GetFullPath((Join-Path $projectRoot $OutputDirectory))
}

if (Test-Path -LiteralPath $outputPath -PathType Leaf) {
    throw "Output path is a file: '$outputPath'."
}

[System.IO.Directory]::CreateDirectory($outputPath) | Out-Null

$packageName = "Nyx-v$Version-win-x64-portable.zip"
$packagePath = Join-Path $outputPath $packageName
$checksumPath = Join-Path $outputPath 'SHA256SUMS.txt'
if ([System.IO.File]::Exists($packagePath) -or [System.IO.File]::Exists($checksumPath)) {
    throw "Release artifacts already exist in '$outputPath'. Use a fresh output directory; existing releases are immutable."
}

$stagingParent = Join-Path ([System.IO.Path]::GetTempPath()) 'NyxRelease'
$stagingDirectory = Join-Path $stagingParent ([System.Guid]::NewGuid().ToString('N'))
$publishDirectory = Join-Path $stagingDirectory 'publish'
$temporaryPackagePath = Join-Path $stagingDirectory $packageName
$temporaryChecksumPath = Join-Path $stagingDirectory 'SHA256SUMS.txt'

[System.IO.Directory]::CreateDirectory($publishDirectory) | Out-Null

Push-Location $projectRoot
try {
    $sdkVersion = & $dotNet --version
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet --version exited with code $LASTEXITCODE."
    }

    $sdkVersion = ([string] $sdkVersion).Trim()
    $numericSdkVersion = $sdkVersion.Split('-')[0]
    $parsedSdkVersion = $null
    if (-not [System.Version]::TryParse($numericSdkVersion, [ref] $parsedSdkVersion) -or
        $parsedSdkVersion.Major -ne 10) {
        throw "Nyx requires a .NET 10 SDK selected by global.json; found '$sdkVersion'."
    }

    Write-Host "Using .NET SDK $sdkVersion"

    if (-not $NoRestore) {
        $restoreArguments = @(
            'restore',
            $projectFile,
            '--runtime', 'win-x64',
            '--nologo'
        )
        if (Test-Path -LiteralPath $nuGetConfig -PathType Leaf) {
            $restoreArguments += @('--configfile', $nuGetConfig)
        }

        Invoke-DotNet -Executable $dotNet -ArgumentList $restoreArguments
    }

    Invoke-DotNet -Executable $dotNet -ArgumentList @(
        'publish',
        $projectFile,
        '--configuration', 'Release',
        '--runtime', 'win-x64',
        '--self-contained', 'true',
        '--output', $publishDirectory,
        '--no-restore',
        '--nologo',
        '-p:PublishReadyToRun=true',
        '-p:PublishTrimmed=false',
        '-p:PublishSingleFile=false',
        '-p:DebugType=None',
        '-p:DebugSymbols=false',
        "-p:Version=$Version",
        "-p:InformationalVersion=$Version"
    )

    $applicationPath = Join-Path $publishDirectory 'Nyx.exe'
    $runtimePath = Join-Path $publishDirectory 'coreclr.dll'
    if (-not (Test-Path -LiteralPath $applicationPath -PathType Leaf)) {
        throw "Publish output is missing Nyx.exe."
    }

    if (-not (Test-Path -LiteralPath $runtimePath -PathType Leaf)) {
        throw 'Publish output is not a self-contained directory bundle (coreclr.dll is missing).'
    }

    $bundleFiles = @(
        @{ Source = (Join-Path $projectRoot 'README.md'); Destination = 'README.md' },
        @{ Source = (Join-Path $projectRoot 'LICENSE'); Destination = 'LICENSE' },
        @{ Source = (Join-Path $projectRoot 'SECURITY.md'); Destination = 'SECURITY.md' },
        @{ Source = (Join-Path $projectRoot 'THIRD-PARTY-NOTICES.md'); Destination = 'THIRD-PARTY-NOTICES.md' },
        @{ Source = (Join-Path $projectRoot 'global.json'); Destination = 'global.json' },
        @{ Source = (Join-Path (Split-Path -Parent $dotNet) 'LICENSE.txt'); Destination = 'DOTNET-LICENSE.txt' },
        @{ Source = (Join-Path (Split-Path -Parent $dotNet) 'ThirdPartyNotices.txt'); Destination = 'DOTNET-THIRD-PARTY-NOTICES.txt' }
    )
    foreach ($bundleFile in $bundleFiles) {
        if (-not [System.IO.File]::Exists($bundleFile.Source)) {
            throw "Required release notice is missing: '$($bundleFile.Source)'."
        }

        [System.IO.File]::Copy(
            $bundleFile.Source,
            (Join-Path $publishDirectory $bundleFile.Destination),
            $true)
    }

    $publishedDocsDirectory = Join-Path $publishDirectory 'docs'
    [System.IO.Directory]::CreateDirectory($publishedDocsDirectory) | Out-Null
    foreach ($documentationFile in [System.IO.Directory]::GetFiles(
        (Join-Path $projectRoot 'docs'),
        '*.md',
        [System.IO.SearchOption]::TopDirectoryOnly)) {
        [System.IO.File]::Copy(
            $documentationFile,
            (Join-Path $publishedDocsDirectory ([System.IO.Path]::GetFileName($documentationFile))),
            $true)
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $publishDirectory,
        $temporaryPackagePath,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false)

    $archive = [System.IO.Compression.ZipFile]::OpenRead($temporaryPackagePath)
    try {
        $hasApplication = $false
        foreach ($entry in $archive.Entries) {
            if ($entry.FullName -eq 'Nyx.exe') {
                $hasApplication = $true
                break
            }
        }

        if (-not $hasApplication) {
            throw 'The generated archive does not contain Nyx.exe at its root.'
        }
    }
    finally {
        $archive.Dispose()
    }

    $hash = (Get-FileHash -LiteralPath $temporaryPackagePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksumLine = "$hash  $packageName$([System.Environment]::NewLine)"
    $utf8WithoutBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($temporaryChecksumPath, $checksumLine, $utf8WithoutBom)

    [System.IO.File]::Move($temporaryPackagePath, $packagePath)
    try {
        [System.IO.File]::Move($temporaryChecksumPath, $checksumPath)
    }
    catch {
        # Only this invocation's newly installed archive is removed on failure.
        [System.IO.File]::Delete($packagePath)
        throw
    }

    Write-Host "Created $packagePath"
    Write-Host "Created $checksumPath"
}
finally {
    Pop-Location
    Remove-VerifiedStagingDirectory -Path $stagingDirectory -ExpectedParent $stagingParent
}
