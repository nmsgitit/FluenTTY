param([Parameter(Mandatory)][string]$CommandLine, [int]$TimeoutSeconds = 600)
# Runs a command in a fresh process created by WMI, i.e. outside any MSIX package identity this shell
# inherited. A shell descended from a packaged app (e.g. the Claude desktop app) carries that app's
# identity, and Windows then virtualizes its HKCU\Software writes into the package's private hive:
# an installer run from such a shell registers itself where only that process tree can see it
# (no Programs and Features / Settings entry). Use this for setup.exe, unins000.exe and any check
# of what the real registry contains.
$r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = $CommandLine }
if ($r.ReturnValue -ne 0) { throw "Win32_Process.Create failed with $($r.ReturnValue) for: $CommandLine" }
$p = Get-Process -Id $r.ProcessId -ErrorAction SilentlyContinue
if (-not $p) { "started pid $($r.ProcessId) (already exited)"; return }
if (-not $p.WaitForExit($TimeoutSeconds * 1000)) { throw "pid $($r.ProcessId) still running after ${TimeoutSeconds}s" }
"pid $($r.ProcessId) exit code $($p.ExitCode)"
