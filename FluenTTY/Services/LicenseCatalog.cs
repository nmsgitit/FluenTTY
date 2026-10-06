using System.Reflection;

namespace FluenTTY.Services;

/// <summary>One component whose license is shown in About › Licenses and in THIRD-PARTY-NOTICES.md.</summary>
public sealed record LicenseEntry(
    string Name,
    string Version,
    string License,
    string Copyright,
    string Url,
    Func<string> Text,
    string? Note = null)
{
    public string Subtitle => $"{Version}  ·  {License}";
}

/// <summary>Licensing information for FluenTTY and everything it ships with.</summary>
public static class LicenseCatalog
{
    public const string AppLicense = "MIT";
    public const string AppCopyright = "Copyright (c) 2026 FluenTTY authors";

    public static string MitText(string copyright) =>
$@"MIT License

{copyright}

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the ""Software""), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED ""AS IS"", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.";

    private static string Resource(string name)
    {
        var asm = Assembly.GetExecutingAssembly();
        var full = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(name, StringComparison.OrdinalIgnoreCase));
        if (full == null) return $"(license text '{name}' not found)";
        using var stream = asm.GetManifestResourceStream(full)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static readonly LicenseEntry App = new(
        "FluenTTY", AppInfo.Version, AppLicense, AppCopyright, "",
        () => MitText(AppCopyright));

    public static readonly IReadOnlyList<LicenseEntry> ThirdParty = new[]
    {
        new LicenseEntry("SSH.NET", "2026.0.0", "MIT", "Copyright (c) 2012-2025 RENCI, Oleg Kapeljushnik, Gert Driesen and contributors",
            "https://github.com/sshnet/SSH.NET", () => MitText("Copyright (c) 2012-2025 RENCI, Oleg Kapeljushnik, Gert Driesen and contributors"),
            "SSH-2 client: shell, SFTP, port forwarding, key authentication (including PuTTY .ppk keys)."),
        new LicenseEntry("BouncyCastle.Cryptography", "2.7.0", "MIT", "Copyright (c) 2000-2024 The Legion of the Bouncy Castle Inc. (https://www.bouncycastle.org)",
            "https://github.com/bcgit/bc-csharp", () => MitText("Copyright (c) 2000-2024 The Legion of the Bouncy Castle Inc. (https://www.bouncycastle.org)"),
            "Cryptography used by SSH.NET (key exchange and key formats)."),
        new LicenseEntry("FluentFTP", "55.0.0", "MIT", "Copyright (c) 2015 Robin Rodricks and FluentFTP Contributors",
            "https://github.com/robinrodricks/FluentFTP", () => MitText("Copyright (c) 2015 Robin Rodricks and FluentFTP Contributors"),
            "FTP and FTPS client."),
        new LicenseEntry("AvalonEdit", "6.3.1.120", "MIT", "Copyright (c) AvalonEdit Contributors",
            "https://github.com/icsharpcode/AvalonEdit", () => MitText("Copyright (c) AvalonEdit Contributors"),
            "The embedded text editor for remote files (syntax highlighting, editing)."),
        new LicenseEntry("System.IO.Ports", "10.0", "MIT", "Copyright (c) .NET Foundation and Contributors",
            "https://github.com/dotnet/runtime", () => MitText("Copyright (c) .NET Foundation and Contributors"),
            "Serial (COM port) access."),
        new LicenseEntry(".NET Runtime, WPF and Windows Forms", "10.0", "MIT", "Copyright (c) .NET Foundation and Contributors",
            "https://github.com/dotnet/wpf", () => MitText("Copyright (c) .NET Foundation and Contributors"),
            "The self-contained .NET runtime, Windows Presentation Foundation (including the Fluent theme) and Windows Forms (hosting the Remote Desktop control)."),
        new LicenseEntry("PuTTY (reference only)", "0.85", "MIT", "Copyright 1997-2026 Simon Tatham and the PuTTY contributors (see the full notice)",
            "https://www.chiark.greenend.org.uk/~sgtatham/putty/", () => Resource("PuTTY-LICENCE.txt"),
            "FluenTTY's terminal behaviour, key sequences, settings and saved-session import follow PuTTY's documented behaviour and source. No PuTTY code is compiled into FluenTTY; the notice is included because its design was used as a reference."),
        new LicenseEntry("Remote Desktop ActiveX control", "Windows", "Windows component", "Copyright (c) Microsoft Corporation",
            "https://learn.microsoft.com/windows/win32/termserv/remote-desktop-activex-control", () => "The Remote Desktop ActiveX control (mstscax.dll) is part of Windows and is used under the Windows license. It is not distributed with FluenTTY.",
            "Used for RDP sessions; loaded from Windows at run time."),
    };
}
