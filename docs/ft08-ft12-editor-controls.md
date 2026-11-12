# FT-08, FT-12 and FT-13 controls handoff

This follow-up is stacked on FT-16 PR #15 at `e367d62`. Review it against `feat/ft16-gallery-day-feed` and integrate after FT-16. FT-15 PR #11 is an independent companion fix for source validity and draft recovery.

- Gallery bulk deletion and the shared destructive confirmation use one danger style. The red surface has explicit hover, focus and pressed states. The confirmation still defaults to its safe action. Editor prompts name both actions for discarding edits, replacing an original, and keeping both files when recycling fails. FT-16's exact-count confirmation remains in place.
- The timeline defaults to 54-DIP tracks and a 44-DIP ruler. **Taller tracks** restores 74-DIP lanes. The divider has a visible grip and supports dragging or keyboard Up/Down while focused. The chosen height is kept in the editor session and constrained on resize.
- A plain wheel gesture over the audio tracks scrolls vertically. Ctrl+wheel zooms around the pointer, Shift+wheel pans, and the existing zoom buttons and keys still work. The waveform, gain, solo and export controls keep their existing data paths.
- Games settings are a separate workstream. Remove Game wording and the informational disabled-game treatment belong with that UI.

```powershell
dotnet test QuickClip.slnx -p:RequireCaptureEngine=true --filter 'FullyQualifiedName~Timeline|FullyQualifiedName~ClipDeletion|FullyQualifiedName~AnimatedReveal|FullyQualifiedName~GalleryDayFeed'
```

For a bounded visual check, use the Debug executable with an isolated profile and `--render=<scratch directory> --editor-smoke-only`. Put a disposable synthetic clip with multiple tracks in the scratch `clips` folder. Check compact and taller lane images, then manually test resizing, wheel/trackpad, keyboard focus, and confirm/cancel at 125% and 150% DPI. Use disposable files for deletion or replacement. Revert this feature commit to roll back. No settings or media migration is needed.
