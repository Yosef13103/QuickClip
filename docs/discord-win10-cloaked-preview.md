# Experimental Discord main-window mirror (Windows 10)

**Status:** local dev candidate; not released, integrated, or approved for gaming. Explicit opt-in via Settings → Discord → Experimental borderless Discord mirror. Default false; Windows 11 and standard Discord WGC unchanged.

## Architecture
- `DiscordGroupPolicy` still owns explicit capture eligibility, app identity and game priority. `DiscordMainWindow` selects a verified main-HWND candidate by title and area, pins the existing identity, and doesn't promote an unlabeled pop-out.
- Native `DiscordDwmHost` is created on the existing companion video thread. It creates an owned 1×1 hidden host, verifies `DWMWA_CLOAK` **before** first `ShowWindow`, resizes to the recording canvas while cloaked, and registers **one authorized** Discord HWND with `DwmRegisterThumbnail`.
- `DwmQueryThumbnailSourceSize` + `DwmUpdateThumbnailProperties` preserve proportions after window resizes. Rendering is at most 1:1 in the current canvas (no artificial upscaling), with letterboxing or uniform downscaling as needed. If the application cloak disappears, source identity changes, or DWM fails, capture stops rather than exposing an on-screen proxy or switching to another source.
- Existing `GroupCapture` acquires the **cloaked host** via WGC and reuses the existing GPU encoder, replay ring, audio-track plan and save/export paths. Experimental backend flag 5 is permitted only with exactly one authorized companion target. No generic capture-hook replacement, external injection, monitor fallback, or process duplication.

## Important limits
- Canvas/encoder dimensions are set when recording begins. A resize can alter framing and detail within the fixed stream but does not dynamically change MP4 dimensions or automatically restart the replay.
- Main-window title matching is a guarded heuristic, not a guaranteed Discord API. Unrecognized titles wait rather than capture another window. Validate locales, window-size edge cases and the transition from multiple pop-outs.
- On Windows 10 the OS border permission API is unavailable. App-cloaking the host **appeared borderless on the owner's tested system**, but do not claim Windows universally disables indicators.
- No pop-outs in this mode; no multi-source composition. Group WGC remains the default path for normal Discord mode.
- GPU contention while gaming remains **unmeasured for the integrated build**. Earlier separate `PrintWindow` research coincided with NVIDIA driver hangs in Marvel Rivals; don't run game-concurrent compatibility tests without separate supervised approval.

## Verification and integration gate
- Native and .NET strict Personal build passed on isolated `feat/discord-companion` checkout.
- Focused selection and existing Discord policy tests passed; entire headless suite 984 passed, 24 skipped, 0 failed (as of 2026-11-21).
- **Unverified:** integrated app live capture, main-window resizing and quality, source-title handling after Discord updates, minimization, moving across monitors, sleep/reconnect, Alt+Tab visibility, WGC system indicator on other hardware, game FPS/frametimes, and audio/video sync with real endpoints.
- Owner preview launcher on Jambo: `Desktop\QuickClip Discord Capture Tests\7 - Discord Mirror Dev Preview.cmd`. Uses development profile, not production. Don't merge to `integration/development`, publish, or install into production until hardware acceptance and separate approval.
