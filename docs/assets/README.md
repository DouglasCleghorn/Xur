# Xur branding

`xur-header.png` is the supplied GitHub README banner, preserved unchanged.

The supplied icon pack lives in `src/Xur.Control/wwwroot/icons/`. Its original
SVG and PNG files are preserved unchanged. The SVG is used in the app and login
headers and as the browser icon, with a 32px PNG fallback. The 180px PNG is the
Apple touch icon; the 192px and 512px PNGs are the installable web app icons.
The other supplied sizes are available for future integrations.

These are source assets and should be committed. No icon-generation step is
needed when building. The PNGs are declared as ordinary icons, not maskable
icons, so platforms do not crop the supplied mark into a different safe area.

`workstations-and-llm.png` and `speech-and-llm.png` are supplied allocation
diagrams, preserved unchanged. They illustrate example profiles, not performance
measurements or a certification of the named models. They contain no server
addresses, credentials, or personal account information.
