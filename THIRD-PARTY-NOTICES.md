# Third-party notices — Synaflow Printer Helper

The Synaflow Printer Helper itself is **not** open source — see [LICENSE](LICENSE).
It is built with, or works together with, the components below. Each remains under its
own licence.

## Included in the release executable

The release `SynaflowPrinterHelper.exe` is a self-contained single-file .NET 8 application.

| Component | Version | Licence | Source |
|---|---|---|---|
| .NET 8 runtime and base class libraries | 8.0 | MIT | https://github.com/dotnet/runtime — third-party notices: https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT |
| System.Drawing.Common | 8.0.8 | MIT | https://www.nuget.org/packages/System.Drawing.Common/8.0.8 |

## Not included — used from the PC or downloaded separately

| Component | Licence | How it is used |
|---|---|---|
| SumatraPDF 3.5.2 | GPLv3 (parts BSD) — https://github.com/sumatrapdfreader/sumatrapdf | **Not bundled.** If it is not already installed, the helper downloads the official build from sumatrapdfreader.org into `%LOCALAPPDATA%\SumatraPDF\` and starts it as a separate program to print PDF files. The helper does not modify or link SumatraPDF. |
| Google Chrome / Microsoft Edge | Their vendors' terms | **Not bundled.** The browser already installed on the PC is started in headless mode to turn label pages into PDF. |
| Windows printer drivers | Their vendors' terms | Printers the shop has already installed in Windows. |

## MIT licence text (applies to the MIT components above)

```
Copyright (c) .NET Foundation and Contributors

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
