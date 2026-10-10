# Screenshot and artwork register

- 2026-10-10: Private stationary head-camera comparison under
  `.build/evidence/calibration-ir-20261010/` (`head-ir.png`, 640×480 grayscale,
  and `head-rgb.png`, 1920×1080 colour), captured at approximately 12:24 UTC
  on xur-255 running host 26.10.032 and robot source `bd2f18c`.
  Purpose: check whether the infrared interface can see the arms and compare
  its coverage and contrast with the colour interface. IR shows both arms
  and grippers but the printed patterns have little contrast; RGB is well lit
  and shows much of both arms, with some lower links outside the frame.
  Camera-only FFmpeg captures issued no motor commands or configuration writes.
  Privacy review: private room/furnishings and robot hardware, no people or
  credentials visible. Keep ignored/private; these are diagnostic evidence,
  not release artwork. Reassess after lighting, camera or robot pose changes.

- 2026-10-09: Private read-only Lenovo RGB diagnostic capture family under
  `.build/evidence/calibration-rgb/`, captured on xur-255 using host 26.10.031
  and robot image source `55c5f1b`. Purpose: assess head-camera coverage after
  identifying that its saved by-id alias resolves to the grayscale infrared
  interface, while the colour capture interface has a distinct by-path alias.
  Capture at 18:13:53 UTC returned 1920×1080 colour pixels and decoded tags 00
  and 01 together, with tag 02 partly below the frame. No motor commands or
  calibration receipts are involved. Privacy review: robot hardware and
  private room/furnishings, with no people or credentials visible; keep this
  family ignored/private. These frames are hardware evidence, not UI
  screenshots or release artwork.

- 2026-10-09: Source review for the measured camera/tag setup panel and metric
  AprilTag JSON display, based on `a8c5a52` plus the isolated metrology prototype.
  The earlier private robot dashboard/setup/AprilTags captures remain retired
  as current UI acceptance evidence and require regeneration before release
  reuse. Actual local setup assets passed synthetic browser checks at 1440,
  390 and 320 px, including the blank template, authenticated API helper,
  save/clear flow and horizontal overflow. Synthetic API proof uses numeric
  observations only; no new screenshots or live camera frames were captured,
  used or published for this change.

- 2026-10-09: Private stationary calibration-coverage capture family under
  `.build/evidence/calibration-current/`: head/hand JPEGs extracted from the
  read-only app observation and the later `head-markers.jpg`/`hand-markers.jpg`
  with copyable marker data. Captured from xur-255 at 17:27 and 17:30 UTC,
  host 26.10.030 and
  robotics image source revision `a080b9de0cf3dc7d6e33887374491c5d6a858c60`.
  Purpose: assess current camera visibility before implementing metric marker
  poses or joint calibration. The head view is almost entirely black and
  returned 640×480 even when full resolution was requested. The 1920×1080
  hand view reads tag 01 in all three frames, showing fingers, tray edge and
  room without shoulder/elbow references. The cameras share no visible tag.
  No motor writes or movement commands were issued. Privacy review: private
  room, furnishings and robot hardware, with no people or credentials visible.
  Keep ignored/private; these are hardware evidence, not publication assets.
  Reassess after camera exposure, cover, mounting, marker or robot pose changes.

- 2026-10-09: The October 8 robot dashboard UI capture families below are retired
  as current acceptance evidence after setup/navigation and runtime ownership
  moved into the proxied robotics container. Their historical privacy reviews
  remain valid; keep the images private. Regenerate dashboard, setup, AprilTags
  and controller views against the combined app before publishing new UI images.
  No new screenshot was captured for this source change.

- 2026-10-08: Deployed robot dashboard acceptance capture family under
  `.build/evidence/robot-web/live/`, covering dashboard cameras/motor details,
  E-stop/reset, the AprilTag overlay and controller instructions on xur-255.
  Capture-specific bundle version, source commit, timestamp and privacy review
  belong in the ignored `receipt.json` beside the images. Purpose: validate the
  published container through Xur's authenticated proxy at desktop/mobile sizes.
  Treat live room/camera imagery as private; review every used capture for people,
  credentials and personal files. Do not publish these images. Regenerate after
  camera, marker, navigation, dashboard or emergency-stop behavior changes.

- 2026-10-08: Robot dashboard review family under `.build/robot/preview/`
  and `.build/evidence/robot-web/`, using the working tree based on `322b687`.
  The local Native AOT preview replays the already registered private head/hand
  marker captures and the 03:08 UTC motor profile. Purpose: review the dashboard,
  tag overlay and copyable data without live hardware access. All views are
  explicitly marked as saved evidence; no motor commands are issued. Privacy
  review: the reused captures contain room furnishings and robot hardware,
  without faces or credentials. Keep ignored/private. A shared-browser snapshot
  on October 8 at 21:57 MDT returned only a blank Chromium error page with an
  Electron renderer error; it is not an application screenshot and was not saved.
  Reassess after camera, layout, mounting or pose changes.

- 2026-10-08: Private motor-identification camera evidence family under
  `.build/evidence/robotics-motor-identification/`, including initial/clear
  head/hand JPEGs, per-test `ready`, `before`, `displaced`, `after` JPEGs and
  copies under `captures/{UTC-timestamp}-{selection}/`. Captured from xur-255
  during attended diagnostic tests from 20:26–21:04 MDT, using the working tree
  based on `322b687` and the pinned robotics tools image plus diagnostic scripts.
  Purpose: correlate single-motor encoder feedback with arm/tag/head/gripper
  responses and record aborted checks. Some tests issued small, capped arm/head
  commands; no wheel motion commands. Privacy review: private room, furniture,
  flooring, electrical outlet, robot hardware and the operator's hands during
  label adjustment; no faces or credentials visible in reviewed captures.
  Keep ignored/private. A hand obscures/moves tag 02 during the right-wrist
  test, so those images do not independently verify that axis. Tag 02 was later
  moved partly outside the head view. Reassess after marker/pose/camera changes.

- 2026-10-08: Live marker adapter/API evidence family under
  `.build/evidence/robotics-marker-survey/live-api/`: `head-markers.jpg`,
  `hand-markers.jpg` and the same JPEGs in `.build/captures/{job}/`.
  Captured at 20:03 MDT through the updated CPU robotics tools container and
  validated/served by the working-tree .NET task API based on `322b687`.
  Only camera devices were passed into the capture container; no motor commands.
  Purpose: verify the container's live three-frame detections, report persistence
  and image retrieval. Head reads 00/01/02; hand reads 01 in all three frames.
  Privacy review: private room, furniture, flooring, electrical outlet and robot
  hardware; no people or credentials visible. Keep ignored and private. The
  previews under `ui/` are test renders using these saved results, not a deployed
  robot-control page. Reassess after mounting, lighting, camera or pose changes.

- 2026-10-08: Private arm-tag assessment capture family under
  `.build/evidence/robotics-marker-survey/`: `head-initial.jpg`,
  `hand-initial.jpg`, their grayscale inputs, `head-repeat.pgmstream`,
  `hand-repeat.pgmstream` and extracted `analysis/frames/*.pgm`.
  Stationary 1920×1080 captures from xur-255 after the owner attached arm tags,
  with no motor commands. Working tree based on `322b687`. Purpose: infer marker
  locations from the images and assess repeated detection and camera
  co-visibility. Head reads 00/01/02; hand reads 01. Privacy review: private room,
  furniture, electrical outlet and robot hardware; no people or credentials
  visible. Retain as ignored private evidence and do not publish. Reassess after
  camera aim, marker mounting, illumination or arm pose changes. Five readable
  stationary frames do not validate movement coverage or joint calibration.

- 2026-10-08: First installed marker camera assessment family under
  `.build/evidence/robotics-markers-camera/`: `head-tag00.jpg`, `hand-tag00.jpg`
  and their grayscale detector inputs. Captured stationary 1920×1080 frames
  through SSH from xur-255 after the owner installed printed tag 00 on the tray.
  Working tree based on `322b687`; no motor commands. Purpose: check actual label
  readability and onboard visibility. Native `tagStandard41h12` detection read
  ID 0 with zero bit errors in the head frame; the hand frame has no detections.
  Privacy review: robot hardware and private room details, including flooring
  and furniture; no credentials or people visible. Keep these as private ignored
  evidence, not publication assets. Reassess after camera aim, tag placement,
  lighting or robot pose changes. A successful ID read does not establish a
  calibrated camera pose, measured tag size or full joint coverage.

- 2026-10-08: Robotics marker print-kit capture family:
  `.build/evidence/robotics-marker-sheet.png` and raster/PDF proof renders under
  `.build/evidence/robotics-marker-print-kit/`. Generated from the working-tree
  `tools/Xur.Robotics/print_markers.cs`, based on `322b687`, using pinned official
  AprilTag artwork. Purpose: review complete patterns, white margins, identifiers
  and scale; rendered sheets and individual labels also undergo native AprilTag
  detection. These are digital proofs, not evidence of successful physical
  printing or camera coverage. Privacy review: tag patterns, numeric IDs and
  printing instructions only; no room images, people or credentials. Retained as
  ignored development evidence. Regenerate after artwork, dimensions or layout
  changes. Artwork retains its upstream BSD-2-Clause notice.

- 2026-10-08: Private robotics camera assessment captures in
  `.build/evidence/robotics-usb/`: `emeet-stationary.jpg`,
  `lenovo-stationary.jpg`, `emeet-settled.jpg`, `lenovo-settled.jpg` and
  `lenovo-open-shutter.jpg`.
  Live stationary frames from xur-255, captured through SSH using the host's
  FFmpeg, with no motor commands. Working tree based on `322b687`; these are
  hardware evidence, not UI or release screenshots. Purpose: identify camera
  roles, exposure and visible arm references for marker calibration. Privacy
  review: the EMEET view includes a person and private room/chassis details;
  retain all frames privately under ignored evidence paths and do not publish.
  Reassess after camera mounting, lighting, robot pose or marker placement changes.

- 2026-10-07: Reviewed private user attachments `IMG_7977.jpg`
  (`7ce58f17-58f7-4d7b-96eb-04413a87deac-18ba470e-74b2-401f-94ba-b9787d7604c0.jpg`)
  and `IMG_7976.jpg`
  (`7ce58f17-58f7-4d7b-96eb-04413a87deac-38b184af-3470-4d52-9123-ca49dae27b0e.jpg`)
  as references for installer progress layout and ordering. Capture version/date
  are unverified; reviewed on this date. Show OS download/deployment, repeated
  Anaconda output and a wrapped chunk identifier. Privacy review: no credentials,
  account names or network addresses visible. Kept private in T3 attachments;
  not copied into documentation assets or published.

- 2026-10-07: Captured the simplified server-name screen at 100×40 and 40×12
  characters and its controller keyboard at 80×25 in
  `.build/evidence/controllers/console/`. PNGs are rasterized from actual ANSI
  console frames from the working tree based on `a9bfa2b`; purpose: review field
  focus, spacing, the single typing hint and contextual Continue/Back actions.
  Synthetic names and typed text only; no live device, credentials, addresses
  or account data. Private regression evidence, not publication assets or
  physical-display captures. Regenerate when console input layout changes.

- 2026-10-07: Reviewed private, in-memory T3 collaborative browser snapshots of
  the individual-controller profile editor at 320 and 1280 CSS px, from the
  working tree based on `a9bfa2b`. The underlying synthetic Razor fixtures and
  text test receipts are under `.build/evidence/controllers/`; no snapshot PNG
  was saved or published. Purpose: inspect controller choices, disconnected
  selections, unavailable identities and mobile checkbox sizing. Privacy review:
  synthetic controller serials and fixture profiles only; preview network
  addresses may appear in private browser metadata. No live host, credentials,
  accounts or model prompts were captured. Regenerate after controller/editor
  UI changes. The published profile-editor JPEG remains due for refresh.

- 2026-10-02: Reviewed private user attachment `IMG_7968.jpg`
  (`9a815e3d-21b5-4f73-aaf3-0d5dfb2d06a1-20c5cb12-93ab-4307-b982-d01a1790ce6e.jpg`)
  for the reported post-install boot problem. Shows Anaconda/dracut waiting for
  installer media; user subsequently confirmed the SSD reached management.
  Boot source at capture is unverified. No credentials visible; kept private,
  not copied into documentation assets. Relevant to installer boot-menu changes.

Review this file before every release. Add an entry whenever a screenshot is
captured, published, replaced, or removed. Keep raw captures and browser test
output under ignored `.build/`; only reviewed, sanitized documentation images
belong in `docs/assets`. Never capture access codes, API keys, private addresses,
usernames, file paths, account names, or model prompts in public screenshots.

## Published images

| Image | Used in | Type / source | Last review | Next review |
| --- | --- | --- | --- | --- |
| `docs/assets/xur-header.png` | README header; website social sharing | User-supplied artwork, unchanged | 2026-09-20; no private data or embedded metadata | Branding changes |
| `docs/assets/workstations-and-llm.png` | README example profiles; website home/model guide | User-supplied diagram, unchanged | 2026-09-20; illustrative allocation, not a benchmark | Workstation/profile changes |

No live-server screenshots are published. The diagrams above are artwork.
The following app screenshots are produced by `node eng/capture-website.cjs`
using the actual Razor renderer and synthetic test fixtures. No real host is
contacted. Dates refer to the reviewed working tree based on `f2d1d7c`; they do
not imply a released app version.

| Image | Used in | Capture / privacy review | Next review |
| --- | --- | --- | --- |
| `docs/assets/control-panel.jpg` | Website home; getting-started guide | 2026-09-20; synthetic Gaming/Studio profiles, no personal data, 1200×800 | Home/profile picker changes |
| `docs/assets/workstations.jpg` | Website home; workstation guide | 2026-09-20; synthetic desktops and GPU, user replaced with `example-user`, connection details collapsed, 1200×800 | Workstation management/layout changes |
| `docs/assets/profile-editor.jpg` | Getting-started guide | 2026-09-20; synthetic GPU and workstation, no account details, 1200×800 | Profile editor or USB assignment changes |
| `docs/assets/model-lab.jpg` | Models guide | 2026-09-20; synthetic target, blank prompt, no responses or benchmark results, 1200×800 | Model lab controls changes |
| `docs/assets/update-channel.jpg` | Updates guide | 2026-09-20; cropped to update-channel panel, Nightly selected, local server/key fields hidden; no network/account details, 944×327 | Update settings changes |

All five JPEGs were visually reviewed and have no EXIF or location metadata.
Guide captions identify their example data. Full-resolution images are linked for
readability; key actions are also described in text.

## Release review — 2026-10-04

Source review through `92fdea6` identifies the following refresh work. This is a
comparison with changed UI controls, not a new image capture or privacy approval.
The capture dates and privacy reviews in the table above remain unchanged.

| Image | Work before the next release |
| --- | --- |
| `docs/assets/profile-editor.jpg` | Refresh: predates inline rename, conditional workstation fields, Add user placement, shared Switch profile navigation and individual controller assignment. |
| `docs/assets/workstations.jpg` | Refresh: predates desktop previews, current Settings/display controls and the removal of profile-loading and launch shortcuts. |
| `docs/assets/control-panel.jpg` | Refresh: predates Home's per-display CEC controls and Displays navigation, as well as the current picker and shared Switch profile navigation. |
| `docs/assets/model-lab.jpg` | Review and refresh if needed for shared navigation; preserve blank prompts/results and synthetic fixtures. |
| `docs/assets/update-channel.jpg` | Compare the cropped selector with current Settings; refresh if its controls or wording differ. |

Regenerate from synthetic fixtures, review each image and its metadata, then
record the actual source commit, date and privacy outcome before publication.
See [release preparation](development/release-preparation.md).

## Development captures

- 2026-10-07: Captured and visually reviewed the private
  `.build/evidence/installer-progress/{download,deploy,complete,failed}-{40x20,80x25,100x40,140x50}.png`
  family and its `review.png` contact sheet. Rasterized from the actual ANSI
  console frames produced by the setup/agent fixtures, based on `a9bfa2b` plus
  the installer layout changes. Covers progress ordering, compact transfer
  counters, the divider before **Anaconda output**, repeated activity and the
  pinned diagnostics warning. Privacy review: synthetic disk identities,
  transfer counters and activity only; no live hardware, account names,
  credentials or network addresses. Private regression evidence, not published
  assets or captures of a running installation. Regenerate after installer
  progress or console layout changes.

| Capture family | Purpose | Storage / usage | Review |
| --- | --- | --- | --- |
| Website preview screenshots | Responsive homepage and guide design review | `.build/website/`; synthetic example content only, not committed | Rebuild and inspect when site layout or guides change |
| Browser regression screenshots | Desktop/mobile layouts: Home, Settings, Storage, Files, Workstations, profiles, endpoints, API keys, Model Lab | `.build/fast/`; test evidence only, not committed | Regenerated by `eng/test-fast.sh`; inspect changed pages before release |
| VM and live validation screenshots | Installation, consoles, diagnostics, streaming | `.build/evidence/`, `.build/vms/`; private evidence only | Review for secrets before any external sharing |
| User-supplied bug screenshots | Reproduce reported defects | T3 attachments; referenced in conversation, not copied into source | Keep private; add a sanitized published-image entry before reuse |

## Change log

- 2026-10-07: Added Diagnostic SSH in Settings and the system report download in
  Diagnostics. Reviewed the actual Razor fixtures at 1280, 390 and 320 px in the
  collaborative browser; the new controls have no horizontal overflow. Checked
  disabled/enabled SSH states with synthetic keys and blank key inputs. The full
  private Settings fixture includes development-host network addresses and stays
  under `.build/evidence/diagnostic-access-ui/`. Local preview navigation was
  unavailable, so rendered HTML and local styles were loaded directly for layout
  checks; real HTTPS tests covered form submission and authorization. No images
  were saved or published. Older private Settings captures predate these controls;
  the published update-channel crop is unaffected.

- 2026-10-07: Reviewed private T3 attachment `image.png`
  (`d967f238-2de3-4081-933c-47809af08b92-0f7301cd-4540-47dd-adb5-91f8829ab7a2.png`)
  for the Qwen-Image-2.1 catalog-resolution error. Shows the profile editor's
  Omni model, checkpoint size, NVIDIA selection and misleading network error;
  capture version is unverified. No credentials or account details visible.
  Kept private in T3 attachments; not copied into source or published.
  Read-only live catalog checks and an unsuccessful resolve reproduced the
  error without saving the profile or starting a workload. No new screenshots
  were saved or published.

- 2026-10-06: Added the automatic update schedule and shared upcoming-window
  notice in the working tree. The private `.build/fast/updates-{desktop,mobile}.png`
  captures predate these controls and must be regenerated before release reuse.
  Checked the actual Razor fixture and local notice script at 1280, 390 and
  320 px in the collaborative browser with synthetic versions and a simulated
  update API; no overflow or live host/account data. Preview navigation could
  not reach the fixture server, so the rendered document was loaded directly.
  Screenshot capture was unavailable; no new images were saved or published.
  The published update-channel crop is unaffected by these Updates controls.

- 2026-10-05: Added desktop previews in the working tree based on `1fd77ac`.
  The generated `.build/evidence/workstation-preview/desktop.png` fixture is a
  synthetic desktop illustration for private collaborative browser checks;
  its embedded window contains example text only, with no live desktop,
  account, credentials or personal files. Browser assertions covered fresh
  capture requests, failure/retry, and layouts at 1440, 390 and 320 px using
  the actual Razor fixture. Collaborative screenshot capture was unavailable,
  so no new page screenshots were saved or published. The published workstation
  image and the older `.build/evidence/workstation-design/` capture family now
  predate desktop previews and must be regenerated before release reuse.

- 2026-10-05: Regenerated and visually reviewed the private
  `.build/fast/control-panel/{desktop,mobile}.png` family from the real Razor
  renderer, based on `cf1a88b` plus the display-power changes. Covers the new
  per-display CEC controls and Displays navigation at 1440 and 390 px widths.
  Privacy review: synthetic Example TV, Desk TV and Office monitor fixtures,
  example profiles and connector IDs only; no real host, account, credential or
  personal address appears. Test evidence only; not published. Refresh this
  family after Home or display-control changes.

- 2026-10-05: Regenerated and visually reviewed the private
  `.build/fast/updates-{desktop,mobile}.png` family from the synthetic Updates
  Razor fixture, based on `404b1b2` plus the browser-test count correction.
  The 1440 and 390 px captures show all 18 tools, including the native AMD
  gfx1103 entry, without overflow. Privacy review: fixture versions and public
  image references only; no live host, accounts, credentials or model prompts.
  These replace the retired 17-tool captures as current regression evidence.
  Regenerate after Updates UI or engine inventory changes; not published.

- 2026-10-05: Source review for the native Radeon 780M vLLM change, based on
  `c750b2a`. The private `.build/fast/updates-{desktop,mobile}.png` captures
  with 17 tools predate the new native gfx1103 engine row and are retired as
  current release evidence; regenerate this family for the 18-tool inventory
  before reuse. The published update-channel crop does not include the engine
  inventory. No new screenshots were captured or published during the live
  AMD kernel and inference tests; their text evidence remains private under
  `.build/evidence/amd-fix/`.

- 2026-10-04: Regenerated `.build/fast/updates-{desktop,mobile}.png` from the
  synthetic Updates Razor fixture, based on `a72a66e` plus the GPU-capacity and
  browser-regression fixes. Covers all 17 tools, including AMD/Intel vLLM and
  Omni, at 1440 and 390 px. Visually reviewed layout and privacy: fixture
  versions and public image references only; no live host, account, credentials
  or model prompts. Private regression evidence; regenerate after Updates UI
  or engine inventory changes.

- 2026-10-04: Regenerated `.build/website/` responsive site capture families
  (`home`, `guide` and named guide/download pages at 1440, 768, 390 and 320 px)
  from source based on `92fdea6` plus the release-documentation edits. Static
  project content and synthetic examples only; no live account, password, model
  prompt or filesystem data is injected. Privacy review: fixture/site source
  checked, captures remain private regression evidence; review images before
  external sharing. Collaborative browser snapshots of the same local website
  may contain the development server address in metadata and remain private.
  Public JPEGs were not refreshed by this pass.

- 2026-10-04: Reviewed private T3 attachment `image.png`
  (`fbc21365-4ad1-43ca-8732-030c6b0d29d8-57f564a2-6767-41b0-8f70-f38cc6216217.png`)
  for AMD/Intel vLLM support. Shows the profile editor selecting
  `fishaudio/s2-pro` with AMD GPUs and the previous NVIDIA-only validation error;
  capture version is unverified. No credentials or account details visible.
  Kept private in T3 attachments; not copied into source or published.

- 2026-10-04: Reviewed the expanded profile-editor USB section in the working
  tree based on `0c3346c`. Private T3 browser capture
  `.build/evidence/profile-editor/primary-desktop.png` shows disabled USB choices
  and retained connected/disconnected selections for a primary workstation.
  Rendered from synthetic Razor fixtures; no live devices, accounts or credentials.
  Browser checks covered 1440, 900, 390 and 320 px. Refresh this capture and the
  published profile-editor image before release when primary/device controls change.

- 2026-10-04: Captured web Wi-Fi setup at desktop and phone widths in
  `.build/evidence/network-ui/wifi-{1440,390,320}.png`. Working-tree Wi-Fi web
  controls, rendered from synthetic Razor fixtures; shows a saved open-network
  connection and a WPA3 connection error with a cleared password field. Reviewed
  for layout and privacy: example SSIDs, adapter MACs and documentation-only IP
  addresses; no live hardware, credentials or account details. Private regression
  evidence, not publication assets. Regenerate when web network controls change.

- 2026-10-04: Captured the two-stick keyboard overlay in
  `.build/evidence/console-overlay/` in the `xbox-controller` worktree, based on
  PR #22 at `32dae01` plus overlay changes. `preview-{100x40,80x25,40x20,40x12}`,
  `typed-100x40` and `password-100x40` are rasterized from actual ANSI console
  frames with synthetic `xur` text and masked password input; no live device,
  credentials, addresses or account data. Private layout and regression evidence,
  not publication assets or physical-display captures. Reviewed 2026-10-04;
  regenerate when wheel layout, feedback, masking or terminal sizing changes.

- 2026-10-04: Retired `docs/assets/speech-and-llm.png` and removed its README
  and website uses because the illustrated model-specific runtime preparation
  was removed. User-supplied allocation diagram; privacy reviewed 2026-09-20,
  with no private data. Also rechecked the published profile-editor and model-lab
  screenshots: synthetic fixture data, no references to the removed runtime.

- 2026-10-02: Regenerated the private profile-editor capture family below for
  dropdown activation and the adjacent Add user button at all four widths.
  Chromium mouse and touch checks passed using synthetic fixtures; no live
  credentials or host details were captured. Native Safari remains unverified.

- 2026-09-20: Profile editor captures in `.build/evidence/profile-editor/`
  (`existing`, `new`, and `devices` at 1440, 900, 390 and 320 px) cover the
  working-tree inline Rename control, conditional workstation name/user fields,
  field order and device assignments. Generated from synthetic Razor fixtures;
  reviewed for privacy, with no live accounts, credentials or host addresses.
  The supplied profile-title and device-permission screenshots remain private
  T3 attachments. Refresh the published `docs/assets/profile-editor.jpg` before
  the next release; it predates these editor changes.

- 2026-09-20: Refreshed private Settings captures in
  `.build/fast/control-panel/settings-{1440,390}.png` for the recovery ZIP scope,
  credential warning and HTTPS requirement. Working-tree recovery-backup feature;
  fixture UI only, no backup payloads or secrets displayed. Review the backup
  text again if included identities or restore support changes.

- 2026-09-20: Refreshed workstation overview/expanded captures and added
  `display-dialog-{1440,390,320}.png` in `.build/evidence/workstation-design/`.
  Working-tree revision adds the JSON display dialog and adjacent client refresh
  control and removes profile loading and launch shortcuts. Synthetic JSON and
  account fixtures only; private regression evidence, not publication assets.
  Published `docs/assets/workstations.jpg` should be refreshed before the next
  release because its controls predate these changes.

- 2026-09-20: Added the five reviewed synthetic app screenshots above to the dark
  website and guides. Captured responsive site previews under `.build/website/`
  and recorded automated accessibility results there. No live application or
  user-supplied screenshot was published.

- 2026-09-20: Reviewed the user-supplied Workstations screenshot in T3 attachment
  `9fbd0a96-cc1e-4308-9589-a0c4bd9ec049-f266a2e9-91c6-4c56-a84e-7c34f7aa033b.png`
  as the redesign baseline; contains account names and device details, kept private.
  Captured the working-tree redesign at 1440, 390 and 320 px in
  `.build/evidence/workstation-design/` (overview, expanded settings/pairing and
  interim previews). Synthetic fixture data and documentation-only network
  addresses; development evidence only, not published. Recheck on changes to
  workstation layout, creation, pairing or profile actions.

- 2026-09-20: Regenerated private website captures for the MIT footer/home text
  and Settings captures for source/license links. The Cloudflare setup screenshot
  was used as a private reference only and was not copied into the repository.
  Also reviewed the Cloudflare application chooser attachment
  `c3be30b2-3576-48ae-a8ef-0cf8d96e4c12-3a1fa4f9-f8a3-4200-bf82-b573dc3743d0.png`
  to locate its “Continue to Pages” link. No credentials are visible; kept in
  private T3 attachments, not published. Recheck these directions when Cloudflare
  changes its setup interface.

- 2026-09-20: Regenerated private Settings regression captures at 1440 and 390 px
  for the combined update-channel selector and contributor public-key fields.
  Synthetic update server/key fixtures. The network panel includes development
  host addresses, so these captures remain private and must be sanitized before
  publication. No credentials or live screenshots were published.

- 2026-09-20: Registered the three README artworks and existing private test
  capture families. Reviewed the supplied README images for private information.
  No live UI screenshots were added to the repository.

For each new published screenshot, record its exact file, all pages using it,
source (synthetic fixture or sanitized live capture), application version/commit,
capture date, privacy review, and the features whose next changes require a refresh.


- 2026-09-20: Files grid regression captures in `.build/evidence/files-grid/`
  (`files-1440.png` and `files-390.png`), generated from synthetic workstation
  and mount fixtures. Reviewed for privacy: only sample paths and file names;
  no credentials or real user contents. Covers AG Grid, search, paths and menus.
  Regenerate when the Files page, grid theme or action menu changes.

- 2026-09-20: Reviewed private T3 attachment
  `45ce27e6-42b7-4cb8-9b41-a4af9e7d7128-3b3cbcc3-39b5-4395-9e22-ff60d3abf7ae.png`
  as the browser error redesign baseline (HTTP 409 and excessive navigation).
  No credentials or personal data visible; kept private, not published. The
  supplied capture predates the working-tree error message and recovery changes.

- 2026-10-04: Profile-switcher capture family: private collaborative T3 browser
  snapshots and `.build/evidence/profile-switcher/switcher-*.png`, covering the
  picker, workload review and responsive menu at 1280 and 390 px. Captured from
  the profile-switcher feature branch based on `0b95f64`; no released bundle.
  Uses synthetic workstation/model/profile fixtures, including a literal HTML
  string during the escaping check. Privacy review: no credentials, personal
  files or production state; development host addresses may appear in browser
  metadata, so keep these captures private. Regenerate after changes to the
  dialog, shortcuts or navigation. Review existing public UI screenshots for
  the new Switch profile navigation entry before the next release.
  Follow-up on 2026-10-04, based on `a72a66e`: the previous picker captures
  predate Unload all and the login-free desktop picker. Regenerate this family
  and the Settings/console images before release to include Profile access
  and the console Switch profile entry. Collaborative browser assertions used
  synthetic fixtures at 1280 and 390 px; screenshot capture was unavailable
  during this follow-up, so no replacement images were published.

- 2026-10-05: Desktop switcher hover capture family:
  `.build/evidence/overlay-dpad/hover-buttons.png` and `hover-profiles.png`.
  Private Qt Fusion renders from the Fedora builder, using the production
  stylesheet in `t3code/fix-overlay-dpad`, based on `1fd77ac`. The button grid
  compares normal, hovered, pressed and disabled states; profile rows compare
  hovered and selected text. Purpose: check contrast after the desktop switcher
  navigation and hover changes. Privacy review: synthetic control/profile names
  only, with no credentials, personal files or production state. These are
  focused style probes, not captures of a running workstation. Regenerate after
  changes to desktop control colors or states; review full desktop picker and
  review-screen captures before release.

- 2026-10-10: Stationary head camera capture-mode comparison family:
  `.build/evidence/robot-camera-controls-20261010/head-640x480.png` and
  `head-1920x1080.png`, captured around 16:55 UTC on xur-255 running Xur
  26.10.032 with the robotics container from commit `bd2f18c`.
  Purpose: compare actual arm coverage across advertised RGB capture modes
  before motorized re-aiming or segmentation work. No motor commands were sent;
  image controls were unchanged, and capture mode was negotiated for each image.
  Privacy review: visible robot arms, tags, tray, exposed camera board and nearby
  room surfaces; no people, credentials, screens or readable personal documents.
  Keep these live room images private and exclude them from source archives.
  Regenerate after camera mounting, head pose, capture mode or arm pose changes.

- 2026-10-08: SO-101 tag-mount prototype v1 CAD capture family:
  `.build/evidence/so101-tag-mount/upstream-links.png`,
  `upstream-sections.png`, `clip-review.png`, and `clip-on-link.png`.
  Private geometry inspection and prototype review renders from
  `tools/Xur.Robotics/cad/so101_tag_clip.py` and TheRobotStudio/SO-ARM100
  commit `a758567c3978dfeefe282ede0500085a48fe8f78`; no released application
  version. Shows the measured upper-arm rail, print orientation and proposed
  collar placement. Upstream-link images retain their Apache-2.0 provenance;
  original Xur mount geometry is MIT. Privacy review: synthetic CAD geometry
  only, no credentials, people, user files or live robot camera imagery.
  Regenerate if the source arm, clip dimensions, placement or label size
  changes. These renders do not establish physical fit or motion clearance.
