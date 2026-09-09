# Regenerates the original, code-drawn application icon. No external artwork required.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$destination = Join-Path $PSScriptRoot '..\src\TerminalBloops.App\Assets\TerminalBloops.ico'
[System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($destination)) | Out-Null
$frames = @()
foreach ($size in @(16, 24, 32, 48, 64, 128, 256)) {
    $bitmap = [System.Drawing.Bitmap]::new($size, $size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.ScaleTransform($size / 32.0, $size / 32.0)
    $shape = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $shape.AddEllipse(2, 2, 28, 28)
    $blue = [System.Drawing.Drawing2D.LinearGradientBrush]::new([System.Drawing.Point]::new(0, 2), [System.Drawing.Point]::new(0, 30), [System.Drawing.ColorTranslator]::FromHtml('#66D4EF'), [System.Drawing.ColorTranslator]::FromHtml('#56C79F'))
    $blend = [System.Drawing.Drawing2D.ColorBlend]::new(3)
    $blend.Colors = @([System.Drawing.ColorTranslator]::FromHtml('#66D4EF'), [System.Drawing.ColorTranslator]::FromHtml('#008CAA'), [System.Drawing.ColorTranslator]::FromHtml('#56C79F'))
    $blend.Positions = @([single]0, [single]0.52, [single]1)
    $blue.InterpolationColors = $blend
    $edge = [System.Drawing.Pen]::new([System.Drawing.ColorTranslator]::FromHtml('#288B9D'), 0.7)
    $white = [System.Drawing.Pen]::new([System.Drawing.Color]::White, 2.2)
    $highlight = [System.Drawing.Drawing2D.LinearGradientBrush]::new([System.Drawing.Point]::new(0, 4), [System.Drawing.Point]::new(0, 17), [System.Drawing.Color]::FromArgb(215, 255, 255, 255), [System.Drawing.Color]::FromArgb(0, 255, 255, 255))
    $graphics.FillPath($blue, $shape)
    $graphics.DrawPath($edge, $shape)
    $graphics.FillEllipse($highlight, 6, 4, 20, 13)
    $graphics.DrawLine($white, 9, 12, 14, 17)
    $graphics.DrawLine($white, 14, 17, 9, 22)
    $graphics.DrawLine($white, 17, 22, 24, 22)
    $stream = [System.IO.MemoryStream]::new()
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $frames += ,@{ Size = $size; Bytes = $stream.ToArray() }
    $stream.Dispose(); $graphics.Dispose(); $bitmap.Dispose(); $shape.Dispose()
    $blue.Dispose(); $edge.Dispose(); $white.Dispose(); $highlight.Dispose()
}
$file = [System.IO.File]::Create($destination)
$writer = [System.IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Bytes.Length); $writer.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
} finally { $writer.Dispose(); $file.Dispose() }
Write-Output "Generated $destination"
