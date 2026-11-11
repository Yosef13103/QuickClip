# Live replay markers and duration requests

`feat/replay-markers-hotkeys` is based on capture frame pacing commit `651d8c4b8e3daf6e182bef674ff53cc1f8037479`.

## Recorder API

- `Recorder.Instance.TryAddReplayMarker(label)` returns a session-scoped marker when a decodable frame was encoded within 250 ms of the request. A stopped, warming up or stalled recorder can return null. A label is optional and can contain up to 128 characters after trimming.
- `Recorder.Instance.SaveReplayAsync(seconds, cancellationToken)` accepts any positive whole-second request. UI can offer choices such as 15, 30, 60 and 120 seconds. It returns a receipt on success and reports an unavailable or busy save through the existing `SaveFailed` event. The save gate rejects overlapping requests. Cancellation is honored before starting and before publishing the clip. An in-progress native mux finishes first, then the staged file is removed if cancellation was requested.
- `Recorder.Instance.ReplaySaved` publishes the same receipt after the clip is moved to its final path. It also fires for existing `SaveClipAsync()` calls. Existing save commands, names, clip notifications, and legacy keyframe selection remain available. UI subscribers should dispatch to the UI thread as needed.

An explicit request ends at its acceptance time and is bounded by the configured ring duration. It starts at the first retained keyframe in that window. Warm-up, eviction, keyframe alignment and video stalls can shorten the clip. A request with no decodable footage fails. The save remuxes the selected video and audio packets from the existing ring. The audio uses the same timestamp origin as video and is bounded at the video tail. Encoder settings, replay capacity and audio routing are unchanged.

## Receipt and persistence

- `RequestedSeconds` is the caller's input. `ActualVideoDuration` comes from the finalized MP4 video track header and is null if the track duration cannot be read. `IsShorterThanRequested` is also null in that case. A frame sample can extend past its presentation timestamp.
- `AvailableVideoSpan` is the time from the oldest usable keyframe still retained at snapshot time to the latest retained video frame eligible for the request. It can include keyframe slack and capture gaps. It excludes the terminal frame's duration. `FirstFrameTime` and `LastFrameTime` are session-relative timestamps of frames selected for the save. `CapturedAt` is the optional absolute capture timestamp from the file.
- Each bookmark has a GUID, session GUID, intended game and source, event time, encoded anchor time and actual source ID. Source IDs identify capture graph instances within a session. A compatible graph restart can preserve earlier frames. An incompatible restart clears them. Markers are attached to encoded frame identities so a restarted source cannot reuse an old marker's timestamp by accident.
- The recorder holds at most 128 pending markers and drops the oldest on overflow. Markers expire with their anchor frames. A receipt includes only markers present when the request was accepted whose exact anchor frames made it into the snapshot. `SavedReplayMarker.Offset` is relative to the first saved video frame. Multiple markers can share a frame. A marker alone cannot keep footage in the ring.

Use the published path to resolve the canonical clip ID in the future library integration. Persist markers against that clip ID and de-duplicate repeated receipt handling by clip ID and marker GUID. A single marker may legitimately appear in multiple saved clips at different offsets. Do not use the display duration on the legacy `SavedClip` event for marker placement. The receipt is an in-process contract and introduces no new sidecar format.

## Integration and validation

Apply the native ring/API and managed interop together. Then apply the recorder runtime and tests. `Recorder.cs` and `CaptureEngine.cs` are shared integration points. When merging recorder ingestion, preserve the existing publication path and use the receipt after a successful publish. Wire duration controls and bookmark actions to the recorder API in the UI milestone.

The default tests exercise the native ring with synthetic video and three audio packet lanes, including compatible and incompatible graph changes, stale positions, warm-up, retention, keyframe alignment, and multiple markers. The managed tests exercise a held save gate and cancellation. The opt-in endpoint test compares the reported video duration to the output file, checks a marker offset, and verifies three track names and their order. It uses one playback endpoint and two deliberately unavailable endpoints to cover silent lanes.

For a supervised hardware check, use a non-private test window and configure three distinct playback/microphone sources. Trigger a marker during a brief signal from each source, then request a duration longer than the warm buffer. Probe the clip for three ordered tracks, check each track independently, and verify the saved duration and offsets against the visible signal. Repeat with a compatible source switch and a mode change that clears the ring. Test simultaneous save requests and cancellation on disposable output. Keep this check separate from the default automated suite.

## Future continuous recording

An opt-in session recorder could consume the same encoded packets through a bounded writer queue. Start segments at video keyframes and rotate on stream-parameter changes. Track session and frame identities across segments so exported markers can be resolved to a finalized file. Enforce a separately configured disk quota and reserve space before opening a segment. Stop and report writer backpressure or insufficient space without blocking the replay ring or silently dropping required packets. Stage each segment separately, finalize it before atomic publication, and retain incomplete segments for explicit recovery. Treat session manifests and library relationships as part of the storage milestone.
