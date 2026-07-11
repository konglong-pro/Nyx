[CmdletBinding()]
param(
    [Parameter()]
    [string] $OutputPath = 'src\Nyx.App\Assets\nyx.ico',

    [Parameter()]
    [string] $PreviewPath = 'artifacts\icon-preview.png'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase

$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$iconPath = if ([System.IO.Path]::IsPathRooted($OutputPath)) {
    [System.IO.Path]::GetFullPath($OutputPath)
}
else {
    [System.IO.Path]::GetFullPath((Join-Path $projectRoot $OutputPath))
}
$preview = if ([System.IO.Path]::IsPathRooted($PreviewPath)) {
    [System.IO.Path]::GetFullPath($PreviewPath)
}
else {
    [System.IO.Path]::GetFullPath((Join-Path $projectRoot $PreviewPath))
}

$crescent = [System.Windows.Media.Geometry]::Parse(
    'M 31,5 C 17,8 8,19 8,32 C 8,47 20,58 35,59 C 25,53 19,43 19,32 C 19,20 23,11 31,5 Z')
$fragments = [System.Windows.Media.Geometry]::Parse(
    'M 39,14 L 43,16 L 39,18 Z M 47,21 L 53,24 L 47,27 Z M 42,31 L 49,34 L 42,37 Z M 48,41 L 54,44 L 48,47 Z M 38,49 L 43,52 L 38,54 Z')
$crescentBrush = [System.Windows.Media.SolidColorBrush]::new(
    [System.Windows.Media.Color]::FromRgb(32, 36, 75))
$fragmentBrush = [System.Windows.Media.SolidColorBrush]::new(
    [System.Windows.Media.Color]::FromRgb(112, 91, 240))
$outline = [System.Windows.Media.Pen]::new(
    [System.Windows.Media.SolidColorBrush]::new(
        [System.Windows.Media.Color]::FromArgb(210, 225, 232, 255)),
    0.8)
$crescentBrush.Freeze()
$fragmentBrush.Freeze()
$outline.Freeze()
$crescent.Freeze()
$fragments.Freeze()

function New-NyxBitmap {
    param([Parameter(Mandatory)][int] $Size)

    $visual = [System.Windows.Media.DrawingVisual]::new()
    $drawing = $visual.RenderOpen()
    try {
        $scale = $Size / 64.0
        $drawing.PushTransform([System.Windows.Media.ScaleTransform]::new($scale, $scale))
        $drawing.DrawGeometry($crescentBrush, $outline, $crescent)
        $drawing.DrawGeometry($fragmentBrush, $outline, $fragments)
        $drawing.Pop()
    }
    finally {
        $drawing.Close()
    }

    $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new(
        $Size,
        $Size,
        96,
        96,
        [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $bitmap.Freeze()
    return $bitmap
}

function Get-PngBytes {
    param([Parameter(Mandatory)][int] $Size)

    $pngEncoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $pngEncoder.Frames.Add(
        [System.Windows.Media.Imaging.BitmapFrame]::Create((New-NyxBitmap $Size)))
    $memory = [System.IO.MemoryStream]::new()
    try {
        $pngEncoder.Save($memory)
        return $memory.ToArray()
    }
    finally {
        $memory.Dispose()
    }
}

[System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($iconPath)) | Out-Null
$frames = [System.Collections.Generic.List[object]]::new()
foreach ($size in 16, 20, 24, 32, 40, 48, 64, 128, 256) {
    $frames.Add([pscustomobject]@{
        Size = $size
        Bytes = [byte[]](Get-PngBytes $size)
    })
}

# ICO is a small directory followed by one PNG payload per image size. PNG-backed
# frames are supported by the Windows shell and preserve alpha at every size.
$stream = [System.IO.File]::Create($iconPath)
$writer = [System.IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$frames.Count)
    $payloadOffset = [uint32](6 + (16 * $frames.Count))
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -eq 256) { [byte]0 } else { [byte]$frame.Size }
        $writer.Write($dimension)
        $writer.Write($dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Bytes.Length)
        $writer.Write($payloadOffset)
        $payloadOffset = [uint32]($payloadOffset + $frame.Bytes.Length)
    }

    foreach ($frame in $frames) {
        $writer.Write($frame.Bytes)
    }
}
finally {
    $writer.Dispose()
    $stream.Dispose()
}

[System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($preview)) | Out-Null
$previewFrame = $frames | Where-Object Size -eq 256 | Select-Object -First 1
[System.IO.File]::WriteAllBytes($preview, $previewFrame.Bytes)

Write-Host "Created $iconPath"
Write-Host "Created $preview"
