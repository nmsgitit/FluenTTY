# Third-party notices

FluenTTY is released under the MIT License (see `LICENSE`). It is built on the open-source
components below, each used under its own license. The full license texts follow the table.
The same information is available in the app under **About › Licenses**
(`FluenTTY/Services/LicenseCatalog.cs` — keep the two in sync).

| Component | Version | License | Copyright | Source |
|---|---|---|---|---|
| SSH.NET | 2026.0.0 | MIT | 2012-2025 RENCI, Oleg Kapeljushnik, Gert Driesen and contributors | https://github.com/sshnet/SSH.NET |
| BouncyCastle.Cryptography | 2.7.0 | MIT | 2000-2024 The Legion of the Bouncy Castle Inc. | https://github.com/bcgit/bc-csharp |
| FluentFTP | 55.0.0 | MIT | 2015 Robin Rodricks and FluentFTP Contributors | https://github.com/robinrodricks/FluentFTP |
| AvalonEdit | 6.3.1.120 | MIT | AvalonEdit Contributors | https://github.com/icsharpcode/AvalonEdit |
| System.IO.Ports | 10.0.12 | MIT | .NET Foundation and Contributors | https://github.com/dotnet/runtime |
| .NET Runtime, WPF, Windows Forms | 10.0 | MIT | .NET Foundation and Contributors | https://github.com/dotnet/runtime, https://github.com/dotnet/wpf |

FluenTTY also uses components of Microsoft Windows that are not redistributed with it: the
Remote Desktop ActiveX control (`mstscax.dll`, used for RDP sessions under the Windows license)
and the Segoe Fluent Icons font.

## PuTTY (design reference)

FluenTTY's terminal behaviour, key sequences, settings and saved-session import follow PuTTY 0.85's
documented behaviour and source. No PuTTY code is compiled into FluenTTY; its notice is reproduced
below because its design was used as a reference.

```
PuTTY is copyright 1997-2026 Simon Tatham.

Portions copyright Robert de Bath, Joris van Rantwijk, Delian
Delchev, Andreas Schultz, Jeroen Massar, Wez Furlong, Nicolas Barry,
Justin Bradford, Ben Harris, Malcolm Smith, Ahmad Khalifa, Markus
Kuhn, Colin Watson, Christopher Staite, Lorenz Diener, Christian
Brabandt, Jeff Smith, Pavel Kryukov, Maxim Kuznetsov, Svyatoslav
Kuzmich, Nico Williams, Viktor Dukhovni, Josh Dersch, Lars Brinkhoff,
and CORE SDI S.A.

Permission is hereby granted, free of charge, to any person
obtaining a copy of this software and associated documentation files
(the "Software"), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge,
publish, distribute, sublicense, and/or sell copies of the Software,
and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be
included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
NONINFRINGEMENT.  IN NO EVENT SHALL THE COPYRIGHT HOLDERS BE LIABLE
FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF
CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

---

## MIT License

Applies to SSH.NET, BouncyCastle.Cryptography, FluentFTP, AvalonEdit, System.IO.Ports and the .NET
runtime components, each with the copyright holder listed in the table.

```
Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
