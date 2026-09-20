# xur.app design direction

Based on Anthropic's current `frontend-design` skill, read from
`anthropics/skills/main/skills/frontend-design/SKILL.md` on 2026-09-20.

Audience: people who own GPU hardware and want desktops and local models on one
host. The first job is to explain profiles; the second is to get them to a guide.

Palette: paper blue `#eaf2fa`, ink `#162a40`, signal blue `#1766b5`, pale circuit
`#c6dbec`, teal `#087b70`, hardware navy `#12283d`. IBM Plex Sans, already licensed
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

Review against the brief: rejected a dark card grid because it looked like an
admin dashboard and repeated the application's layout. Use a light technical
handbook around a single dark hardware illustration instead. No pricing, sales
claims, sign-up form, analytics, stock photography, or fake benchmarks. The
examples are explicitly illustrations, not live telemetry or performance claims.
