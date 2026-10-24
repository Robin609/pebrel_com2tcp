param()
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$taskIconPath = Join-Path $PSScriptRoot 'PebrelLauncher.ico'
$taskImages = @()
$taskSource = [System.Drawing.Image]::FromFile((Join-Path $PSScriptRoot '..\ui\PebrelMark.png'))
foreach ($taskSize in @(16, 24, 32, 48, 64, 128, 256)) {
    $taskBitmap = New-Object System.Drawing.Bitmap($taskSize, $taskSize)
    $taskGraphics = [System.Drawing.Graphics]::FromImage($taskBitmap)
    $taskGraphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    # Keep the upstream Titanium identity, rather than drawing another logo.
    $taskGraphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $taskGraphics.DrawImage($taskSource, 0, 0, $taskSize, $taskSize)
    if ($taskSize -eq 128) {
        $taskBitmap.Save((Join-Path $PSScriptRoot 'PebrelLauncher.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    }
    $taskStream = New-Object System.IO.MemoryStream
    if ($taskSize -lt 256) {
        # .NET Framework Icon.ToBitmap needs a DIB frame, not a PNG frame.
        $taskNativeIcon = [System.Drawing.Icon]::FromHandle($taskBitmap.GetHicon())
        try { $taskNativeIcon.Save($taskStream) } finally { $taskNativeIcon.Dispose() }
        $taskSingleIcon = $taskStream.ToArray()
        $taskFrameSize = [BitConverter]::ToInt32($taskSingleIcon, 14)
        $taskFrameOffset = [BitConverter]::ToInt32($taskSingleIcon, 18)
        $taskFrame = New-Object byte[] $taskFrameSize
        [Array]::Copy($taskSingleIcon, $taskFrameOffset, $taskFrame, 0, $taskFrameSize)
    } else {
        $taskBitmap.Save($taskStream, [System.Drawing.Imaging.ImageFormat]::Png)
        $taskFrame = $taskStream.ToArray()
    }
    $taskImages += ,@{ Size = $taskSize; Bytes = $taskFrame }
    $taskStream.Dispose(); $taskGraphics.Dispose(); $taskBitmap.Dispose()
}
$taskSource.Dispose()
$taskOutput = [System.IO.File]::Create($taskIconPath)
$taskWriter = New-Object System.IO.BinaryWriter($taskOutput)
try {
    $taskWriter.Write([UInt16]0); $taskWriter.Write([UInt16]1); $taskWriter.Write([UInt16]$taskImages.Count)
    $taskOffset = 6 + 16 * $taskImages.Count
    foreach ($taskImage in $taskImages) {
        $taskDimension = if ($taskImage.Size -eq 256) { 0 } else { $taskImage.Size }
        $taskWriter.Write([byte]$taskDimension); $taskWriter.Write([byte]$taskDimension)
        $taskWriter.Write([byte]0); $taskWriter.Write([byte]0)
        $taskWriter.Write([UInt16]1); $taskWriter.Write([UInt16]32)
        $taskWriter.Write([UInt32]$taskImage.Bytes.Length); $taskWriter.Write([UInt32]$taskOffset)
        $taskOffset += $taskImage.Bytes.Length
    }
    foreach ($taskImage in $taskImages) { $taskWriter.Write([byte[]]$taskImage.Bytes) }
} finally { $taskWriter.Dispose(); $taskOutput.Dispose() }
Write-Host ('Icon: ' + $taskIconPath)
