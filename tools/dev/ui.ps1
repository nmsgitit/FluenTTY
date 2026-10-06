param(
    [Parameter(Mandatory)][ValidateSet('windows', 'tree', 'invoke', 'select', 'addselect', 'toggle', 'state', 'settext', 'password', 'value', 'expand', 'focus', 'close')][string]$Action,
    [string]$Name = "",
    [string]$Window = "",
    [string]$Type = "",   # optional ControlType filter, e.g. Button (when a label has the same name)
    [string]$Value = "",
    [int]$Depth = 12,
    [int]$ProcessId = 0   # only this FluenTTY process (use it whenever the user may have their own copy open)
)
# Drives the running FluenTTY through UI Automation, by accessible name. No mouse, no focus stealing
# (except 'focus'). Examples:
#   .\ui.ps1 windows                              # top-level FluenTTY windows with HWNDs (for screenshot.ps1 -Hwnd)
#   .\ui.ps1 tree -Window "Welcome to FluenTTY"   # names of everything in a window
#   .\ui.ps1 invoke -Name "Settings"              # click a button (searches all FluenTTY windows unless -Window)
#   .\ui.ps1 settext -Name "Host" -Value "user@host"
#   .\ui.ps1 settext -Name "Terminal" -Value "ls`n"   # types into the terminal (no real keystrokes)
#   .\ui.ps1 value -Name "Terminal"                   # the terminal's visible text
#   .\ui.ps1 addselect -Name "file.txt"              # multi-select list rows
#   .\ui.ps1 state -Name "Cancel"                    # IsEnabled / HasKeyboardFocus
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$AE = [System.Windows.Automation.AutomationElement]
$procs = if ($ProcessId) { Get-Process -Id $ProcessId -ErrorAction SilentlyContinue } else { Get-Process -Name FluenTTY -ErrorAction SilentlyContinue }
if (-not $procs) { throw "FluenTTY is not running" }
$ids = @($procs | ForEach-Object { $_.Id })
$cond = New-Object System.Windows.Automation.PropertyCondition ($AE::ControlTypeProperty), ([System.Windows.Automation.ControlType]::Window)
# Top-level windows come from Win32 EnumWindows, not only the UIA desktop's children: a pop-out window
# can have an unnamed pane as its UIA parent and would otherwise be missed.
if (-not ("UiTop" -as [type])) {
    Add-Type @"
using System; using System.Runtime.InteropServices; using System.Collections.Generic;
public static class UiTop { public delegate bool P(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumWindows(P f, IntPtr l);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  public static List<IntPtr> For(uint[] pids) { var r = new List<IntPtr>(); EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (IsWindowVisible(h) && Array.IndexOf(pids, p) >= 0) r.Add(h); return true; }, IntPtr.Zero); return r; } }
"@
}
$tops = @(foreach ($hw in [UiTop]::For([uint32[]]$ids)) { try { $AE::FromHandle($hw) } catch { } })
# Owned dialogs can also appear as children of the main window
$all = @()
$seen = @{}
foreach ($t in $tops) {
    foreach ($w in @($t) + @($t.FindAll([System.Windows.Automation.TreeScope]::Children, $cond))) {
        $key = $w.Current.NativeWindowHandle
        if ($key -and $seen.ContainsKey($key)) { continue }
        $seen[$key] = $true
        $all += $w
    }
}
if ($Action -eq 'windows') {
    foreach ($w in $all) { "{0}`t{1}" -f $w.Current.NativeWindowHandle, $w.Current.Name }
    return
}
$targets = if ($Window) { @($all | Where-Object { $_.Current.Name -like "*$Window*" }) } else { $all }
if (-not $targets) { throw "No window matching '$Window'" }

if ($Action -eq 'tree') {
    function Walk($el, $indent, $level) {
        if ($level -gt $Depth) { return }
        $c = $el.Current
        if ($c.Name -or $c.AutomationId) { "{0}{1} [{2}] {3}" -f $indent, $c.Name, $c.ControlType.ProgrammaticName.Replace('ControlType.', ''), $c.AutomationId }
        $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
        $child = $walker.GetFirstChild($el)
        while ($child) { Walk $child ($indent + '  ') ($level + 1); $child = $walker.GetNextSibling($child) }
    }
    foreach ($t in $targets) { "== $($t.Current.Name)"; Walk $t '' 0 }
    return
}
if ($Action -eq 'close') {
    foreach ($t in $targets) { $p = $null; if ($t.TryGetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern, [ref]$p)) { $p.Close(); "closed $($t.Current.Name)" } }
    return
}

if ($Action -eq 'password') {
    # First password box in the matching window(s), whatever its name: .\ui.ps1 password -Window "files sftp" -Value "test"
    $pc = New-Object System.Windows.Automation.PropertyCondition ($AE::IsPasswordProperty), $true
    foreach ($t in $targets) { $pb = $t.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $pc); if ($pb) { $pb.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Value); "set password"; return } }
    throw "No password box"
}
$nameCond = New-Object System.Windows.Automation.PropertyCondition ($AE::NameProperty), $Name
if ($Type) {
    $ct = [System.Windows.Automation.ControlType].GetField($Type).GetValue($null)
    $nameCond = New-Object System.Windows.Automation.AndCondition $nameCond, (New-Object System.Windows.Automation.PropertyCondition ($AE::ControlTypeProperty), $ct)
}
$el = $null
foreach ($t in $targets) { $el = $t.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $nameCond); if ($el) { break } }
if (-not $el) { throw "Element '$Name' not found" }
$p = $null
switch ($Action) {
    'invoke' {
        if ($el.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$p)) { $p.Invoke(); "invoked $Name"; return }
        if ($el.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$p)) { $p.Select(); "selected $Name"; return }
        if ($el.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$p)) { $p.Toggle(); "toggled $Name"; return }
        throw "No invoke pattern on '$Name'"
    }
    'select' { $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); "selected $Name" }
    'addselect' { $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).AddToSelection(); "added $Name to selection" }
    'state' { $c = $el.Current; "enabled=$($c.IsEnabled) focused=$($c.HasKeyboardFocus)" }
    'toggle' { $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle(); "toggled $Name" }
    'expand' { $el.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); "expanded $Name" }
    'settext' { $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Value); "set $Name" }
    'value' { $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value }
    'focus' { $el.SetFocus(); "focused $Name" }
}
