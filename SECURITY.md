# Security and privacy model

## Signed updates in 0.1.31

Automatic checks contact only the public GitHub Releases API for AlexFenixQ/OllamaDesktopAssistant, at startup and every six hours when enabled. The stable channel excludes prereleases; beta accepts both channels. Installation requires a user click, waits for active work, and preserves drafts/staged attachments. No history, attachments or Ollama credentials are sent to GitHub.

Update metadata is verified using a pinned RSA public key, SHA-256 and PKCS#1 signatures before installer download/execution. The signed product, version, channel, minimum base, repository URLs, sizes and hashes are validated. The detached signature authenticates the release independently of HTTPS and does not constitute an Authenticode certificate. The private release-signing key is DPAPI-protected outside the repository and release ZIP.

The hidden PowerShell 5.1 worker verifies the installed inventory, rejects unknown files/reparse points, waits for the parent process without terminating operations, creates hash-verified ZIP backups of program files and Data, then runs the same-AppId per-user Inno installer with /VERYSILENT /SUPPRESSMSGBOXES /NORESTART. Installer and installed inventory hashes are rechecked. A failed installation restores program files and installer registration; Data is never replaced during automatic rollback. Backups contain private data and must remain local. The worker is application code, never a model-callable tool.

## Trust boundaries

The model receives the user's request, limited selected conversation history, document text/references and optionally normalized images through HTTPS to the fixed ollama.com origin. Redirects are disabled for API calls. Document content, images, model text, tool parameters and HTTP streams are untrusted. Do not place provider keys or private credentials in a prompt. Only a user's personal API key is stored; no shared key or remote telemetry endpoint is embedded.

Allowlisted tools are decimal calculation and the finite DOCX/XLSX/PPTX/ZIP creation functions, permitted only under an explicit user creation request. Document specifications reject unknown members and enforce dimensions, content lengths and approved image identifiers. Formulas cannot reference other sheets, external books, URLs or macros. No C#/PowerShell/JavaScript interpreter, browser engine, Office COM automation, LibreOffice, Tesseract or shell execution is available to the model. System shell use is restricted to explicit user actions opening a checked generated file, its folder or a confirmed HTTPS link.

The Office preflight checks container sizes, ratios, paths, duplicate names, macro content types and VBA projects. XML DTD and resolution of external entities are disabled in the preflight. The Open XML SDK is used only for reading packages; external relationships are counted and not followed. The application does not invoke PDF JavaScript, form actions, attachments or external content APIs. Markdown is rendered into authored WPF elements, never interpreted as XAML/HTML, and remote Markdown images are not fetched.

Native PDF/image code runs in an import worker under the current Windows account. Windows Job Object memory limit: 1 GiB; parent deadline: 90 seconds; parent kills the worker on cancellation/timeout. ImageMagick policy.xml denies delegates, filters and non-allowlisted coders; it is delivered beside the executable and MAGICK_CONFIGURE_PATH is set before decoding. Correct loading of this policy must be confirmed by Windows acceptance. This worker is **not an AppContainer/security sandbox** and has the same user privileges. Known package vulnerabilities are audited during restore; critical/high advisories fail the build. Dependency and native-code review is not replaced by these controls.

## Local data

Data and program installations are separate. DPAPI CurrentUser encrypts the API key; plaintext strings necessarily exist in process memory while making a request. SQLite messages and local copies of attachments/generated files are not additionally encrypted. DPAPI does not defend against compromised software running as the same user. Application data may remain after uninstall and after import/cancellation; this is deliberate preservation, not secure erasure. There is no hidden sync, telemetry or automatic transfer of unsent files.

Only explicit user deletions remove an owned conversation and its internal copies; Save As copies remain. Do not manually place unrelated files under the owned application-data subdirectories. Paths reject traversal, rooted inputs and reparse points. File actions verify stored hashes before opening/saving generated artifacts. They do not execute macros or arbitrary script files.

## Failure and diagnostics

Network authentication, quota/rate limits, cancellation, malformed streams and interrupted EOF are distinct failures. Content checkpoints preserve incomplete responses and mark their state. Network generation and file writes are not blindly retried. Invalid schema arguments may be corrected in bounded model tool rounds before publication; this spends additional cloud requests. HTTP error bodies, prompts, secrets, model reasoning, attachment text and raw exception messages are not recorded in application diagnostics. The UI error may identify a selected user file; application diagnostic exports exclude that content.

The owner build logs can contain local source paths; the diagnostic script exports only filtered failure lines after path/token redaction, not raw logs. Review the diagnostic ZIP before sharing. Report a problem with that ZIP and a description; do not supply an API key or confidential documents.

## Distribution

Source archive SHA-256 is supplied in the delivery message and an external checksum file; MANIFEST.json covers complete source files, excluding itself. The Windows build obtains a pinned Microsoft SDK using official release-metadata SHA-512 and Microsoft Authenticode validation. Inno's installer is validated against a trusted Authenticode chain and publisher Pyrsys B.V. Native PDFium notices are tied to the same release archive and matching DLL hash. Build products are not signed with an application-publisher certificate in this preview. Global execution policy, antivirus and SmartScreen settings are never disabled.

First-install rollback uses the known version, installed manifest and normal uninstaller, preserving user data. Version 0.1.29 supports verified in-place updates from known predecessors. Program files and HKCU uninstall metadata are backed up before replacement. Source updates record the exact ordinary/Hotfix baseline and support hash-verified rollback. Interrupted installations can be rolled back only when every present program file matches the old or new known inventory; unknown files stop recovery. User data is stored separately and is not migrated or downgraded.

## Audio/video trust boundary (0.1.31)

Media is untrusted data. Header validation and a strict local container whitelist precede probing. The custom LGPL FFmpeg build disables all network protocols and external codecs; runtime permits only the file protocol and never opens playlists. Decoder arguments use ProcessStartInfo.ArgumentList, no shell. FFmpeg runs in a nested Windows Job Object (1536 MiB per process); the media worker has 3072 MiB per-process memory cap, cancellation kills its process tree. This is not an AppContainer sandbox. Stages have bounded logs/timeouts; the entire import deadline is 24h. Resume uses hash-checked originals and validated checkpoint paths. Five-minute chunk completion atomically checkpoints text/metadata. Cancellation can repeat the unfinished chunk, never marks unfinished output complete.

Whisper weights use immutable upstream URLs and pinned SHA-256, temporary download + atomic move; small/medium are local ASR, not Ollama Cloud audio endpoints. Raw media is not uploaded. Report requests transmit transcript text and optionally selected normalized video frames to Ollama only with existing cloud consent. Full transcription does not imply error-free ASR or speaker identification. All report blocks are processed; if budget/decoding/cloud fails the UI reports failure and keeps the full/partial TXT, rather than silently returning a complete report.
