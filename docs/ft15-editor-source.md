# FT-15 / QC-008 editor source lifetime

The editor listens for confirmed recycle requests and rechecks the exact open path after Gallery deletion or Undo. A canceled or unrelated deletion leaves playback alone. A failed recycle resumes the original paused preview. Page and window activation check for external moves and unavailable files. Missing, locked and replaced files show a recovery panel and disable playback and export. The editor keeps the draft in memory. A source restored with matching file identity and stamp is probed again before the existing draft and preview are made usable. A replacement at the same path needs to be opened as a new source.

Export checks the open file before it shows a destination prompt, before launching FFmpeg, after FFmpeg completes and before Save Over touches the original. Failed or interrupted exports clean up their own staging file. The checks narrow the race around external filesystem changes. They cannot make a shell recycle operation and file publication one atomic transaction.

Verification command

```powershell
dotnet test QuickClip.slnx -p:RequireCaptureEngine=true --filter 'FullyQualifiedName~EditorSource|FullyQualifiedName~ClipDeletion|FullyQualifiedName~Export'
```

For a supervised WPF check, open a disposable clip, change its trim and title, and go to Gallery. Cancel deletion, delete another file, then delete the open source and return to Editor. Confirm playback and export stop, restore with Undo, and confirm the draft and paused preview are available. Repeat with a moved source, a locked source and a replacement at the same path. During an export to an empty directory, move the source once FFmpeg starts and verify the staged output is discarded. Leave existing files in the destination to verify that they are untouched.

Rollback by reverting the FT-15 feature commit. The patch adds no stored data or settings migration.
