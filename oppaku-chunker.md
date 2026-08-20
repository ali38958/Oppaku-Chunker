# Oppaku — Advanced Archive & Chunker Utility

> **Type:** DESKTOP / WPF GUI (C# .NET WPF App)
> **Slug:** `oppaku-archive-tool`
> **Created:** 2026-08-18 (Updated: V3 Vision)

---

## Goal

Evolve Oppaku from a standalone file splitter into a **full-fledged advanced archiving tool**. 
While it will support standard archiving features (compression, encryption, password protection, file management), its **unique superpower** remains its zero-space, out-of-order, sparse-file chunking and rebuilding engine — allowing massive archives to be securely transferred across low-capacity drives and rebuilt seamlessly on the target machine without requiring 2x the disk space.

---

## Success Criteria

- [ ] Extract mode: reads exact byte range from source, writes chunk + `chunk.meta` to USB path
- [ ] Rebuild mode: creates sparse file at full size, inserts each chunk at correct offset
- [ ] Metadata is self-describing (any chunk can be validated standalone)
- [ ] No chunk is ever re-read from the source file unnecessarily (single-pass per chunk)
- [ ] Final rebuilt file matches source (SHA-256 verified)
- [ ] Works on Windows 10+ (sparse file support via `DeviceIoControl` or `FileStream`)
- [ ] Single `.exe` — no installer, no dependencies outside .NET runtime

---

## Tech Stack

| Layer | Choice | Rationale |
|-------|--------|-----------|
| Language | C# 12 | Strong binary I/O, spans, native sparse file APIs |
| Runtime | .NET 8 (LTS) | `System.IO.FileStream`, `RandomAccess`, no extras |
| Sparse Files | `P/Invoke DeviceIoControl` | Windows NTFS sparse file creation |
| Metadata | JSON (`System.Text.Json`) | Human-readable, zero dependencies |
| Checksum | `SHA-256` via `System.Security.Cryptography` | Built-in, fast |
| GUI UX | `WPF (Windows Presentation Foundation)` | Modern desktop UI, native dialogs |
| Build | `dotnet publish -r win-x64 --self-contained` | Single portable exe |

---

## Project Type

**DESKTOP / WPF GUI** — Windows native desktop application.

Agent: `backend-specialist`
Skill stack: `clean-code`, `plan-writing`

---

## File & Folder Structure

```
Oppaku/
├── oppaku-chunker.md           ← THIS PLAN
├── Oppaku.sln
├── src/
│   └── Oppaku.Gui/
│       ├── Oppaku.Gui.csproj
│       ├── App.xaml                ← Application entry point
│       ├── MainWindow.xaml         ← Main UI (Extract / Rebuild Tabs)
│       ├── Models/
│       │   └── ChunkMetadata.cs    ← Serializable metadata record
│       ├── Services/
│       │   ├── Extractor.cs        ← SOURCE MODE logic
│       │   ├── Rebuilder.cs        ← TARGET MODE logic
│       │   └── SparseFileHelper.cs ← P/Invoke + sparse file creation
│       ├── Helpers/
│       │   └── ChecksumHelper.cs   ← SHA-256 stream hashing
│       └── Exceptions/
│           └── OppakuException.cs  ← Domain-specific error type
└── tests/
    └── Oppaku.Tests/
        ├── Oppaku.Tests.csproj
        ├── ExtractorTests.cs
        ├── RebuilderTests.cs
        └── MetadataTests.cs
```

---

## Metadata Schema (`chunk.meta` — JSON)

```json
{
  "fileName":        "ubuntu-24.iso",
  "totalFileSize":   5368709120,
  "chunkSize":       1073741824,
  "totalChunks":     5,
  "chunkIndex":      2,
  "byteOffset":      2147483648,
  "actualChunkSize": 1073741824,
  "sourceFileHash":  "sha256:abc123...",
  "chunkChecksum":   "sha256:def456...",
  "createdAt":       "2026-08-18T23:30:00Z",
  "oppakuVersion":   "1.0.0"
}
```

> **Field notes:**
> - `chunkIndex` is 0-based. `byteOffset = chunkIndex × chunkSize`.
> - `sourceFileHash` — SHA-256 of the **entire original file**, computed **once before splitting begins**. Every chunk carries the same value. Used for final rebuild verification and for catching wrong-USB cross-contamination on insert.
> - `chunkChecksum` — SHA-256 of **this chunk's raw bytes only**. Used for per-chunk integrity validation on the target before writing to the sparse file.
> - Both hashes are stored as `"sha256:<hex>"` strings so the algorithm is self-describing.

---

## Task Breakdown

### Phase 1 — Project Scaffold

- [x] **T1.1** — Create solution: `dotnet new sln -n Oppaku && dotnet new wpf -n Oppaku.Gui -o src/Oppaku.Gui`
  → Verify: `Oppaku.sln` exists, `dotnet build` succeeds

- [x] **T1.2** — Remove CLI boilerplate and setup `MainWindow.xaml` base
  → Verify: empty WPF window launches

- [x] **T1.3** — Create test project: `dotnet new xunit -n Oppaku.Tests -o tests/Oppaku.Tests && dotnet sln add`
  → Verify: `dotnet test` runs 0 tests, no errors

- [x] **T1.4** — Set publish target in `.csproj`: `<RuntimeIdentifier>win-x64</RuntimeIdentifier>`, `<SelfContained>true</SelfContained>`, `<PublishSingleFile>true</PublishSingleFile>`
  → Verify: `dotnet publish` produces a single `.exe`

---

### Phase 2 — Core Models & Helpers

- [x] **T2.1** — Implement `ChunkMetadata.cs`: JSON-serializable record with all 11 fields above
  → Verify: unit test serialises → deserialises round-trip correctly

- [x] **T2.2** — Implement `ChecksumHelper.cs`:
  - `ComputeFileHash(string path) → string` — streams full file in 64 KB pages, returns `"sha256:<hex>"`
  - `ComputeSpanChecksum(ReadOnlySpan<byte>) → string` — hashes a single in-memory buffer
  → Verify: known SHA-256 hash of a 1 KB test file matches both methods

- [x] **T2.3** — Implement `SparseFileHelper.cs`: P/Invoke `DeviceIoControl(FSCTL_SET_SPARSE)` + zero-range marking
  → Verify: 4 GB sparse file created, `du` shows near-zero disk usage, file has full logical size

- [x] **T2.4** — Implement `OppakuException.cs`: typed exception with `ErrorCode` enum (`InvalidChunk`, `MetadataCorrupt`, `SparseFailed`, `ChecksumMismatch`)
  → Verify: exception is throwable and catches correctly in test

- [x] **T2.5** — Implement `Extractor.ComputeSourceFileHash(sourcePath)`: call `ChecksumHelper.ComputeFileHash` before any chunk extraction begins. Cache the result in memory for the session; write it into every `chunk.meta` as `sourceFileHash`.
  → Verify: hashing a 100 MB file completes; same hash appears in metadata of chunk 0, 1, and 2

---

### Phase 3 — Extractor (Source Mode)

- [x] **T3.1** — Implement `Extractor.ExtractChunk(sourcePath, chunkIndex, chunkSize, outputDir, sourceFileHash)`:
  - **Precondition:** `sourceFileHash` must already be computed (T2.5) and passed in — never hash inside this method
  - Calculates `byteOffset = chunkIndex × chunkSize`
  - Uses `RandomAccess.Read(handle, buffer, offset)` for zero-copy read
  - Writes raw chunk bytes to `outputDir/chunk_{index}.bin`
  → Verify: extracted chunk bytes match expected byte range of source file

- [x] **T3.2** — Write `chunk.meta` JSON alongside chunk file:
  - `sourceFileHash` = the pre-computed whole-file hash (same value in every chunk)
  - `chunkChecksum` = `ChecksumHelper.ComputeSpanChecksum(chunkBuffer)` computed immediately after read
  → Verify: `chunk.meta` is valid JSON, all 11 fields populated; `sourceFileHash` is identical across all chunks for the same file

- [x] **T3.3** — Add validation: chunk index bounds check, output directory writable, source file exists
  → Verify: invalid inputs throw `OppakuException` with correct `ErrorCode`

- [x] **T3.4** — Unit test `ExtractorTests.cs`: create 8 MB test file, extract chunk 0 and chunk 1, verify bytes and metadata
  → Verify: `dotnet test` passes all extractor tests

---

### Phase 4 — Rebuilder (Target Mode)

- [x] **T4.1** — Implement `Rebuilder.InitialiseTarget(destDir, metadata)`:
  - Creates sparse file at `destDir/fileName` with full `totalFileSize`
  - Creates `{fileName}.progress` JSON tracking received chunk indices
  → Verify: sparse file exists with correct logical size, near-zero disk usage

- [x] **T4.2** — Implement `Rebuilder.InsertChunk(chunkBinPath, metaPath, destDir)`:
  - Reads `chunk.meta`, validates `chunkChecksum`
  - Opens sparse file, seeks to `byteOffset`, writes chunk bytes
  - Updates `.progress` file — marks chunk as received
  → Verify: written bytes at offset match original extractor output

- [x] **T4.3** — Implement `Rebuilder.Finalise(destDir, fileName)`:
  - Checks all chunks received (`.progress` file complete)
  - Runs `ChecksumHelper.ComputeFileHash(rebuiltFilePath)` on the completed file
  - Compares result against `sourceFileHash` from any chunk's `.meta` file (all carry the same value)
  - If match → removes sparse attribute, deletes `.progress` sidecar, prints ✅ success
  - If mismatch → throws `OppakuException(ErrorCode.ChecksumMismatch)`, leaves file intact for diagnosis
  → Verify: finalised file SHA-256 matches `sourceFileHash`; deliberate corruption of one byte causes mismatch error

- [x] **T4.4** — Unit tests `RebuilderTests.cs`: end-to-end round-trip with 3-chunk 6 MB file
  → Verify: `dotnet test` passes, rebuilt file is bit-for-bit identical to source

---

  → Verify: UI updates accurately from Rebuilder events, shows checksum pass/fail

- [ ] **T5.4** — Wire up async event handlers & try/catch blocks:
  - Run Extractor and Rebuilder logic via `Task.Run()`
  - Catch `OppakuException` and display `MessageBox` to user
  → Verify: long operations don't freeze the window, errors show standard dialogs

---

### Phase 6 — Integration & Polish

- [ ] **T6.1** — End-to-end manual test: extract a 2 GB ISO in 700 MB chunks, rebuild on same machine (simulating USB flow)
  → Verify: SHA-256 of rebuilt file matches original

- [ ] **T6.2** — Edge cases: last chunk smaller than `chunkSize`, single-chunk file, already-complete rebuild attempt
  → Verify: all handled gracefully with clear messages

- [ ] **T6.3** — Performance check: extraction speed should be ≥ 80% of raw disk read speed
  → Verify: `Stopwatch` timing printed at end of operation

- [ ] **T6.4** — Publish release build: `dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true`
  → Verify: `Oppaku.exe` < 20 MB, runs on a clean machine without .NET installed

---

### Phase X — Verification Checklist

> 🔴 Do NOT mark complete until ALL pass.

- [ ] `dotnet build` — zero warnings, zero errors
- [ ] `dotnet test` — all unit tests pass (ExtractorTests, RebuilderTests, MetadataTests)
- [ ] Manual round-trip: 2 GB file → 3 chunks → rebuild → SHA-256 match ✅
- [ ] `.exe` runs on machine with no .NET SDK installed
- [ ] `Ctrl+C` mid-operation exits cleanly (no corruption)
- [ ] Invalid inputs show helpful error, do not crash
- [ ] `chunk.meta` from one session correctly drives Rebuilder in a fresh session
- [ ] `sourceFileHash` is identical across all chunk meta files for the same source
- [ ] Deliberate 1-byte corruption of rebuilt file is caught by `Finalise()` checksum check
- [ ] Security scan: `python .agents/skills/vulnerability-scanner/scripts/security_scan.py .`

---

## Risk Register

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Sparse file not supported on non-NTFS USB | Medium | High | Detect filesystem, warn user, fall back to pre-allocated file |
| Last chunk smaller than `chunkSize` | High | Low | `actualChunkSize = min(chunkSize, remaining bytes)` — always use `actualChunkSize` |
| `.progress` file corruption | Low | Medium | Re-validate `chunkChecksum` on each insert; progress is recoverable |
| Large buffer causing OOM on low-RAM machine | Medium | Medium | Stream in 64 KB pages via `RandomAccess`, never load full chunk to RAM |
| User re-inserts wrong USB (wrong file set) | Low | High | `fileName` + `sourceFileHash` in metadata cross-validated on each insert |
| `sourceFileHash` computed after partial extraction | Low | High | T2.5 enforces hash-before-split: hash is computed once and passed into every `ExtractChunk` call — never computed mid-session |

---

## Implementation Order (Critical Path)

```
T1.x (Scaffold)
  ↓
T2.x (Models + Helpers)  ←── all others depend on these
  ↓                ↘
T3.x (Extractor)    T4.x (Rebuilder)   ← parallel after T2 done
        ↓                 ↓
        └────── T5.x (GUI / UX) ───────┘
                      ↓
                 T6.x (Integration)
                      ↓
                 Phase X (Verify)
```

---

## Notes

- `RandomAccess.Read` (static, .NET 6+) is preferred over `FileStream.Seek` — it's thread-safe and avoids position state bugs.
- Sparse file creation requires **NTFS**. Always print the filesystem type before creating the target file.
- Keep `chunkIndex` 0-based internally; display as 1-based in the UI (`Chunk 1 of 5`).
- `OpenFolderDialog` is available in WPF starting with .NET 8, avoiding the need for third-party folder pickers.
- `.progress` sidecar file format: `{ "received": [0, 2], "total": 5 }` — simple and append-safe.
- **Hash-first invariant:** `sourceFileHash` is always computed over the complete, unmodified source file **before** any chunk is extracted. This means you can re-verify the rebuild at any point — even years later — by hashing the rebuilt file against any surviving `.meta` file, without needing the original source.

---

## V3 Vision: The Full Archive Tool

Moving forward, Oppaku is pivoting to become a comprehensive archiving solution. The core chunking technology is built; the next phases will introduce:

1. **Encryption & Security:**
   - AES-256 encryption for the `.oppaku-dir` custom archive format.
   - Password protection for archives and individual chunks.
   - Encrypted metadata headers to hide original file names and folder structures.

2. **Compression Integration:**
   - While `FolderPacker` currently stores files uncompressed to allow exact byte-offset hole-punching, future versions will explore streamable compression algorithms (like LZ4 or Zstd) that still allow for deterministic extraction and space reclamation.

3. **Archive Management:**
   - View, extract, or add single files to an `.oppaku-dir` archive without unpacking the whole thing.
   - Archive integrity checking and repair modes.
   
4. **Cloud & Network Hooks:**
   - Instead of just USB drives, stream out-of-order chunks directly over a local network or via cloud storage, rebuilding the file locally on the fly using the sparse-file engine.

---

### Session 2026-08-20 Notes
- Replaced FolderPacker with Zero-Storage VirtualFolderStream for memory-efficient chunking.
- Implemented Solid Archives (.oppaku-archive) with Brotli Compression and AES-256 Encryption.
- Modernized Oppaku.Cli with archive and unarchive commands to match the GUI features.
- Generated Top-Star README and implemented strict Proprietary License documentation.
- Next: Further cloud and network hooks, or archive management commands.
