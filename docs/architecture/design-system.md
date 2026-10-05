# Xur interface

Reviewed Anthropic's frontend-design skill at commit
`41bbe19d1a1a7eaab5e7bb9050a417e5c6cffc8f` before this redesign.

The product is an appliance manager for switching GPU workload sets. The visual
priority is knowing what is loaded and changing it. A gallery of large cards
makes that comparison harder, so profiles use aligned rows and one clear active
state instead. No hero, promotional copy, decorative statistics, or animations.

Tokens: Graphite `#15181d`, Surface `#20252d`, Rule `#373f4b`, Text `#edf1f7`,
Secondary `#b0bac8`, Action `#99c5ff`. Green identifies running state; amber
identifies pending changes; red is reserved for errors and destructive actions.
IBM Plex Sans is bundled locally. Titles are 28px, profile names 18px, body and
controls 15px, secondary information 14px. Status must remain readable without
being the brightest thing on the page.

Desktop uses a 192px navigation rail and a fluid, left-aligned content area.
Controls are 36px high, with an explicit primary/secondary/quiet/destructive
hierarchy. Mobile uses 44px touch targets, wrapping profile rows and a More menu
for secondary navigation and profile actions. No capability disappears on mobile.

```
Profiles                                       Create profile
Loaded: Profile 2           2 workloads         Unload profile

Saved profiles                                  Search
Profile 1     Gaming workstation       1 GPU     Load  Edit More
Profile 2     Qwen + speech            4 GPUs    Loaded Edit More

Running workloads
Name                  Engine       Devices       State   Logs
```

Review against the brief: a large active-profile hero would repeat the original
wasted-space problem, so the loaded state is a compact strip. Running workloads
use rows with human names; process IDs and routes are secondary details. The
profile editor keeps the existing automatic names and IDs and uses aligned
fields. Delete lives in each row's More menu; unload is directly visible beside
the loaded state. Confirmation pages describe the actual affected workloads.

GPU monitoring follows the same visual system. Each detected card gets a compact
panel headed by its actual name and assigned workloads. Four current readings
(utilization, VRAM, power, temperature) precede two always-visible history plots
for VRAM and power. Technical details, process ownership, and additional plots
are expandable. Desktop uses two columns; phones use one with the same controls.
The time range is shared across charts. Blue means memory, amber means power,
green means activity, and coral means temperature. Missing telemetry is text,
never a fabricated zero line. No aggregate hero or decorative status tiles.

The profile editor pass (2026-09-20) follows the current Anthropic
`frontend-design` guidance while retaining these colors and IBM Plex Sans.
Its title is the single place to name the profile: Rename replaces the title
with an inline editor, with Enter/Done to accept and Escape/Cancel to discard.
The name is persisted with Save profile. Each workload starts with its type
and a quiet Remove action, then workstation/model selection and GPU assignment.
Only a new workstation shows its name and user fields, below the GPU selection;
USB and audio stay in a disclosure beneath them. Desktop fields align in two
columns and become one column on narrow screens. Add workload belongs below
the workload list; Save profile and Cancel share the final action bar.

Review against the brief: an always-visible profile-name form duplicated the
title, and placing user selection beside workload type made related fields
harder to follow. Keep one title and follow the assignment order, without new
decorative cards or a separate visual theme for this page.

## Profile switcher (2026-10-04)

Verified the latest Anthropic `frontend-design` skill: commit
`41bbe19d1a1a7eaab5e7bb9050a417e5c6cffc8f` (2026-09-03). The switcher is a
short, focused interaction for keyboard, controller and touch users, including
people sitting farther from a gaming display.

Retain Graphite `#15181d`, Surface `#20252d`, Rule `#373f4b`, Text `#edf1f7`,
Secondary `#b0bac8` and Action `#99c5ff`; green retains its running-state meaning.
Use locally bundled IBM Plex Sans, with 26px titles, 20px profile names and 15px
supporting text. Center the dialog while keeping its contents left aligned.
Profile rows are at least 76px high, with a clear focus outline and a compact
loaded badge. Keep the list scrollable and the controls visible on small screens.

```
Switch profile                                      Close
Loaded: Gaming desk
Search profiles

Gaming desk                          Loaded
Gaming workstation, Assistant
Studio desk
Studio workstation, Speech

Arrow keys / D-pad: move       Enter / A: review
```

Selecting a row opens a second view with the actual Keep, Stop and Start
workloads, followed by Load profile and Back. Opening or selecting never applies
a profile. The opening controller chord must be released before navigation.
Errors retain a recovery action; an empty list links to profile creation.

Review against the brief: a grid would spread controller focus across two axes
and repeat the existing management page. Use a single vertical list instead.
Spend visual emphasis on the focused profile; keep shortcut hints and secondary
actions quiet. The desktop picker and web dialog share the same vocabulary,
palette and review flow. No animated decorations or remote rendering assets.
