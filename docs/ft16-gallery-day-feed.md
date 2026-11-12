# FT-16 / QC-008 gallery day feed

The gallery groups visible clips by their local capture date, newest day first. Game, tag, search and favorite filters use the same grouping. Favorites-first applies within each day. A date header names up to two games and shows the remaining game count and number of visible clips. The day header, card rows and existing virtualizing panel share one flat feed.

Each day's checkbox toggles that day's visible clips. A mixed checkbox selects the rest of the day. Multiple days and individual clips share the existing selection. Filters keep hidden selections and the toolbar reports them separately. Batch actions operate on visible selected clips. Clear selection clears both scopes. Multi-clip deletion always confirms the exact count. Inventory reconciliation removes deleted paths from selection and leaves failed deletions selected.

The Share clip icon uses the existing clipboard action. The icon is keyboard focusable and has a tooltip and accessible name. The gallery keeps its hover playback, thumbnails, filters, projects and 1–4 columns.

```powershell
dotnet test QuickClip.slnx -p:RequireCaptureEngine=true --filter 'FullyQualifiedName~GalleryDayFeed|FullyQualifiedName~ClipDeletion|FullyQualifiedName~ClipAnnotation|FullyQualifiedName~ClipDetails|FullyQualifiedName~LibraryGallery'
```

For a bounded offscreen smoke test, run the Debug executable with `--render=<scratch directory> --gallery-smoke-only`. Use an isolated `QUICKCLIP_PERSONAL_DATA_DIR` and place synthetic clips in the scratch directory's `clips` subfolder. The harness reports realized rows and checks filtered selection and day toggles. It also writes gallery images without starting recording.

The Windows run passed 19 targeted tests with zero skips. The offscreen fixture used 1,000 clips across 50 local days, with 7 of 550 feed rows realized at first paint. Search found one clip and kept it selected. The harness also exercised mixed state and selecting and deselecting multiple days.

For manual acceptance with disposable clips, select three clips from one day and two from another, then deselect the first day. Verify the batch bar, mixed state, filtered hidden count, exact delete count, failed deletion and Undo. Tab to Share clip and check the clipboard. Resize across one to four columns at 125% and 150% DPI. Check hover, project navigation and filter scroll restoration. Mixed-DPI interaction and clipboard contents remain manual checks.

Rollback by reverting the FT-16 feature commit. The change adds no persisted schema or settings migration.
