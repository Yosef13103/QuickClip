# Custom clip-save notification sounds — draft owner acceptance

The user's choice is a local audio file stored as a preference, not copied to the app
or decoded on every save. The bundled shutter remains the default and the fastest path.

## Supported sources

WAV, MP3, FLAC, OGG, Opus, M4A, AAC, AIFF; maximum 16 MiB input.
QuickClip's existing FFmpeg decodes the first 3 seconds into bounded 48kHz 16-bit mono PCM.
The temporary PCM is held in memory (and cached by path/size/mtime), never written to disk.

FFmpeg is launched without a shell or stdin and with file-only input protocols,
subject to an 8-second timeout. Audio is routed to the user's exact selected WASAPI
render endpoint; a disconnected endpoint never silently falls back to another output.
No NAudio package, new service, or extra binary is bundled.

## Owner verification

Use a short WAV, MP3, and OGG file with different durations, then test the bundled
shutter restore button. Verify the volume slider and alternate output, an output that
disconnects, missing file after restart, silent/malformed files, repeated rapid saves,
and whether choosing a recorded playback lane captures the sound (as warned in Settings).
Check no idle CPU/memory growth; actual hardware audio validation is pending.
