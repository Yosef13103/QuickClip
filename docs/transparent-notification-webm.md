# Transparent WebM notification overlays — draft owner acceptance

QuickClip reuses its existing transparent WPF image overlay. It does not embed a browser,
new media-player DLL, or a desktop-pet framework.

## Why libvpx is mandatory

WebM's `alpha_mode=1` VP8/VP9 files carry alpha in a BlockAdditional side stream.
FFmpeg's built-in VP8/VP9 decoders may discard that stream silently. The custom
loader checks the alpha tag and explicitly selects `-c:v libvpx` or
`-c:v libvpx-vp9` **before** `-i`; it outputs BGRA to WPF.

Source: https://projects.blender.org/blender/blender/pulls/132795
Upstream context: https://www.mail-archive.com/ffmpeg-devel%40ffmpeg.org/msg189707.html

## Constraints

- Local files only, <=16 MiB compressed, alpha_mode=1, VP8 or VP9
- <=2048x2048 source pixels, <=30 seconds, <=120 scaled and frozen output frames
- Render dimension <=256px; process timeout 25 seconds
- No network protocols, no transparent fallback to an opaque decoder, no orphan FFmpeg processes
- Existing overlay timer stops when hidden, and frames remain cached for repeat notifications of the same file

## Owner verification

Test a real VP9-alpha WebM, a VP8-alpha WebM, opaque WebM rejection, and malformed or oversized files.
Try Light/AMOLED themes on both displays. Verify the actual background remains transparent
(and the notification itself stays excluded from recorded frames where supported).
Check idle/decoding CPU and memory, repeat notification behavior and cancellation.
This PR is draft until Windows hardware/visual acceptance.
