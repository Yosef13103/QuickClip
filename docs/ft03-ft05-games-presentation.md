# FT-03 through FT-05 Games settings

The branch starts at published Epic discovery commit `f4fd3f921e057a25f43f62251d8e2b422eab3c45`. Its Steam discovery parent is `f0c3af8e4168766dcb499476c52621b4b75835b1`. The integration candidate contains both.

The monitor button changes the existing automatic-targeting override. A crossed-out monitor, a text status, and a per-game accessible action identify the excluded state. Manual entire-display recording can still include an excluded game if it is visible on the approved display.

Open a game row to rename it or inspect its detection settings. The details expand in the list, follow Windows reduced-motion preferences, and stay with the same catalog ID when filtering. Detection overrides and executable associations are under Advanced game detection. A manual executable check runs off the UI thread when that row opens. Source failures show actionable scan issues, and remembered entries stay visible after incomplete scans. An Epic entry can be enabled for targeting while its renderer is still unverified.

Steam, Epic, approved folders and manual entries keep their existing discovery and storage behavior. Effective per-game recording profiles depend on recorder integration. This change touches only the Games settings view, its tests, and this document.

Automated checks cover the 8-, 50-, and 200-game layouts at 1050×660 logical pixels, row recycling, inline height changes, search and expansion identity, accessible action text, and serialized targeting overrides. A supervised pass should check keyboard and screen reader announcements, motion on and off, 1080p at 125% and 150%, reconnecting an unavailable source, and a targeting toggle across restart on an isolated profile. Verify runtime suppression separately on the integration candidate.
