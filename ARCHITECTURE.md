# Architecture 0.1.29

WPF MainWindow -> MainViewModel -> ChatStore/ProjectStore/ConversationEngine -> Ollama REST.

Existing `history.db` remains schema version 1. New `Projects/projects.sqlite` uses tables `projects` and `chat_projects`; no automatic AI memory extraction. This separation preserves previous client compatibility. Project notes/context are user supplied, bounded to 4000 characters per field and added only for project conversations. Regeneration reuses the original prompt and attachments, omits its previous assistant response from the model context, and appends a new assistant message while retaining prior files.

The artifact toolchain remains limited and validated. CSV stress fixtures are profiled over all rows under the bounded local ingestion limits; arbitrary row retrieval remains future work. No arbitrary filesystem/tool execution is granted to the model.

## 0.1.28 tabular data pipeline

Import is isolated in child process. XLSX/CSV rows are inspected deterministically and projected into small analytics profiles; only these aggregates and up to 75 raw XLSX rows (small sheets) enter the cloud context. Native financial sums use decimal arithmetic; cached formula values may be used but original formulas are never executed. The complete source file remains in private attachment storage. Missing post-hoc row retrieval is disclosed. Unbounded cloud prompts, automatic external fetch and unlimited decompression remain disallowed.


## 0.1.28 Windows compatibility

The installer and WPF both accept Windows NT 10.0 build 10240+; the old 22000 gate is removed. On Win11 Arm64 the x64 build is enabled through x64compatible; native dependency verification is pending. Official .NET 10 support remains restricted to Microsoft-listed LTSC/Enterprise Windows 10 releases. The installer being permissive does not prove OS/runtime compatibility. Runtime smoke-test is mandatory on Windows owner build; target OS acceptance is separate.

## 0.1.29 reliability

ArchiveReplay stages an immutable ZIP under its chat and verifies hashes before replay, including older Staging records. UI cancellation reaches archive expansion and import workers. The worker receives the parent-owned data root through its child-only environment. Document schema validation rejects null structure and wrong JSON types before writes. Runtime identifiers and dependency locks remain stable through repeated builds and package inventory. Source updates select ordinary/Hotfix1 baselines by the old manifest hash; installed upgrades back up first, remove only known retired files after successful Setup, and quarantine verified partial state during rollback.
