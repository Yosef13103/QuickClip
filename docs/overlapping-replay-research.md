# Overlapping replay joining — research and implementation contract

Status: research only; no new runtime joining, metadata migration, or deletion code enabled.

## Product behavior

Default: separate clips (unchanged). Optional automatic modes: extend overlapping clips during a recording session or queue joining until that session ends. Manual Gallery action: Find overlapping clips; show independently highlighted clusters, chronological interval bars, range selection within a cluster, source titles and resulting duration. A 60-second save at t=60, t=90 and t=120 covers [0,120], not 180 seconds. Never join across a gap or unrelated recording session. Fully contained/duplicate clips add no duration. Keep a normal save sound per successful hotkey; merge completion is separate and quiet. Use a configurable cap and explicit opt-in.

## Existing seams

Recorder.SaveOnWorker already receives ReplaySaveReceipt containing SessionId, FirstFrameTime, LastFrameTime, FirstSourceId, LastSourceId, actual duration and source metadata. ReplayMarkers already anchors save bookmarks. Native Engine::Save snapshots PacketRing entries containing original session timing, sequence, stream and packet; subtracts the snapshot start and rescales each stream when muxing MP4. These are better foundations than file dates or frame-image matching.

Persist exact immutable recording provenance only after successful publication, tied to file/media identity. Include recording/session identity and epoch, source, encoder/codec parameters and extradata, stream layout (including full mix and isolated tracks), original timebase and exact per-stream packet/range information sufficient to prove overlap and adjacency after MP4 timestamp rebasing. Existing bookmarks and filename UTC are insufficient proof. Edited/replaced media must invalidate or regenerate provenance. Use existing library migration/repair patterns and keep metadata private/local.

A sorted interval-component planner can group verified overlapping entries in O(n log n). Select a chronological contiguous subset, deduplicate coverage, and show union duration rather than sum. Legacy/imported clips with no reliable provenance are possible matches only; use packet alignment or decoded overlap verification on demand and fail closed for ambiguous/repeated scenes. Never infer exact overlap from file times alone. Old clusters can be supported if independently verified; no automatic legacy deletion.

## Joining engine

Prefer packet-level remux using the native FFmpeg already shipped, not CPU video re-encoding. Prove compatible stream identity, exact shared packet ranges, decode dependencies at boundaries (including keyframe/closed GOP and B-frame DTS order), correct per-stream duration and AAC sample/padding and priming behavior. Retain one copy of each packet/sample and preserve all audio tracks. Reject gaps, mixed sources, incompatible encoder changes, stale identities or unavailable proof. If unavoidable, offer a separately disclosed re-encode option in manual mode only. Do not stitch by wall-clock estimates or concatenate whole overlapping files.

Queue off the capture/dispatcher path. Serialize per cluster; do not hold capture mutex while remuxing prior saved files. Keep originals intact until a staged output is closed, checked for stream count, durations, continuity, and validated identity; atomically publish distinct result and persist journal before recycling originals. Preserve metadata/bookmarks at correct offsets and provide transaction-aware Undo. Avoid auto-consuming edited/favorited/project-linked clips or ones in active editor/export/share; offer review. Handle low disk, cancellation, failed recycle, crash, interrupted recording and concurrent hotkeys without loss. Never touch excluded/archive folders. Deferred mode should write once per cluster rather than repeatedly rewrite large files; live mode needs caps, bounded I/O and profiling to avoid quadratic total disk traffic. No unbounded RAM buffer.

## Synthetic proof (Linux FFmpeg 7.1.5, not QuickClip Windows capture)

Generated 9s 320x180 30fps H.264 with fixed 1s GOP, no B frames, three distinct 48kHz AAC audio tracks. Stream-copied two six-second overlapping clips from the same encode. Used concat demuxer A outpoint=4/duration=4 and B inpoint=1/duration=5. Commands:

    ffmpeg -v error -f lavfi -i testsrc2=size=320x180:rate=30:duration=9 -f lavfi -i sine=frequency=440:sample_rate=48000:duration=9 -f lavfi -i sine=frequency=660:sample_rate=48000:duration=9 -f lavfi -i sine=frequency=880:sample_rate=48000:duration=9 -map 0:v -map 1:a -map 2:a -map 3:a -c:v libx264 -g 30 -keyint_min 30 -sc_threshold 0 -bf 0 -threads 2 -c:a aac -t 9 full.mp4
    ffmpeg -v error -i full.mp4 -t 6 -map 0 -c copy a.mp4
    ffmpeg -v error -ss 3 -i full.mp4 -t 6 -map 0 -c copy b.mp4

list.txt:

    file 'a.mp4'
    outpoint 4
    duration 4
    file 'b.mp4'
    inpoint 1
    duration 5

    ffmpeg -f concat -safe 0 -i list.txt -map 0 -c copy joined.mp4
    ffprobe -v error -show_entries stream=index,codec_type,duration,nb_frames -of compact joined.mp4
    ffmpeg -v error -i full.mp4 -map 0:v -f framemd5 full.md5
    ffmpeg -v error -i joined.mp4 -map 0:v -f framemd5 joined.md5

Result: all 270 decoded video frame hashes equal in order, video duration 9.000 seconds; all three audio tracks retained BUT each had 424 packets/9.021333s versus original 423/9.000s. No non-monotonic timestamp warnings. This is a feasibility proof and a counterexample to treating successful concat as lossless A/V joining. No production algorithm or CPU benchmark yet. Require audio sample comparison at seams as well as frame hashes.

References: FFmpeg formats concat demuxer documentation https://ffmpeg.org/ffmpeg-formats.html#concat (inpoint/outpoint may produce extra/overlapping packets and requires compatible streams); Medal feedback discussion https://feedback.medal.tv/2001 explicitly distinguishes a single continuous union from separate deduplicated saves, not proof of a released implementation; Medal montage workflow https://support.medal.tv/support/solutions/articles/48001185258-how-to-create-a-montage .

## Acceptance before enablement

Synthetic known reference: duplicate, contained, overlapping, disjoint, three-plus chained, cross-session, source/encoder changes, timestamp rounding, non-keyframe, B-frame, dropped-frame, audio-only drift, multi-track and variable-FPS inputs. Validate source video frame ordering and decoded audio sample continuity compared to a continuous reference, plus seek/playback. Simulate crash at each publication/recycle stage, undo, edited clip conflicts, stale catalog, file unavailable and shutdown. Native Windows test on generated scratch clips, then real gameplay owner validation. Measure save latency, capture dropped frames, CPU/GPU, sustained disk write/read and space against unmodified separate saves; do not claim speed from the tiny synthetic experiment.
