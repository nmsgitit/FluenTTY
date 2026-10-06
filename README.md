# FluenTTY

A tabbed remote-management client for Windows 10 and 11, built with Microsoft's Fluent 2 design.

- **Terminals:** SSH, Telnet, Rlogin, Rsh, raw TCP and serial (COM) consoles, with an xterm-compatible
  terminal (256 colours and true colour, mouse, alternate screen, Unicode, logging).
- **Files:** SFTP and FTP/FTPS browsing. Double-click a file to edit it in the built-in editor
  (syntax highlighting, find and replace); saving uploads it again. You can also open a file in any
  local app and changes are uploaded back.
- **Remote desktops:** Remote Desktop (RDP), VNC and XDMCP, each with full-screen and pop-out.
- **PuTTY compatible:** terminal, keyboard, SSH, proxy and serial options follow PuTTY 0.85. It
  imports PuTTY saved sessions and Default Settings, plus PuTTY Connection Manager databases,
  including folders, login macros and post-login commands. PuTTY command-line flags (`-ssh`, `-P`,
  `-l`, `-load`, `-serial`…) and `ssh://`/`telnet://` links work too.
- **Fast to reach:** press Ctrl+Shift+P to search saved sessions or type `user@host:port`. Tabs can
  be dragged, torn off into their own window, and duplicated. A sidebar organises sessions into folders.
- **Portable settings:** export and import settings by category (appearance, sessions, known hosts…).
  Saved passwords can go into an export, encrypted with a passphrase you choose.
- **Crab screensaver:** like MobaXterm's penguins, little Claude Code crabs scuttle over idle terminals
  (and can keep SSH/Telnet sessions alive meanwhile). Configure or turn it off in Settings › Screensaver.

## Download

Get the latest version from the [Releases page](../../releases/latest).

| | |
|---|---|
| `FluenTTY-Setup-<version>.exe` | Installs for your account without admin rights; you can choose all users instead. It adds FluenTTY to Start and search, and you uninstall it from Settings › Apps or Control Panel › Programs and Features. |
| `FluenTTY-Portable-<version>.exe` | A single exe that runs anywhere. It keeps its settings in a `FluenTTY Data` folder next to itself and writes nothing to the registry. |

Both are self-contained, so no .NET install is needed.

**Updates:** from version 1.2.0 on, FluenTTY checks for a new release when it starts and offers to install it
(installed and portable copies alike). Your settings and saved sessions are kept and backed up first. You can
turn the check off in Settings › Updates. What changed in each version is in [CHANGELOG.md](CHANGELOG.md).

## Build

Requires the .NET 10 SDK and, for the installer, Inno Setup 6 (`winget install JRSoftware.InnoSetup`).

```
dotnet build FluenTTY\FluenTTY.csproj
powershell -File tools\build-installer.ps1
powershell -File tools\build-portable.ps1
```

Outputs go to `dist\`. `powershell -File tools\publish-release.ps1` builds both and publishes them as a GitHub release
(bump `<Version>` in `FluenTTY.csproj` and add a `CHANGELOG.md` section first). Developer notes (architecture, testing, gotchas) are in `CLAUDE.md`.

## License

MIT (see `LICENSE`). Third-party components and their licenses are listed in
`THIRD-PARTY-NOTICES.md` and in the app under About › Licenses.
