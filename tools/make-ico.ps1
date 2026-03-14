Add-Type -AssemblyName System.Drawing

$sourcePath = Join-Path $PSScriptRoot '..\Assets\Square150x150Logo.scale-200.png'
$outputPath = Join-Path $PSScriptRoot '..\Assets\app.ico'
$sizes      = @(16, 24, 32, 48, 64, 128, 256)

$source = [System.Drawing.Bitmap]::new($sourcePath)

$streams = foreach ($sz in $sizes) {
    $bmp = [System.Drawing.Bitmap]::new($sz, $sz)
    $g   = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.DrawImage($source, 0, 0, $sz, $sz)
    $g.Dispose()
    $ms = [System.IO.MemoryStream]::new()
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $ms
}

$fs     = [System.IO.File]::Create($outputPath)
$writer = [System.IO.BinaryWriter]::new($fs)

# ICO header
$writer.Write([uint16]0)
$writer.Write([uint16]1)
$writer.Write([uint16]$sizes.Count)

# Directory entries
$dataOffset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz  = $sizes[$i]
    $len = [uint32]$streams[$i].Length
    $writer.Write([byte]$(if ($sz -eq 256) { 0 } else { $sz }))
    $writer.Write([byte]$(if ($sz -eq 256) { 0 } else { $sz }))
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]32)
    $writer.Write($len)
    $writer.Write([uint32]$dataOffset)
    $dataOffset += $len
}

foreach ($ms in $streams) { $writer.Write($ms.ToArray()); $ms.Dispose() }
$writer.Dispose()
$source.Dispose()

Write-Host "Created $outputPath ($((Get-Item $outputPath).Length) bytes)"
