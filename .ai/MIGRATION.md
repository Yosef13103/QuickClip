# SteelSeries Moments migration: private-data-safe investigation and import

## Paths (READ-ONLY sources)
- GG binaries: `C:\Program Files\SteelSeries\GG`
- GG shared data: `C:\ProgramData\SteelSeries`
- Finished library: `E:\Stuff\Personal\Clips` (~31.5 GB finished exports; parent root)
- Legacy original/editing archive: `E:\Stuff\Personal\Clips\SteelSeries Moments` (~113 GB; **nested** child)

**Critical:** QuickClip/Codex must not remove, rename, edit, re-encode in place, recurse into unintended system/user locations, commit or upload media, inspect login/session secrets, or clean up source directories. Investigation requires approval for sensitive paths; read-only source access is not permission to execute GG binaries or modify ProgramData.

## Step A: read-only topology inventory
Build a local scanner that:
- collects directory sizes, file counts, extensions, relative paths, timestamps (UTC/local), duration, resolution, codec, number/types/labels of audio streams using ffprobe when recognized;
- samples small files/representative clips first; supports `--max-files`, `--max-bytes`, `--sample`, `--skip-large`, `--exclude`, `--no-media-open`, `--redact`, `--resume`;
- distinguishes folders of the originals and exports, filters nested archive out of normal exports;
- recognizes hidden/system files and metadata DBs by type, but **doesn't open credentials**, auth files, tokens, cookies, encrypted user profiles, or personally identifying chat content;
- uses read-only DB connections or copies metadata DB into a scratch folder before opening if necessary (SQLite connections can create -wal/-shm unless opened read-only immutable, so be careful);
- reports confidences, observed evidence and unresolved questions, not assumptions.
- stores reports only to an explicit user-chosen local output directory outside originals. Summaries checked into repo must be scrubbed of personal filenames, personal video titles, paths and full media metadata.

## Step B: identify audio format correctly
Windows Media Player sometimes only selects one audio track. `ffprobe -show_streams -show_format -of json` should reveal whether other tracks are embedded. Check whether folders contain separate audio files/sidecars, indexing DBs, custom containers, proprietary compression and actual usable streams. Don't infer loss because one player hides Chat/Mic. Compare source saved clip to an exported result made by Moments when possible, using a test duplicate rather than editing originals.

## Step C: match original to export
Candidate identity: normalized game title + capture time + duration window + resolution + video content fingerprint (ONLY if needed, sampling is optional and cost documented) + GG metadata IDs if found. Filename capture time is often more reliable than edited file `CreationTime` or `LastWriteTime`, but may be incorrect. Keep evidence and confidence scores per match; require manual resolution for ambiguous records. Never infer custom title from arbitrary external metadata without user review.

## Step D: dry-run migration manifest
Table fields: source relative path; detected container/tracks; proposed game; proposed capture time and evidence; proposed original/final designation; proposed output filename; proposed destination; confidence; missing streams/warnings; expected storage bytes; match with prior export. Also show aggregate projected disk usage and estimated work. If using content hashes, offer a sampled fingerprint vs full hash choice; never default to fully hashing 113 GB.

## Step E: controlled import (separate explicit approval)
- New destination chosen by user; preserve existing originals and exports. No use of existing GG hot directories.
- Prefer importing existing finished files by **indexing in place** first; no need to duplicate 31.5 GB. For originals, make new readable MKV/MP4 only if decoder reliably supports it. Do not promise recoverability of private/proprietary audio.
- Use temp file then fsync/atomic rename within destination where possible; verify ffprobe streams, A/V sync, duration, and checksums or sampled fingerprints. Never overwrite without explicit per-operation selection.
- Resume after cancellation, support failed-item report, with `--limit`, `--dry-run`, `--dest`, `--verify` flags. Test with synthetic fixtures or user-approved duplicated small real clips, not all archive.
- Post-import cleanup: OUT OF SCOPE unless separately explicitly approved after review and verified independent backups. Deleting old files from `SteelSeries Moments` may make GG lose its library.

## Reporting expectations
Output a small safety-first report with `what could be parsed`, `how many sources/exports`, `audio stream recovery evidence`, `unmatched files`, `space needed`, and `exact next local test`. Never claim a conversion succeeded without verifying the resulting media.
