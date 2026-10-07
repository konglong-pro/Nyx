[CmdletBinding()]
param(
    [switch] $NoRestore,
    [switch] $IncludeDestructiveTests,
    [switch] $Package
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$localDotNet = Join-Path $projectRoot '.dotnet\dotnet.exe'
$dotNet = if (Test-Path -LiteralPath $localDotNet -PathType Leaf) {
    $localDotNet
}
else {
    (Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
}

function Invoke-CheckedDotNet {
    param([string[]] $ArgumentList)
    & $dotNet @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($ArgumentList[0]) failed with exit code $LASTEXITCODE."
    }
}

Push-Location $projectRoot
try {
    if (-not $NoRestore) {
        Invoke-CheckedDotNet @('restore', 'Nyx.slnx', '--nologo')
    }

    Invoke-CheckedDotNet @('build', 'Nyx.slnx', '-c', 'Release', '--no-restore', '--nologo')
    foreach ($testProject in @('Nyx.Core.Tests', 'Nyx.App.Tests')) {
        Invoke-CheckedDotNet @(
            'test', "tests/$testProject/$testProject.csproj",
            '-c', 'Release', '--no-build', '--no-restore', '--nologo'
        )
    }

    Invoke-CheckedDotNet @(
        'test', 'tests/Nyx.DestructiveTests/Nyx.DestructiveTests.csproj',
        '-c', 'Release', '--no-build', '--no-restore', '--nologo', '--filter', 'TestCategory=Inventory'
    )

    if ($IncludeDestructiveTests) {
        $previousOptIn = [Environment]::GetEnvironmentVariable('NYX_RUN_DESTRUCTIVE_TESTS', 'Process')
        try {
            $env:NYX_RUN_DESTRUCTIVE_TESTS = '1'
            Invoke-CheckedDotNet @(
                'test', 'tests/Nyx.DestructiveTests/Nyx.DestructiveTests.csproj',
                '-c', 'Release', '--no-build', '--no-restore', '--nologo', '--filter', 'TestCategory=Destructive'
            )
        }
        finally {
            [Environment]::SetEnvironmentVariable('NYX_RUN_DESTRUCTIVE_TESTS', $previousOptIn, 'Process')
        }
    }

    if ($Package) {
        $candidateDirectory = Join-Path $projectRoot (
            'artifacts\verification\' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N'))
        & (Join-Path $PSScriptRoot 'build-release.ps1') -NoRestore -OutputDirectory $candidateDirectory
        if (-not $?) {
            throw 'Portable package verification failed.'
        }

        $checksum = (Get-Content -LiteralPath (Join-Path $candidateDirectory 'SHA256SUMS.txt') -Raw).Trim() -split '  ', 2
        $archivePath = Join-Path $candidateDirectory $checksum[1]
        $actualHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actualHash -ne $checksum[0]) {
            throw "Package checksum mismatch: '$archivePath'."
        }

        Write-Host "Verified portable package: $archivePath"
    }
}
finally {
    Pop-Location
}
