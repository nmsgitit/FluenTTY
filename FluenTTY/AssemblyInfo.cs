using System.Runtime.InteropServices;

// Native DLLs named in [DllImport] (dwmapi, crypt32, user32, kernel32…) are loaded from System32 only,
// never from the exe's own folder. The portable exe is often run from Downloads, where a planted
// dwmapi.dll or crypt32.dll would otherwise be loaded into FluenTTY. Security review L5 — see CLAUDE.md
// "Security invariants". Every P/Invoke in this assembly must target a Windows system DLL.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
