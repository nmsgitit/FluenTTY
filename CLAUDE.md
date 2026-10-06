# FluenTTY — notes for Claude

A tabbed remote-management client for Windows in Microsoft's Fluent 2 style: SSH, SFTP, Telnet, Rlogin,
Rsh, raw TCP and serial terminals; FTP/FTPS/SFTP file browsing with an embedded editor; RDP, VNC and
XDMCP graphical sessions. Behaviour and settings follow **PuTTY 0.85** (reference source in `PuTTY\`,
MIT, *not* compiled in). Tabby (github.com/eugeny/tabby) inspired the connect palette and settings.
Sibling apps with the same look and tooling: `..\FluentPlay` (icons, installer, About layout), FluxCap.

## Build, run, ship

```
dotnet build FluenTTY\FluenTTY.csproj                      # Debug → FluenTTY\bin\Debug\net10.0-windows\win-x64\FluenTTY.exe
powershell -File tools\build-installer.ps1                 # → dist\FluenTTY-Setup-<ver>.exe   (Inno Setup 6)
powershell -File tools\build-portable.ps1                  # → dist\FluenTTY-Portable-<ver>.exe (single file)
```

- **Versioning — every change gets a new version.** Bump `<Version>` in `FluenTTY.csproj` (major.minor.patch:
  minor for features, patch for fixes) and add a `## <version> — <date>` section to `CHANGELOG.md` in plain,
  user-facing words (what people notice, not how it's built; a few bullets). The app shows it after updates
  and it becomes the GitHub release notes; a ProtocolTests check fails if the section is missing.
- **GitHub:** public repo https://github.com/nmsgitit/FluenTTY (`main`), which is also `Updater.Repository`.
  Commit as `nmsgitit <338382383+nmsgitit@users.noreply.github.com>` (repo-local git config; never a real
  email). `PuTTY\` is git-ignored: a local reference only. Commit and push each change with its version bump.
- **Releasing:** `powershell -File tools\publish-release.ps1` (needs `gh auth login`) builds both editions,
  writes `SHA256SUMS.txt` and creates release `v<version>` on GitHub with the changelog section as notes.
  Push the code (`git push`) first. Never re-use or delete a published version number; release a new one.
- .NET 10 WPF, `net10.0-windows` (plain: **no WinRT APIs** — the 19041 TFM added a 53 MB projection DLL).
  WinForms is enabled only to host the RDP ActiveX control (`AxHost` in a `WindowsFormsHost`).
- `nuget.config` is required: this machine has no global NuGet source.
- Version lives in `FluenTTY.csproj` `<Version>`; both scripts read it.
- **Installer** (`installer\FluenTTY.iss`): per-user by default (`PrivilegesRequired=lowest`), with Inno's
  own install-mode dialog (`PrivilegesRequiredOverridesAllowed=dialog`). Known cosmetic issue: after
  "Install for all users" Inno keeps that dialog on screen until the elevated install ends. Don't try to
  replace it with a [Code] page that relaunches setup: on Inno Setup 6.7 Setup's own process gets
  ERROR_ACCESS_DENIED (5) starting `{srcexe}` via ShellExec *and* Exec (tested; that attempt broke
  all-users installs and was reverted). **Never change the AppId** `{6E0D2A57-3C4B-4E8F-9A61-2B7D5F1C8E34}`. Uninstall
  refuses while the app runs (mutex `Local\FluenTTY.Instance`) and only offers to delete
  `%AppData%\FluenTTY` in interactive uninstalls. Optional tasks: desktop icon, ssh/telnet/sftp URL handlers.
- **Portable** (`-p:Portable=true` → `PORTABLE` define, separate `obj\portable\`): uncompressed single file
  (compressed self-extractors trip EDR), data in `FluenTTY Data\` beside the exe (falls back to
  `%LocalAppData%\FluenTTY Portable` if read-only), its own mutex so it can run beside an installed copy.
- The first launch of a freshly built exe takes 5–25 s (Defender scan); later launches ~1 s. Scripts
  must wait for the window in a loop, never a fixed sleep.
- `tools\make-icon.ps1` regenerates `Assets\FluenTTY.ico/.png` (FluentPlay's blue tile with a white `>_`).

## Layout

| Folder | What |
|---|---|
| `Models\` | `AppSettings` (sections mirror PuTTY's config panels; PuTTY `conf.h` names in comments), `SessionProfile` (+ per-session overrides, `LoginScript`), enums, colour schemes |
| `Services\` | `SettingsStore` (JSON load/normalise/debounced save), `AppInfo` (data folder, portable), `SecretStore` (DPAPI; AES-GCM+PBKDF2 for exports), `SettingsTransfer` (export/import by category), `PuttyImport` (registry sessions + Default Settings), `PcmImport` (PuTTY Connection Manager XML), `CommandLine` (PuTTY flags + URLs), `SingleInstance` (mutex + named pipe), `Updater` + `Changelog` (GitHub releases, what's new), `Screensaver`, `ThemeService`/`WindowBackdrop` (Mica, animations), `LicenseCatalog` |
| `Terminal\` | Own VT/xterm emulator: `TerminalEmulator` (DEC parser, CSI/OSC/DCS, alt screen, mouse modes…), `TermBuffer` (cells, scrollback), `TerminalCanvas` (GlyphRun rendering, selection, input), `KeyEncoder` (PuTTY's `format_*_key`), `CharWidth`, `Palette`, `SessionLogger` |
| `Connections\` | `ITerminalBackend` + `IConnectionUi` (in-terminal prompts, host-key dialog); SSH (SSH.NET), Telnet/Rlogin/Rsh/Raw (`TcpBackends`), serial, proxies (`SocketConnector`: SOCKS4/4a/5, HTTP) |
| `Files\` | `IRemoteFileSystem` for SFTP and FTP (FluentFTP), `FileBrowserView`, `EditorTab` (AvalonEdit) + `FindBar` |
| `Remote\` | `VncClient` (RFB 3.3–3.8, Raw/CopyRect/RRE/Hextile/ZRLE), `RdpTab` (MsTscAx), `XdmcpTab` (external X server embedded via `SetParent`), `RemoteToolbar` |
| `Views\` | `MainWindow` tabs (`SessionTab` base), `TerminalTab`, `StartTab`, `ConnectPalette` (Ctrl+Shift+P), `SessionsSidebar`, `SessionEditorWindow`, `OnboardingWindow`, `ImportWindow`, `AboutWindow`, dialogs |
| `Settings\` | Windows-11-style settings: `SettingsCatalog` (every page and card), `SettingItems` (card types), `SettingsEditors` (lists, schemes, fonts…), `SettingsWindow` (nav, search) |
| `tests\` | `TestServers` (localhost SSH/SFTP/Telnet/Raw/FTP/VNC) and `ProtocolTests` (headless checks) |
| `tools\dev\` | `ui.ps1` (UI Automation driver), `screenshot.ps1` (PrintWindow), `run-unpackaged.ps1` |

Adding a setting: field in `AppSettings` (with the PuTTY name) → card in `SettingsCatalog` → use it (often
via `TerminalConfig.BuildOptions/BuildLook`) → if it's per-session overridable, `SessionProfile` too →
check `SettingsTransfer` categories and `PuttyImport` mapping.

## Testing (do it in the sandbox, clean up afterwards)

Always point the app at a scratch data folder so the user's real settings are untouched:
`$env:FLUENTTY_DATA_DIR = "<scratch>\data"` (honoured by every build). `FLUENTTY_LOG=<file>` writes a
diagnostic log.

1. Servers: `dotnet run --project tests\TestServers -- <scratch>\servers` — localhost only, user/password
   `test`/`test` (VNC password `test`): SSH+SFTP 2222, Telnet 2323, Raw echo 2324, FTP 2121 (no TLS,
   like a plaintext-only switch), VNC 5959. Files are re-seeded under `<scratch>\servers\files` on every
   start. Hostile fixtures for the security tests: SFTP serves a virtual `/evil` tree (traversal names,
   a rooted `C:\…` name, a right-to-left-override name, endless `loop` nesting — escape targets stay
   inside `<scratch>\servers`), and VNC port 5960 offers "no authentication" alongside VNC auth.
   Windows Defender quarantines a file saved with the RLO name (Trojan:Win32/Artoelo) — only the old,
   vulnerable code does that; the text content is harmless.
2. Headless: build `tests\ProtocolTests`, run `ProtocolTests.exe <scratch>\servers\files` with
   `FLUENTTY_DATA_DIR` set. 131 checks (emulator, keys, SSH/SFTP/Telnet/Raw/FTP/VNC, PuTTY/PCM import,
   export/import, and `SecurityTests.cs` — the security-review regressions, prefixed `SEC`). All must pass.
   Don't run it while FluenTTY is open (the single-instance test needs the mutex).
   The user may have their own copy running (installed, or a portable with a `FluenTTY-Portable-*`
   process name). Set `"singleInstance": false` in the scratch settings.json or your launch is handed to
   *their* window (it opens a tab there and exits), and expect the M5 check to report "skipped" (it refuses
   to probe someone's live instance). Stop only processes you started (by PID), and drive only those:
   **always pass `-ProcessId <pid>` to `tools\dev\ui.ps1`** — without it, it searches every process named
   FluenTTY, including the user's own installed copy.
   End-to-end update test without touching the user's install: `dotnet publish -c Debug -p:Portable=true -o
   <scratch>\e2e`, seed `<scratch>\e2e\FluenTTY Data\`, run with `FLUENTTY_PRETEND_VERSION=<older>` and
   invoke Update now / Restart now by PID; check the exe hash equals the release digest and the data survived.
   Don't test the installer update on this machine: the user has an all-users install in Program Files.
3. UI: launch the Debug exe, drive it with `tools\dev\ui.ps1` (by accessible name), capture with
   `tools\dev\screenshot.ps1 -Hwnd <hwnd from 'ui.ps1 windows'> -Out x.png`, then look at the PNG.
   Type into a terminal with `ui.ps1 settext -Name Terminal -Value "ls`n"` (the terminal's automation
   peer exposes ValuePattern: Value = screen text, SetValue = keystrokes). Give new controls an
   `AutomationProperties.Name` so they can be driven; names must be unique enough (`ui.ps1` takes the
   first match — e.g. the editor's find box is "Find what" because the toolbar button is "Find"; add
   `-Type Button` when a label shares the name). Also `addselect` (multi-select rows) and `state`
   (IsEnabled / HasKeyboardFocus). Common Win32 dialogs (folder/file pickers, Inno's task dialog) don't
   expose their edit boxes to UIA: send messages to those specific controls instead (`WM_SETTEXT` to the
   edit, `BM_CLICK` to the button, `TDM_CLICK_BUTTON` to a task dialog) — targeted window messages, never
   global input. Background `Start-Job`s die when the PowerShell call ends, so open a modal dialog and
   complete it within one call.
4. Installer: run setup/uninstall **through `tools\dev\run-unpackaged.ps1`** (a shell started by the
   Claude desktop app has a package identity; HKCU writes would be virtualised and never reach
   Programs and Features). Silent per-user: `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CURRENTUSER`.
   Verify the HKCU `Uninstall\{AppId}_is1` key, `Get-StartApps`, then uninstall and verify removal.
   Inno's wizard controls are VCL windows UIA can't see: list them with EnumChildWindows and click with
   `BM_CLICK` (post it — `SendMessage` blocks while the click opens a modal box). Inno's install-mode
   dialog is a task dialog: `TDM_CLICK_BUTTON` (0x466) with the command-link id (6 = for me only).
   Never approve a UAC prompt for the user — the "all users" path is theirs to test. Big setups take
   ~10 s before the wizard process appears; wait for the `TWizardForm` window in a loop.
   (A PowerShell hook blocks commands that contain both `Remove-Item` and a `/VERYSILENT`-style argument;
   run cleanup and setup in separate calls.)
5. Clean up: stop FluenTTY and TestServers, delete scratch folders, `bin`/`obj` of the app and tests,
   `%TEMP%\FluenTTY`.

**Never synthesise real keyboard/mouse input** (SendKeys, `keybd_event`, clicking by coordinates): if
focus is wrong the keystrokes land in the user's other windows — this once typed into the user's chat.
Use UI Automation patterns only.

## Security invariants (from the security review — do not regress)

Threat model: a malicious or compromised server, a network man-in-the-middle, a tampered file someone
imports (settings export, PuTTY CM database, shared portable data folder), another user on the same PC.
Each rule below is guarded by a `SEC` test in `tests\ProtocolTests\SecurityTests.cs`; if you change the
code it names, keep the test passing and extend it rather than loosening it.

- **H1 — server names never become paths.** Every remote file/folder name that reaches the local disk
  goes through `Files\LocalPaths` (`SafeFileName`, `ChildPath`, `DownloadTree`): last path segment
  only, no `\` `/` `:` control or bidi characters, device names defused, and the final full path must
  be inside the destination folder. Recursive downloads don't follow symlinked folders and stop at
  `MaxTreeDepth` / `MaxTreeFiles` / already-visited folders. Never write `Path.Combine(folder, entry.Name)`.
- **M1 — opening a downloaded file.** "Open with default app" asks first when `LocalPaths.IsRunnable`
  (programs, scripts, shortcuts, installers, disk images) and offers the editor instead. Everything
  downloaded gets Mark-of-the-Web (`LocalPaths.MarkFromInternet`, ZoneId=3, sanitized ASCII HostUrl).
- **M2 — host keys.** A key of *another type* on record for host:port is a changed key
  (`HostKeyInfo.Mismatch` + `PreviousAlgorithm`), never a first contact (`SshConnector.VerifyHostKey`).
- **M3 — dangerous confirmations default to Cancel.** The changed-host-key dialog uses
  `FluentDialog.MakeCancelDefault(armAfter)`: Cancel focused and default, accept buttons disabled for
  1.5 s. Use it for any future "trust this / run this" confirmation that can pop up mid-typing.
- **M4 — FTP never silently downgrades.** "Auto" = explicit TLS; if the server can't do TLS
  (`FtpSecurityNotAvailableException`) the user must agree to plaintext (anonymous logins excepted).
  Never use FluentFTP's own `FtpEncryptionMode.Auto` (it falls back silently).
- **M5 — single-instance pipe.** Both ends use `PipeOptions.CurrentUserOnly`, and the name includes the
  session ID. Command lines can carry `-pw` passwords; never relax this.
- **M6 — imported configuration is untrusted.** `SettingsTransfer.FindRisks` lists values that run
  programs, write files, trust keys, weaken encryption, redirect traffic or read keys from network
  shares; `Import` applies them only when approved (`approvedRisks`), otherwise the current value is
  kept. **Adding a setting that names a program, a path to write, a trust decision or a security
  downgrade? Add it to `FindRisks` + `Neutralize` and to the test.** PuTTY CM import drops UNC key
  paths (NTLM leak). XDMCP hosts must pass `XdmcpTab.IsSafeHost` (they go onto the X server command
  line). A portable copy whose data folder is on a network share warns at startup.
- **M7 — VNC.** Prefer VNC auth over "None"; the local clipboard is sent only on an explicit paste
  (Ctrl+V / Shift+Insert / menu), never on tab activation.
- **L5 — native DLLs.** `AssemblyInfo.cs` sets `[DefaultDllImportSearchPaths(System32)]`; every
  `[DllImport]` must name a Windows system DLL.
- **L8 — logging.** Login-script sends whose text contains `{password}` are logged as "sent secret"
  even when the step isn't marked secret. Never put secrets in `Status`/`Diagnostics.Log`.
- **U1 — updates.** `Updater.Repository` is a compile-time constant (never a setting: imports must not be
  able to redirect updates). Only non-draft, non-prerelease releases newer than the running version are
  offered; download links must point into that repository's `/releases/download/`; the file must match the
  release's SHA-256 (GitHub asset digest or `SHA256SUMS.txt`) and size, or it isn't used. Nothing is downloaded
  without the user's click. Before applying, user data is flushed and copied to `<data>\Backups\` (newest 5);
  no backup → no update. The installer upgrade never touches `%AppData%\FluenTTY`; the portable update only
  swaps the exe (rename running exe → `.old`, new exe under the same name, `--after-update <pid> <old>` makes
  the new one wait for the old process to exit, then deletes `.old`). Turning update checks off via an import
  is an M6 risk. The update-state file (`update-state.json`: last run version, skipped version) is outside
  settings exports on purpose. Verified update files deliberately get no Mark-of-the-Web (unlike M1 downloads).
- **Cleartext badge.** `SessionTab.IsUnencrypted` (Telnet, Rlogin, Rsh, raw, VNC, XDMCP; FTP without
  TLS) shows an open padlock on the tab. New protocols must set it correctly.
- Already hardened before the review (keep it that way): terminal never answers clipboard reads, title
  queries return empty, remote resize ignored, OSC buffers capped, pastes stripped of control
  characters and the bracketed-paste terminator, XML import rejects DTDs, port forwards bind to
  loopback by default, DPAPI with app entropy for saved passwords, recents never keep passwords.
- Open (Low) items not yet fixed: VNC server-size/zlib caps, single-line paste with trailing newline,
  a dedicated `--url` handler entry, `-pw` persisted by "restore last sessions", export PBKDF2
  strength (210k) and plaintext fallback in `SecretStore.Unprotect`, RDP "connect anyway" warning,
  unfiltered SSH banners/prompts.

## Gotchas (each cost real time)

- **Encoding / icon glyphs**: many files contain literal Segoe Fluent Icons private-use characters
  (they look like empty strings in most views). Search-and-replace tools may not match them; edit
  around them, and in new code write C# escapes (backslash, `u`, then the hex code, e.g. E70F) instead of pasting the character. Files are UTF-8; keep them that way (`-Encoding utf8` when writing from PowerShell).
- **Caption buttons**: the main window uses `WindowChrome` with `UseAeroCaptionButtons=True` and native
  DWM min/max/close; `CaptionSpacer` is sized from `DWMWA_CAPTION_BUTTON_BOUNDS`. Custom caption buttons
  drew on top of DWM's own (doubled buttons) — don't reintroduce them. DWM paints those buttons *under*
  the client area, so the title row must never get an opaque background — including when Mica is off
  (Windows 10, transparency effects off, remote sessions, backdrop "None"): `WindowBackdrop.Apply(...,
  dwmCaptionButtons: true)` keeps the surface transparent and sets `DWMWA_CAPTION_COLOR` to the Fluent
  background, and `MainWindow.BackdropApplied` paints only `BodyBackground` (row 1). Test that path with
  `FLUENTTY_NO_BACKDROP=1`; `tools\dev\screenshot.ps1` (PrintWindow) shows the buttons faithfully.
- **Alt key**: a lone Alt tap puts WPF windows into menu mode and eats the next key; `WndProc` swallows
  `SC_KEYMENU` like PuTTY (`MainWindow.SuppressAltMenu`, also in `PopOutWindow`). Alt+Space is a setting.
- **Terminal cells**: a zeroed `Cell` means *palette colour 0*, not "default". `TermLine` always fills
  with `Cell.Blank(erase)`; any new buffer code must do the same or you get two background shades.
- **Airspace**: RDP and XDMCP content are native HWNDs and always paint over WPF. Anything overlaid on a
  tab (the connect palette) must call `SessionTab.SetOverlayOpen(true)` so those tabs hide their host.
- **RDP control**: create MsTscAx by CLSID with a creatability probe (`RdpTab.FindControlClsid`, newest
  → oldest); v13 exists in the registry on some PCs but fails with CLASS_E_CLASSNOTAVAILABLE. Events come
  through `IConnectionPoint` with DISPIDs from mstscax's type library. WinForms thread exceptions are
  routed to FluenTTY's error dialog (`App.OnStartup`).
- **Moving tabs between windows** (pop out, tear off, return to tabs): the RDP ActiveX control must never
  be re-parented into another top-level window — it keeps the old window as its frame, focusing it
  re-activates that window, and the two windows fight over activation (glitchy, unusable pop-out).
  `SessionTab.BeforeMove`/`AfterMove` bracket every move (`MainWindow.PopOut`/`Adopt`, `PopOutWindow`);
  `RdpTab` tears the control down and connects a new one in the new window (the server keeps the
  session). Its event sink carries a generation so late events from the old control are ignored.
  Any future tab hosting a native control must do the same.
- **Title bar / cards**: every title-bar control is `TabHeader.BarHeight` (32) tall and vertically centred
  (`TitleBarButton` style), so tabs, their close buttons, new tab/connect and About/Settings share one centre
  line. The selection plate floats (rounded, inset `PlateInset`), it doesn't join the content card. The
  sidebar and tab host are separate rounded cards (`MainWindow.ApplySidebar`; none in full screen) and
  `TabHost.Clip` follows the rounded corner (RDP/X HWNDs ignore it). Narrow tabs: below 90 px the close
  button shows only on hover/selected; below ~64 px a tab shows one centred thing (close or icon); at the
  36 px minimum the strip scrolls (`TabScroller`, wheel + bring-selected-into-view) instead of overflowing
  under About/Settings.
- **Crab screensaver** (MobaXterm's penguin "consolesaver", terminal tabs only): `Services\Screensaver`
  tracks input via `InputManager.PreProcessInput` (mouse moves count only when `GetCursorPos` changes —
  WPF fakes moves when content moves under the pointer), eats the waking key/click and its text/mouse-up,
  and raises `KeepaliveDue` each minute (`ITerminalBackend.SendKeepalive`: SSH ignore msg, Telnet NOP; false
  for Rlogin/Rsh/raw/serial). `Views\CrabOverlay` (one per TerminalTab, over the canvas) walks crabs on
  `TerminalCanvas.GetOccupiedCells`. Sprite = Claude Code's banner art with 1:2 pixels (half-block cells).
  UIA `settext`/`invoke` don't count as input, so the idle timer runs during UI tests; Settings ›
  Screensaver › Preview starts it on demand. It deliberately ignores the Animations setting (own toggle).
- **Updates / what's new** (`Services\Updater`, `Services\Changelog`, `Views\UpdateUi`): the startup check
  runs 3 s after load and only ever shows the bar above the cards (no modal mid-typing). Applying happens
  inside `MainWindow.OnClosing` via `CloseForUpdate`, after unsaved-editor prompts, so a "no" leaves sessions
  intact. The setup is run `/SILENT /FLUENTTYUPDATE=1 /CURRENTUSER|/ALLUSERS` (all users = program folder not
  writable); `FluenTTY.iss` waits for the app mutex and relaunches with `runasoriginaluser`. Test against the
  real release with a Debug build and `FLUENTTY_PRETEND_VERSION=1.0.0` (Debug only) — but a Debug build has no
  `unins000.exe`, so it offers the download page; test real installs with an installed/portable Release build.
  What's new appears when `update-state.json`'s last version is older than the running one (first-ever run with
  onboarding not done = new user = nothing shown).
- **Sessions sidebar**: the TreeView has horizontal scrolling disabled on purpose — parents stay anchored
  at the left and long names trim with "…" (a horizontal offset hid the top-level folders).
- **SSH.NET**: its handshake timeout also covers our host-key dialog, so `ConnectionInfo.Timeout` is 10
  minutes and a fast TCP probe (`SshConnector.ProbeReachable`) gives the real connect timeout. Auth order
  key → keyboard-interactive → password; wrong passwords re-prompt by reconnecting.
- **AvalonEdit's SearchPanel** has fixed 24 px controls that overlap under the Fluent theme (and has no
  replace) — `FindBar` replaces it. Other third-party templates may have similar sizing issues.
- **Settings nav** has virtualisation off so UIA can see every item; the file list's row style must be
  `BasedOn` `GridView.GridViewItemContainerStyleKey` or rows render type names.
- **Login scripts** (PuTTY CM "login macros"): the prompt may already be on screen when the script
  starts, so `StartLoginScript` checks the current cursor line first.
- **PuTTY CM import**: only the XML database format is readable; encrypted `.dat` files are rejected with
  instructions to export unencrypted from PuTTY CM. Passwords are imported only when the user opts in
  (DPAPI-protected).

## Conventions

- UI is mostly built in C# (not XAML) with `SetResourceReference` to Fluent theme brushes
  (`TextFillColorPrimaryBrush`, `CardStrokeColorDefaultBrush`, …) so light/dark switch live. Shared
  styles are in `Styles.xaml` (`IconButton`, `ToolbarButton` with the glyph in `Tag`, `SettingsCard`…).
- Honour `ThemeService.AnimationsEnabled` / `ThemeService.Anim(ms)` for every animation.
- About › Licenses (`LicenseCatalog`) and `THIRD-PARTY-NOTICES.md` must list the same components and
  versions; update both when adding or bumping a package.
- Settings/About icons match FluentPlay: gear ``, info ``.
