param([string]$Out = "$env:TEMP\fluentty-shot.png", [string]$ProcessName = "FluenTTY", [long]$Hwnd = 0)
# Renders a FluenTTY window to a PNG via PrintWindow (works even when the window is behind others).
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Win32 {
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT rect, int size);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
[Win32]::SetProcessDpiAwarenessContext([IntPtr]::op_Explicit(-4)) | Out-Null   # PER_MONITOR_AWARE_V2
if ($Hwnd -ne 0) {
  $hwnd = [IntPtr]$Hwnd
} else {
  $proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
  if (-not $proc) { throw "No $ProcessName window found" }
  $hwnd = $proc.MainWindowHandle
}
$full = New-Object Win32+RECT
[Win32]::GetWindowRect($hwnd, [ref]$full) | Out-Null
$vis = New-Object Win32+RECT
[Win32]::DwmGetWindowAttribute($hwnd, 9, [ref]$vis, 16) | Out-Null   # DWMWA_EXTENDED_FRAME_BOUNDS

$w = $full.Right - $full.Left; $h = $full.Bottom - $full.Top
$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
[Win32]::PrintWindow($hwnd, $hdc, 2) | Out-Null   # PW_RENDERFULLCONTENT
$g.ReleaseHdc($hdc)
$g.Dispose()

# Crop the invisible resize borders when the rectangles allow it
$crop = New-Object System.Drawing.Rectangle ($vis.Left - $full.Left), ($vis.Top - $full.Top), ($vis.Right - $vis.Left), ($vis.Bottom - $vis.Top)
$inside = ($crop.X -ge 0) -and ($crop.Y -ge 0) -and ($crop.Right -le $w) -and ($crop.Bottom -le $h) -and ($crop.Width -gt 0) -and ($crop.Height -gt 0)
if ($inside) {
  $cropped = $bmp.Clone($crop, $bmp.PixelFormat)
  $cropped.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
  $cropped.Dispose()
} else {
  $bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
}
$bmp.Dispose()
Write-Output $Out
