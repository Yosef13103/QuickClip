# Notification overlay redesign — Wave 1 follow-up

Owner approved the prior frontend pass, then requested a separate notification redesign before V1.0.1.

## Research and visual direction

- https://fluent2.microsoft.design/components/web/react/core/toast/usage — predictable location, concise primary message, status icon, optional context, avoid covering work.
- https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/app-notifications-ux-guidance — relevant, valuable, non-noisy notification content.
- https://fluent2.microsoft.design/motion and https://learn.microsoft.com/en-us/windows/apps/design/motion/ — short eased entrance/exit, reduced motion. No new external dependency.

Compact: rounded themed surface, semantic status glyph, high-contrast title, muted wrapped context. Detailed save only: title/clip name, divider, labeled metadata rows. No thumbnail, custom media, GIF, WebM, or image decoder in any banner. Image only: transparent asset, no persistent card/text. A ripple uses a real expanding perimeter with reserved transparent space; Soft pop and Lift/fade add choices. All banner anchors except exact center are supported. New image settings default top-right and 3s; banner default 1.5s, independent placement, size, opacity, motion and timing. Existing image-only profiles migrate their appearance to image settings, reset banner appearance to defaults; old banner preferences otherwise preserved. Existing explicit image position retained. Missing/invalid image falls back to compact when banners enabled; otherwise no image (Windows channel remains).
 
## Routing

Visual notifications master and per-event filters retained. Windows-only channel + image toggle displays regular Windows notifications plus image on successful save only. Overlay-only / both channels allow compact banners for all other eligible events; Saved uses selected image OR detailed OR compact, not both local formats. Windows channel remains independently controlled. Critical capture warnings keep their separate existing five-second readable override, do not use images. A single no-activate overlay replaces prior event; no continuous animation while hidden. Windows reduced-motion setting still controls animated assets and entrances; existing bounded image decoder, native no-activate/click-through, capture-exclusion attempt and elapsed-time frame scheduler retained. Exclusive fullscreen/capture exclusion hardware/API limitations remain.

## Acceptance

Test every channel combination, image toggle and detailed toggle, per-event switches, visual master, independent sound. New profile banner 1.5s and image top-right; existing 6s image profile migrates; record start/stop/replay enabled/failure/inactive never decode or show image, including during image-only mode. Save uses only requested local presentation, Windows-only+image works. Missing/invalid GIF/WebM fallback, image controls independent, details hidden in image mode, no center option for banners; preview both. Exercise all themes, 100/125/150% DPI, large and negative-origin monitors, long field values, ripple and all transitions, reduced motion, repeated rapid saves, idle timers/resources, UI focus and click-through. Test actual Windows appearance, capture exclusion and exclusive fullscreen limitations. Existing clip media and recorder unchanged.

Original Wave 1 acceptance remains required and no stable merge or release is made by this change.

## Bundled camera animation

Owner selected 512x512.gif (same bytes in ZIP), 295103 bytes, 512×512, 85 frames, 2590 ms, SHA-256 9a447241222c1412a058495ac183b662314a6e313b504b6f63c7a362cccc9fad. Place the exact bytes at src/QuickClip/Assets/clip-saved.gif. Personal embeds it as QuickClip.clip-saved.gif like the WAV chime. A null custom path means built-in animation; custom paths, image-mode/channel/event preferences and independent timing/placement remain intact. Restore default clears only custom path. Decode only on first saved-image event or explicit preview, no download or extraction. Image mode remains opt-in (default top-right, 3s). Invalid custom assets still fall back to enabled banners. No other event shows an image. Source-only checkpoint builds remain possible, but Personal publish is blocked until the asset exists; resource tests validate exact bytes and frame timing once present. Do not release until asset is committed, visual preview accepted, and redistribution permission verified.
