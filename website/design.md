# xur.app design direction

Based on Anthropic's current `frontend-design` skill, read from
`anthropics/skills/main/skills/frontend-design/SKILL.md` on 2026-09-20.

Audience: people who own GPU hardware and want desktops and local models on one
host. The first job is to explain profiles; the second is to get them to a guide.

Palette: app charcoal `#15181d`, surface `#20252d`, text `#edf1f7`,
action blue `#99c5ff`, rules `#373f4b`, and teal `#8ed7b7`. IBM Plex Sans, already licensed
and bundled with Xur, serves text and display headings. Headlines use compact
spacing and a deliberately large scale; body copy stays below 70 characters.

The distinctive element is a usable GPU allocation diagram. Profile buttons
reassign four illustrated slots so the concept is understandable before install.
Everything else stays quiet: left-aligned copy, broad whitespace, readable guides,
and blue/teal details tied to workload identity.

    logo                  Profiles   Guides   GitHub
    One host.             [ Gaming + AI | Model serving ]
    More possibilities.   [ GPU 1 ] ── desktop / speech
    explanation           [ GPU 2 ] ── desktop / speech
    guide / source        [ GPU 3 + GPU 4 ] ── language model
    examples / practical guide links / early-release status

The user requested a dark site matching the software on 2026-09-20. Keep the
editorial layout and interactive allocation diagram with the app's accessible
charcoal/blue palette. Header navigation is Guides, GitHub (with icon), Download now.
The download page checks public GitHub releases without adding a server runtime.
Homepage and guide screenshots come from the actual Razor renderer with synthetic
fixtures; see docs/screenshots.md. No pricing, sign-up form, analytics, stock
photography, or fabricated benchmark results.
