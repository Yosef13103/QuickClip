# Surround playback loopback

Personal's explicitly selected playback endpoints can preserve their Windows speaker layout through WASAPI and downmix to the existing stereo recording track. The change is adapted from [upstream QuickClip 6f44cc1](https://github.com/swiiftex/QuickClip/commit/6f44cc1027d2d69f398c06850a77b8b45dbc5ac9).

The path applies when a render endpoint advertises an extensible float32 surround mix with a valid speaker mask. WASAPI still converts to QuickClip's recording sample rate. Front left and right have unity gain, surrounds and center use −3 dB, and LFE uses −6 dB on both sides. If the weighted sum would exceed full scale, both outputs are scaled together for that sample. This preserves stereo balance and can reduce the level of loud, correlated surround content.

Mono, stereo, and formats without a usable layout retain the existing WASAPI conversion. If Windows rejects the surround format, QuickClip retries stereo on the same endpoint using a fresh audio client. Microphones, process-loopback capture, track ordering, AAC encoding, and MP4 muxing use their existing paths.

## Supervised playback check

1. In an isolated Personal profile, select a surround render endpoint under **Settings → Audio** and enable capture diagnostics. Use an explicitly authorized recording target.
2. Play a known left/right reference followed by isolated center and surround tones through the selected endpoint. Keep the rest of the playback devices quiet.
3. Save a short clip and inspect the named track locally. Compare left/right level to the source, check center on both sides and surround placement, and check that other tracks retain their independent signals.
4. Repeat with stereo playback. Check the capture log for the negotiated capture channel count and any same-endpoint stereo retry.

Synthetic tests verify channel mapping, front-channel gain and overload handling. The real device test checks WASAPI format negotiation and driver behavior.
