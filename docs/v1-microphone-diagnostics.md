# Verify microphone capture on Windows

The microphone meter in **Settings → Audio** measures the PCM returned to QuickClip's shared-mode WASAPI capture client. For a running recorder it reads that lane. An idle **Test microphone** runs for eight seconds and keeps only aggregate measurements. **Stop test**, leaving Audio settings, or starting the recorder stops the idle test. A missing or wrong-flow endpoint produces a lookup or identity error with its HRESULT.

## Compare the inputs

To keep the test separate from your existing settings and library, launch the strict Personal build from a fresh PowerShell session with a scratch profile. From the repository root after building:

```powershell
$env:QUICKCLIP_PERSONAL_DATA_DIR = Join-Path (Get-Location) '.quickclip-local/mic-owner-profile'
dotnet src/QuickClip/bin/Release/net10.0-windows/QuickClip.dll
```

The scratch profile has its own app instance and clips folder. Leave instant replay off for the idle comparison. Before the replay test, verify that the clips folder points to the scratch profile.

1. With the recorder idle, open **Settings → Audio** and enable **Record selected Windows audio devices** if needed. Add **SteelSeries Sonar - Microphone** from the **microphone** picker. The playback picker shows a different Windows endpoint.
2. Click **Test microphone**, then speak a short test phrase. Let the test finish and click **Save diagnostics**. Repeat with **Microphone (SteelSeries Arctis Pro Chat)** using the same phrase and approximate distance. Keep both JSON files local.
3. For the recording-path check, enable the Sonar capture track and leave other microphones disabled. Use a game or a desktop display you've explicitly authorized for capture. Open **Settings → Audio** with the recorder running and save a diagnostic snapshot before the phrase. Speak the same phrase, wait a second for audio encoding, then save a second snapshot and save a short replay containing the phrase. Compare the change in counters between the two snapshots from that session.
4. Use the second snapshot's zero-based `TrackIndex` and choose a window that contains the phrase. The inspector writes a local JSON report to stdout. Pass `-StartSeconds` to skip unrelated clip time.

```powershell
./scripts/inspect-clip-audio.ps1 -Clip 'C:\path\to\test.mp4' -AudioTrack 2 -StartSeconds 5 -Seconds 10
```

The inspector lists audio stream indices and handler names with [FFprobe](https://ffmpeg.org/ffprobe.html), then decodes the selected track to the null output with [FFmpeg astats](https://ffmpeg.org/ffmpeg-filters.html#astats). Select the audio ordinal from the JSON snapshot. The MP4's absolute stream index includes video. An idle test has `TrackIndex = -1`.

## Read the results

- `SelectedId` is the configured MMDevice ID. An empty selection means Windows default in legacy mic mode. `ResolvedId`, `ActualFlow`, and `DefaultCaptureId` report what Windows returned at activation. Actual flow `0` is playback, `1` is capture, and `2` means flow could not be verified.
- `MixRate`, `MixChannels`, and the format fields describe the endpoint mix format. `RequestedRate` and `RequestedChannels` describe QuickClip's float32 stream request. `CaptureRate` and `CaptureChannels` appear after successful initialization of that client format. `MixHResult` and `MeterHResult` separately describe those queries.
- `PacketCount` can rise even when every packet is silent. `SilentPackets` counts packets Windows explicitly flagged silent. `NonzeroPackets` counts packets containing finite, nonzero PCM. The peak and RMS values are linear amplitude with 1.0 representing full scale. `WindowsPeak` and `MaxWindowsPeak` come from the endpoint meter and may differ from the PCM peak returned to QuickClip. A negative peak indicates that a measurement is unavailable.
- If the endpoint meter registers activity and `NonzeroPackets` stays at zero during the phrase, investigate the Windows capture stream and the selected shared-mode format before changing the mixer or MP4 code. If the input counter rises but `NonzeroMixerBlocks` does not, check timestamp fallback/discontinuity counts and where the packets landed on the mix timeline. `NonzeroMixerBlocks` counts pre-AAC blocks from the associated track and can include multiple sources when legacy mode mixes microphones together.
- If the mixer counter rises, check the persisted track's handler name, window, and peak/RMS. `-inf` with a nonzero decoded sample count is digital silence. A missing audio stream or an empty decode window is reported as an error.

The Sonar capture symptom still needs a supervised test. The PCM measurements and saved-clip inspection do not alter device selection, Windows defaults, or Sonar routing.
