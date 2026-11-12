# FT-11 WASAPI endpoint diagnosis and picker contract

Base is PR #7 at `0c7719410a045a7a28c7a37d1d8f14d6cc045ef7`. This is an independent branch from the armed recording lifecycle PR. It covers the engine and managed contract for FT-11. Sonar hardware acceptance remains open.

## Native behavior and evidence

- An explicit playback lane resolves its exact endpoint ID and verifies eRender before starting WASAPI loopback. An explicit microphone lane verifies eCapture and starts without the loopback flag. The same friendly name can appear in both flows. A failed explicit lookup, activation or read does not substitute the system default. The existing retry policy retries the same endpoint.
- Both paths request shared-mode stereo float at the recorder's capture rate with WASAPI conversion enabled. The diagnostics report endpoint mix rate and channel count separately from capture rate. This makes a driver-format or initialization failure visible without making a speculative change to the stereo conversion or mixer timeline.
- `CaptureEngine.ReadEndpointStatus()` returns the native lane ordinal, flow, stage, HRESULT, endpoint mix format, capture rate, packet count for the current stream attempt, age of its last packet and optional latest packet peak. It returns an empty list outside explicit endpoint recording. The snapshot can change during reconnect. No raw IDs, friendly names or PCM are returned.
- Native failures record ordinal, flow, stage and HRESULT. Enable the existing **Sample recording timings** diagnostics to get the sanitized endpoint format and five-second packet/peak summaries during an explicitly started session. Peak measurement uses the existing packet before it enters the mixer. A reported zero peak with packets indicates silence in the sampled data, and zero packets indicates no packets during that interval. A missing peak means diagnostics are off or no packet was measured. This never opens a separate audio graph or retains raw samples.

The owner reported silent Sonar eCapture with working Sonar render loopback and an Arctis input. The code and synthetic tests do not establish the cause. Potential causes include Windows microphone privacy, Sonar mute/gate or device routing, format negotiation, no input packets, and a later mixer or playback issue. An HRESULT and signal sample can narrow these cases. Do not assume a playback lane with a microphone-like name is the physical microphone path.

## Chat 2 picker handoff

Use `CaptureEngine.EnumAudioEndpoints()` and `EndpointAudio.ForFlow` to build separate **Playback (what you hear)** and **Microphones (input)** lists. Match a stored selection using `EndpointAudio.FindEndpoint`, which requires flow and case-insensitive exact ID. Display the friendly name. Preserve the stored ID and flow for absent selections, showing a missing-device state until it returns. Never merge rows based on their labels.

Associate status ordinals only with the immutable enabled endpoint plan supplied to the running engine in the same session. A pending edit or reorder in Settings does not describe the running native lanes. Display the stage and HRESULT on failure, format and packet availability separately from level, and do not show a healthy-input indicator just because Windows enumerates a device. No Audio WPF code changed in this branch.

## Verification and rollback

Run `dotnet test QuickClip.slnx -c Release -p:QuickClipProfile=Personal -p:RequireCaptureEngine=true` with capture opt-ins unset. `EndpointAudioTests` covers same-label/different-flow identity, missing input, flow mismatch and native peak measurement of synthetic buffers. Use a scratch profile for `--check-personal-runtime`.

For a supervised Sonar test, select Game playback, Chat playback, and the explicit Sonar **eCapture** input. Enable timing diagnostics and run short, separate source signals using a nonprivate test scene. Compare the mic's stage/HRESULT, packet counts and peak with the Arctis input and the separately selected Sonar render lane. Confirm physical mic mute/gate and Windows privacy locally without changing system settings as part of the software test. Probe the disposable MP4 for three named tracks and listen to each alone. Repeat an unplug/reconnect of an approved test device if practical. A signal at the capture boundary with a silent exported lane shifts the investigation downstream. No live input was opened for the automated validation.

Owned files are `Audio.cpp/.h`, the endpoint diagnostic export in `Api.cpp`, the engine's lane registration and status snapshot, managed capture interop and endpoint plan helpers, and tests. The lifecycle PR also touches API, engine and interop files. Combine the additive exports and preserve the lifecycle start signature and display identity check. A revert removes diagnostics without migrating settings or changing saved media.
