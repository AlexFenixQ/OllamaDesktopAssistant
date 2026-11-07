# Dependency and license review record — 2026-10-25

The self-contained installer redistributes the pinned managed/native dependencies with collected upstream notices. Direct package versions are exact-pinned in the project files. Package metadata/license files are copied into each locally built distribution by Collect-Notices.ps1. The script records transitives and captures available native notices. The owner must review completeness and redistribution conditions before public/commercial distribution; no legal clearance is claimed.

| Component | Pinned version | License/review starting point |
|---|---|---|
| .NET SDK / WPF runtime | SDK 10.0.403 / runtime 10.0.14 | Microsoft distribution LICENSE and third-party notices; https://dotnet.microsoft.com/en-us/platform/free |
| CommunityToolkit.Mvvm | 8.4.2 | MIT; https://www.nuget.org/packages/CommunityToolkit.Mvvm/8.4.2 |
| Microsoft.Data.Sqlite | 10.0.13 | MIT wrapper, SQLitePCLRaw/native notices; https://www.nuget.org/packages/Microsoft.Data.Sqlite/10.0.13 |
| System.Security.Cryptography.ProtectedData | 10.0.13 | Microsoft/.NET license; https://www.nuget.org/packages/System.Security.Cryptography.ProtectedData/10.0.13 |
| DocumentFormat.OpenXml | 3.5.1 | MIT; https://www.nuget.org/packages/DocumentFormat.OpenXml/3.5.1 |
| ClosedXML | 0.105.1 | MIT plus transitives; https://www.nuget.org/packages/ClosedXML/0.105.1 |
| PdfPig | 0.1.16 | Apache-2.0; https://www.nuget.org/packages/PdfPig/0.1.16 |
| bblanchon.PDFium.Win32 | 158.0.8111 | Package, PDFium and compiled third-party licenses are separate; https://www.nuget.org/packages/bblanchon.PDFium.Win32/158.0.8111 and https://github.com/bblanchon/pdfium-binaries/releases/tag/chromium%2F8111 |
| Magick.NET-Q8-AnyCPU | 15.0.0 | Apache-2.0 wrapper; ImageMagick and native codec dependencies have additional notices; https://www.nuget.org/packages/Magick.NET-Q8-AnyCPU/15.0.0 |
| Markdig | 1.5.0 | BSD-2-Clause; https://www.nuget.org/packages/Markdig/1.5.0 |
| Inno Setup | 7.1.0 x64 | Owner build tool, not installed on ordinary users' PCs; review supplied license and current commercial terms: https://jrsoftware.org/isdl.php and https://jrsoftware.org/isorder.php |

SDK/WPF/SQLite/PDFium/ImageMagick required managed/native dependencies are included by the self-contained publish path, with explicit presence checks and native tests. That path remains to be exercised on clean Windows. No font files are bundled; Segoe UI/Consolas are referenced as Windows fonts, not copied. Missing font/renderer differences can affect visual layout, which is not certified by Open XML validation.

Ollama Cloud is a third-party online service with its own account, transmission, payment, model and usage terms. No model weights are distributed. Text model and vision model licensing must not be conflated with a desktop redistribution license. Service/API status, model availability and legal terms can change after the review date.

Sources used for integration:
- https://docs.ollama.com/cloud
- https://docs.ollama.com/api/authentication
- https://docs.ollama.com/api/chat
- https://docs.ollama.com/capabilities/tool-calling
- https://docs.ollama.com/capabilities/vision
- https://docs.ollama.com/capabilities/structured-outputs
- https://ollama.com/api/tags
- https://ollama.com/pricing
- https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json
- https://learn.microsoft.com/en-us/dotnet/core/deploying/

Native audio/video additions: Whisper.net 1.9.1 (MIT), whisper.cpp exact commit f24588a272ae8e23280d9c220536437164e6ed28 (MIT), FFmpeg 9.0.2 (LGPL 2.1+ build with GPL/version3/nonfree disabled), and statically linked MinGW/GCC runtimes under their runtime exceptions. Complete corresponding native source archives, build flags/script and licenses are included under ThirdPartyNotices/Media-native. Speech model weights are downloaded separately under OpenAI Whisper MIT; they are not in the installer. See deployment/MEDIA_LICENSES.md.
