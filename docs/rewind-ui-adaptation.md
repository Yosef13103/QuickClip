# Rewind-inspired QuickClip UI — owner direction 2026-11-20
Design backlog, not implementation or pixel-perfect mandate. Owner approved game hubs and recording sessions; interactive timeline is optional/deferred, not required. Owner likes Rewind's layout, theme/colors, recording status, straightforward controls; exclude achievements, auto-clipping, match stats and per-game integrations. Keep QuickClip's identity, functionality, existing WPF and configurable appearance. Source reviewed at Rewind 15755d04b78da9b8735402958982d0bf37f7ef93 (v0.2.6). Reimplement ideas in existing code; Rewind is GPLv3, attribution/license review if actual code/assets adopted.

## Visual references (immutable)
- [Gallery](https://github.com/abdelaziz-mahdy/rewind/blob/15755d04b78da9b8735402958982d0bf37f7ef93/docs/images/screenshot.png): three-column large landscape cards, black canvas, dark rail, prominent page heading + counts/size, inline filter buttons with counts, sort/folder at right, clip/session metadata below thumbnails.
- [Game hub](https://github.com/abdelaziz-mahdy/rewind/blob/15755d04b78da9b8735402958982d0bf37f7ef93/docs/images/matches.png): prominent game icon/name/status, four inline summary cells, compact Capture Settings card and Edit affordance, group cards. Replace match metrics with recording facts.
- [Recorder](https://github.com/abdelaziz-mahdy/rewind/blob/15755d04b78da9b8735402958982d0bf37f7ef93/docs/images/recorder.png): ARMED dot/label + configured buffer in rail; expandable popup with Capturing selector, elapsed/target buffer, progress, large primary Save clip + hotkey, outline Record.
- [Settings](https://github.com/abdelaziz-mahdy/rewind/blob/15755d04b78da9b8735402958982d0bf37f7ef93/docs/images/settings.png): General and My Games navigation; section marker + heading + brief reason; segmented buffer duration and 2x2 outcome-based presets; simple toggles with contextual options underneath. Recorder remains available in Settings.
- [Design spec](https://github.com/abdelaziz-mahdy/rewind/blob/15755d04b78da9b8735402958982d0bf37f7ef93/docs/superpowers/specs/2026-07-25-broadcast-deck-design-system.md), [theme.dart](https://github.com/abdelaziz-mahdy/rewind/blob/15755d04b78da9b8735402958982d0bf37f7ef93/lib/src/ui/theme.dart). Actual checked-in screenshots/current shell supersede older top-deck spec. Current navigation widgets: nav_rail.dart, recorder_panel.dart, session_card.dart, event_filter_chips.dart, game_hub_screen.dart, settings_screen.dart.

## UI-01: Broadcast-inspired optional QuickClip theme
Reference token mapping (Rewind values, not final mandatory QuickClip palette):
| Role | Hex | Application |
|---|---|---|
| Canvas | #08090B | Gallery/editor backdrop |
| Surface | #101216 | Sidebar/cards |
| Raised surface | #181B21 | Hover/popup/selected rows |
| Hairline | #14FFFFFF | Thin divisions, boundaries |
| Primary text | #E8EBEF | Headings/body |
| Secondary text | #9AA3B0 | Descriptions, counts |
| Dim text | #7E8794 | Secondary metadata, ensure contrast |
| Interactive | #DCE3EC | Selected border/primary action/focus |
| Pressed | #B9C4D2 | Pressed action |
| Armed/warning | #F5A524 | Armed/temporarily unavailable |
| On air | #FF4D4F | Explicit continuous recording if implemented |
| Positive | #37D39B | Success/healthy meter |
| Danger | #EA5257 | Error/destructive action |
Use existing semantic theme resources (not color literals scattered in XAML); preserve current QuickClip status contract (game red, manual desktop blue, armed/recovering amber, master off original) until explicit design acceptance. Distinguish active capture from manual continuous recording. Rewind uses near-white interactive chrome and state hues; implement neutral selection and meaningful accents as theme option, not a ban on owner custom colors/gradients. Theme Designer C: support gradient stops/direction/preview, semantic roles and reset, import/export only if approved, validation, contrast across Light/Dark/AMOLED/Slate/Custom. Use existing font licenses or system typography; reference Archivo headings, Inter Tight UI, IBM Plex Mono numeric role (OFL fonts need notices if adopted). Prefer 8 DIP card / 6 control / 4 chip / 2 small radius as visual references, not fixed universal constraints. Compact metadata, aligned numerical counters, clear focus rings, restrained animation. Test ~100/125/150% DPI, ultrawide and narrow window, high contrast/readability, keyboard navigation, disabled/hover/selected/error states. Preserve existing themes.

## UI-02: Shell / recorder
Illustrative layout:
```text
QUICKCLIP                          Gallery   [Sort] [Folder]
● ARMED  120 s buffer  ▾           All recordings    56 · 8.4 GB
All clips                         [All] [Favorites] [Tags ...]
GAMES                             [thumbnail ] [thumbnail ] ...
▼ Terraria              56
    All recordings      56
    Calamity            34
    Vanilla             22
▶ Half-Life 2           42
+ Add game
Settings    Storage    Diagnostics
```
Expandable/persistent status at rail top or existing equivalent, available in Gallery/Editor/Settings, never hides master control. Popup example:
```text
CAPTURING
[ Terraria · Game window   ▾ ]  [Auto / explicit source]
Collection  [Calamity      ▾]
01:42 / 02:00   [=======---] BUFFER FILLING
[ Save clip           Alt+F10 ]
[ Start recording ] (only when continuous mode implemented)
```
- Actual buffered duration from native telemetry (earliest valid keyframe and latest committed replay time, not wall-clock since launch), actual capture backend/target and configured duration, state reason.
- Master off/armed/no eligible game/recording/recovering/permission blocked/temporarily minimized/buffer ready/saving/error distinct. Explain why Save Clip unavailable, no fake success. Save button existing request path. Clear deliberate manual source override, return to Auto, separate grants; source picker does not implicitly enable master or broaden authorization. Source label e.g. 'Game window · Terraria', 'Discord · Stream pop-out', 'Display 2 · Entire display', 'Game window minimized · black frames'. Keep game state sticky on alt-tab.
- Active collection visible only in relevant game, explicit None/Automatic/manual. Changes affect new saves according to accepted E2 semantics. Selecting edit profile is not source switch. No expensive preview polling; state notifications, bounded low-frequency telemetry, coalesce visual updates, suspend UI updates when hidden. Preserve close-to-tray, editor draft/player state.
- Existing recording HUD and custom notification systems remain separate; align compact semantics without removing owner customization.

## UI-03: Game hubs / Gallery
Example: Terraria header + detected/idle pill; summary cells 56 Clips | 12 Sessions | 8.4 GB | 2 Collections, computed only from current filtered/authorized library with clear total semantics. Capture profile summary '1440p · 60 FPS · 120 s · NVENC P2' links directly to same existing per-game Recording settings. Active collection selector, Manage Collections & Tags link, collection chips (All/Uncategorized/Calamity/Vanilla with counts), normal tags ANY/ALL filters, favorites/hidden and storage. Keep actual existing All Clips, day feed, selection, editor/import, search, sort, storage, folder actions. Expand sidebar game tree from E2; icons from existing discovery, counts and live state, no title-only identity. Maintain virtualization and muted one-at-a-time hover preview.
- Required alternative view selector: Clips (existing flat grid) / Sessions. Session card has representative thumbnail, date/start/end or elapsed if known, clip count, saved footage duration/size, collection if homogeneous (mixed otherwise); no KDA/kill count/champion/match art. Session ID from verified logical recording session; approximate legacy grouping disclosed. Collection remains user categorization across sessions; sessions need not create folders. Click session shows clips, not synthetic whole-match video. Back restores scroll/filter/selection. Keep B overlap highlighting and popout separate from session grouping.
- Deferred OPTIONAL session timeline (not needed for initial session grouping): recorded intervals shown as positioned bars on actual elapsed session time, unrecorded gaps distinct; bookmark markers only when anchored to retained footage (or explicitly labeled event outside footage if supported). Click/seek to recorded segment, skip gaps during playback; overlapping clips choose exact proven union or explicit selection, don't repeat footage silently. Export highlights via D/B media jobs and validate formats, no blanket stream-copy. Show 'Recorded highlights' not 'full match'. Non-destructive by default.
- Collection example: HL2 > Episode 1, Episode 2; manual custom Convergence under Elden Ring. Global Funny tag works across all. Management unified but roles clearly separated (one Collection vs many Tags), game-only scopes, optional folder preference. Inline current Games/Recording profile links. Preserve existing physical folder disabled default and B gating.

## UI-04: Basic Settings
Rewind's presentation not a change to actual entitlement or encoder defaults. Use current Basic/Advanced/Guided Setup, SettingsView.RecordingDimensions/RecordingPresets, per-game profile store. General pages vs My Games, one consistent editor component; no duplicate independent settings. Keep full existing Recording controls in Advanced, including codec/hardware selection, quality, ratio, custom crop, bitrate, duration. 'All Games' plus explicit overrides inheritance/reset visible.
```text
Instant replay
How far back a saved clip reaches.
[15s] [30s] [60s] [Custom]   (display configured duration)
Only record while playing    [toggle, explain standby]
Video
[Performance         ] [Balanced            ]
[1080p • 30 FPS       ] [1080p • 60 FPS       ]
[~clip MB · ~RAM MB   ] [~clip MB · ~RAM MB   ]
[High quality        ] [Custom              ]
[1440p • 60 FPS       ] [Current settings     ]
Audio
Record game/system sound [toggle]  From [device/app when supported]
Microphone               [toggle]  Device [picker] [test]
```
Presets proposed labels and example dimensions only; apply using supported exact config + effective profile, no silently overwriting Custom or changing authorization. Resolution is height/aspect policy per existing controls, not universal fixed 16:9 promise. Show actual derived clip bytes, RAM, audio track count with estimate caveat (existing SettingsView.xaml.cs estimate). Reveal relevant options; app audio mode only after runtime validation, preserve Sonar endpoint selection. Setup steps keep master, authorized display/source, clip location, audio, quality, game detection, notifications; guide with tooltips/info icons for technical concepts. Advanced remains accessible, preserve all user preferences.
- Basic storage: clip folder, usage, open folder, optional retention only if explicitly enabled and recovery verified. Protect/favorites not blindly conflated; explain constraints and preview. Updates: existing check, notes, skip exact version; Diagnostics for actual capture source/error.
- No event/achievement controls, match stats pages, or game-specific integrations. Optional generic bookmarks only if separately supported; no promise of universal events.
## Acceptance
UI implementation gets screenshots at real DPI and widths, keyboard/accessibility, theme contrast, actual native buffer state, source selection/permissions, profiles and collections integration, Gallery navigation/draft preservation, no extra capture/polling when UI hidden. Compare against pinned Rewind screenshots by layout/clarity, not pixel identity. Owner reviews before changing defaults. No new functionality merely implied by mockups.
