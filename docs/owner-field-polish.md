# Independent owner field-polish candidate

Based on 8bc5a73591cdda5549dd1a53fcbc094a0c49fad5. Separate branch deliberately preserves unfinished local Codex work. Basic includes Sharing and all shortcuts, plain-language shortcut copy, right-aligned setup actions without Pause, smaller gallery subtitle, compact expandable discovery details to reclaim list space, deterministic x64 .NET 10 SDK selection. A known Minecraft launch stub is vetoed even in a cached catalog; a GDK manifest can supply launcher evidence instead of renderer evidence.

## Microphone playback

Explicit Record 5 seconds and Play recording beside the existing signal test, available in both audio routing modes. New optional native sink uses the identical WASAPI source, with bounded PCM16 stereo storage at 48 kHz, no persistent media file, no new dependencies. Existing telemetry-only probe is unchanged. Sample duration is limited from streaming start; show actual recorded duration if fewer frames arrive (no fabricated silence for missing packets). Explicit playback uses the existing WASAPI output helper with the default multimedia render endpoint; cancellation on page/mic changes and running recorder. Native and managed scratch buffers are discarded on cancellation/completion, and the temporary WAV stays in memory only. Physical audio acceptance still needed.

## HUD

Preserves startup mic latch. Original replay arrows and mic on compact 28 DIP dark tiles. Try the existing translucent layered window, then a compact non-layered opaque fallback if capture exclusion fails. Both must pass the Windows display-affinity check; no unsafe show-if-exclusion-fails fallback. Win32 error status and idle preview are available. Neither success nor preview proves actual DDA exclusion or exclusive-fullscreen visibility. Verify on real hardware; opaque fallback is a deliberate tradeoff rather than guaranteed transparency.

## Theme

Apply and refresh selection inside a reentrancy guard (Fluent ThemeMode can invalidate control templates), refresh choices when opening the dropdown, preserve the normalized saved library when switching themes, and clarify draft-only invalid contrast. Root cause of owner's disappearing theme not reproduced; this is defensive hardening, with serialization tests, not confirmed physical fix.

## Minecraft

Minecraft Java is independently confirmed by the owner's foreground process, a bundled javaw.exe under Minecraft Launcher/runtime, with no matching current-user AppX registration. Automatic Java title identification is not implemented here. Do not approve arbitrary Java runtimes or infer gameplay from a title string. A future adapter should validate trusted launcher runtime and actual Minecraft-specific process evidence without retaining raw command lines (they may contain session credentials), then associate with the existing title identity. Otherwise offer owner-selected exact process association with the shared-runtime limitation stated.

## Integration

Per-display permission redesign, in-flight setup cancellation and display-revocation publication races remain in the already-started local frontend/reliability lane. No updater/diagnostics implementation replaced. Reconcile with their current unpublished changes (especially Api.cpp, Audio.cpp and shared settings). Windows CI and physical audio, HUD exclusion, theme lifecycle and UI testing required on exact combined head. No release approval.
