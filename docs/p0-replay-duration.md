# FT-10 replay duration measurements

Baseline `0c7719410a045a7a28c7a37d1d8f14d6cc045ef7`. The test feeds generated H.264 with one-second GOPs, 60 fps and three stereo AAC tones through the production PacketRing, selection and MP4 mux. It opens no capture source and uses an exact synthetic clock. The production caller still supplies its original clock and selection policy.

## Windows 10 19045 results

| Request | Offset after preceding keyframe | Legacy video | Bounded video |
| --- | ---: | ---: | ---: |
| 15 s | 0.400 s | 15.416667 s | 14.416667 s |
| 30 s | 0.400 s | 30.416667 s | 29.416667 s |
| 60 s | 0.400 s | 60.416667 s | 59.416667 s |
| 60 s | 0.600 s | 60.616667 s | — |
| 120 s | 0.400 s | 120.416667 s | 119.416667 s |
| 60 s during warm-up | — | — | 3.266667 s |

At the 60-second request and 0.400-second phase, FFprobe reported legacy container duration 60.421333 s, which is 4.666 ms beyond its video track. The other legacy measurements in the table had matching video and container duration. The first second of both 60-second files decoded with FFmpeg error escalation. The three named lanes stayed ordered and aligned within packet tolerance, and the bounded output preserved the three individual test tones.

The legacy save includes the previous keyframe and all video from it to the requested end. The first frame before the desired start accounts for 400 or 600 ms here. A final 60 fps sample contributes another 16.667 ms. Container/audio overhead accounts for only a few milliseconds in this fixture. This reproduces the field symptom without involving hotkey latency, and distinguishes the video-track duration from the container duration. It does not establish what occurred in each owner clip.

## Next implementation choice

Switching the default to the existing bounded snapshot would keep the newest frame and avoid positive GOP pre-roll, but can discard almost one GOP at the beginning. It cannot meet the one-frame target by itself. A precise follow-up should first prove a container edit with decodable pre-roll in FFmpeg, libmpv, seeking and exports. If that fails, evaluate re-encoding only the leading partial GOP and verify stream-parameter compatibility and save latency. Do not adopt either path until independent A/V alignment, first-frame and end-of-clip seeks, and editor/export playback are tested. FT-10 stays open, and this PR leaves production selection and mux timing intact.

## Reproduce

```powershell
dotnet test QuickClip.slnx -c Release -p:QuickClipProfile=Personal -p:RequireCaptureEngine=true --filter "FullyQualifiedName~ReplayMuxDurationTests|FullyQualifiedName~ReplayRuntimeTests" --logger "console;verbosity=detailed"
```

Ten tests passed, zero skipped. Fixtures are generated in scratch and disposed. No private media or endpoint input is used. Owned code is the extracted native save seam, native test fixture export and managed probe test. Rollback is a revert of this PR and removes only the measurement harness and documented findings.
