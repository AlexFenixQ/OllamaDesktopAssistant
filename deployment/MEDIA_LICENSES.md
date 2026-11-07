# Native audio/video components

FFmpeg 9.0.2 is used under LGPL 2.1 or later. The exact complete upstream source archive, configuration flags and build script are distributed alongside the binaries. This build disables GPL, version3 and nonfree components and all network protocols. No external codec library is linked. Sources include COPYING.LGPLv2.1 and complete upstream licensing files. FFmpeg is a separate subprocess; the application is not linked to FFmpeg libraries. Users may replace the executables with compatible modified builds; the updater rejects modified installation files to prevent silently overwriting them. Keep modified builds outside the managed installation or use a custom source build and release inventory.

Whisper.net 1.9.1 and whisper.cpp f24588a272ae8e23280d9c220536437164e6ed28 use MIT licenses included in their complete source archives. whisper.cpp builds are shared, with static GCC/MinGW runtimes under their respective runtime exceptions. MinGW-w64 runtime copyright/license is included separately. Build-Native.ps1 applies only the documented target-name patch to the extracted wrapper CMakeLists.txt; archived sources are unmodified.

The separately downloaded multilingual Whisper small/medium ggml models originate from OpenAI Whisper (MIT). Exact immutable model URLs and SHA-256 values are pinned in MediaModels.cs. Model weights are not part of this installer. The included MIT license records the upstream OpenAI copyright; the model-download host is Hugging Face ggerganov/whisper.cpp.

Build compiler: w64devkit 2.11.0 (development only; not shipped to end users). No Visual C++ Redistributable is required by these native builds. AV1 software decoding is provided by static libaom 3.15.1. The native FFmpeg LGPL decoder/parser/demuxer set is enabled, while runtime container/protocol whitelists still reject playlists/network sources. Corrupted, encrypted, unknown/proprietary or resource-exceeding files fail explicitly with originals preserved.


libaom 3.15.1 (official AOMedia release): BSD-2-Clause plus AOM patent license; complete exact source archive and build flags are included. No GPL/nonfree codec dependency is enabled. Build is static, decoder-only and CPU-generic; no GPU or external runtime is required. AOM-LICENSE.txt and AOM-PATENTS.txt are included.

JPEG XR uses the OS-provided Windows WIC codec through WPF in the disposable STA worker; no Windows codec DLL is redistributed. DNG/RAW/HEIF/AVIF/JXL etc use the existing Magick.NET native codec distribution, subject to bundled upstream notices. This product includes DNG technology under license by Adobe.

The owner build can use an existing Git-for-Windows Perl or automatically fetch official Strawberry Perl 5.42.3.1 portable, verified against upstream SHA-256. Perl is a build-time tool, not shipped to ordinary users.
