param(
    [Parameter(Mandatory = $true)][string]$SourcePng,
    [string]$OutputDirectory = $PSScriptRoot,
    [string]$PreviewDirectory
)

# Mechanical Windows format export only; never redraw, recolor or alter logo geometry.
# The 256px PNG is the reproducible source for every ICO image, preserving its alpha.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$sourcePath = (Resolve-Path -LiteralPath $SourcePng).Path
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
if (-not (Test-Path -LiteralPath $outputRoot -PathType Container)) { throw 'Create the artwork output folder first.' }
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)

function Resize-Png([System.Drawing.Image]$Source, [int]$Size) {
    $bitmap = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $attributes = [System.Drawing.Imaging.ImageAttributes]::new()
    try {
        $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $attributes.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)
        $graphics.DrawImage($Source, [System.Drawing.Rectangle]::new(0, 0, $Size, $Size), 0, 0, $Source.Width, $Source.Height, [System.Drawing.GraphicsUnit]::Pixel, $attributes)
        return ,$bitmap
    } catch { $bitmap.Dispose(); throw }
    finally { $attributes.Dispose(); $graphics.Dispose() }
}

$source = [System.Drawing.Image]::FromFile($sourcePath)
$basePng = $null
try {
    if ($source.Width -ne $source.Height) { throw 'The Windows icon source must be square; no cropping or distortion is allowed.' }
    $basePng = Resize-Png $source 256
    $pngPath = Join-Path $outputRoot 'saywhat-icon-256.png'
    # A detached in-memory image avoids holding a lock when SourcePng is this exported file.
    $source.Dispose(); $source = $null
    $basePng.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $basePng.Save((Join-Path $outputRoot 'saywhat-mark.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    $entries = @()
    foreach ($size in $sizes) {
        $image = Resize-Png $basePng $size
        $buffer = [IO.MemoryStream]::new()
        try {
            $image.Save($buffer, [System.Drawing.Imaging.ImageFormat]::Png)
            $entries += [pscustomobject]@{ Size = $size; Bytes = $buffer.ToArray() }
        } finally { $buffer.Dispose(); $image.Dispose() }
    }
    $icoPath = Join-Path $outputRoot 'SayWhat.ico'
    $file = [IO.File]::Open($icoPath, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
    $writer = [IO.BinaryWriter]::new($file)
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$entries.Count)
        [uint32]$offset = 6 + 16 * $entries.Count
        foreach ($entry in $entries) {
            [byte]$dimension = if ($entry.Size -eq 256) { 0 } else { $entry.Size }
            $writer.Write($dimension); $writer.Write($dimension); $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([uint16]1); $writer.Write([uint16]32)
            $writer.Write([uint32]$entry.Bytes.Length); $writer.Write($offset)
            $offset += [uint32]$entry.Bytes.Length
        }
        foreach ($entry in $entries) { $writer.Write([byte[]]$entry.Bytes) }
    } finally { $writer.Dispose(); $file.Dispose() }
    # Exercise the same native decoder as the tray without creating a real tray icon.
    # System.Drawing's selector treats a directory dimension byte of zero as zero
    # rather than 256 on this runtime. The 256 frame is checked by the WPF tests;
    # native tray sizes still must decode exactly here.
    foreach ($size in @(16, 32, 48, 64, 128)) {
        $checkIcon = [System.Drawing.Icon]::new($icoPath, $size, $size)
        try { if ($checkIcon.Width -ne $size -or $checkIcon.Height -ne $size) { throw "ICO size $size failed native decoding." } }
        finally { $checkIcon.Dispose() }
    }
    if ($PreviewDirectory) {
        $previewRoot = [IO.Path]::GetFullPath($PreviewDirectory)
        if (-not (Test-Path -LiteralPath $previewRoot -PathType Container)) { throw 'Create the preview folder first.' }
        $sheet = [System.Drawing.Bitmap]::new(820, 420, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [System.Drawing.Graphics]::FromImage($sheet)
        $font = [System.Drawing.Font]::new('Segoe UI', 11)
        $brush = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#F6EFE7'))
        try {
            $g.Clear([System.Drawing.ColorTranslator]::FromHtml('#191817'))
            $g.DrawString('SayWhat? · Windows icon sizes (actual pixels)', $font, $brush, 24, 20)
            $x = 24
            foreach ($size in @(16, 32, 48, 64, 128, 256)) {
                $preview = Resize-Png $basePng $size
                try { $g.DrawImageUnscaled($preview, $x, 110); $g.DrawString(($size.ToString() + ' px'), $font, $brush, $x, 380) }
                finally { $preview.Dispose() }
                $x += $size + 30
            }
            $sheet.Save((Join-Path $previewRoot 'windows-icon-sizes.png'), [System.Drawing.Imaging.ImageFormat]::Png)
        } finally { $brush.Dispose(); $font.Dispose(); $g.Dispose(); $sheet.Dispose() }
    }
    [pscustomobject]@{ Icon = $icoPath; Png = $pngPath; Sizes = ($sizes -join ', '); AlphaPreserved = $true }
} finally { if ($source) { $source.Dispose() }; if ($basePng) { $basePng.Dispose() } }
