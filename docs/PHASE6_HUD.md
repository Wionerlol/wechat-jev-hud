# Phase 6 — Anchored HUD

Status: **IN PROGRESS**, not PASS. Branch `codex/phase-6-hud`, base/target `main`.
The real dual-monitor Demo and Remote WeChat → Jev → HUD gates require human operation.
No changes to Phase 4.5 perception or Phase 5 inference algorithms are authorized here.

## Architecture

`Runtime.PerceptionRuntime` shares the existing production object construction between
Diagnostics and App: exact existing detector, Unified Paddle, Adaptive failure-only
fallback, Observer and options. No matching, OCR, trust or identity changes.
App's background `HudRuntimeCoordinator` runs capture/Observer and submits eligible
targets to the existing asynchronous Jev coordinator. Overlay consumes typed results,
never console text. Observer and TypeSafe have no Overlay/WPF references.

Overlay owns pure `JudgmentComposer`, `OverlayCoordinateMapper`,
`OverlayLayoutEngine`, `HudLifecycle`, immutable presentation/scene records, and
`WpfOverlayPresenter`. `HudPresentationPolicy` chooses chronological priority independently
from geometry; `OverlayLayoutEngine` chooses one safe right-side rail and its density.
One transparent host HWND/Canvas contains a shared rail and tiny keyed bubble anchors,
not one large card beside every message (D-036). A small controller
owns Stop/status; `-HudDebug` additionally shows the latest complete typed diagnostic
result, including all judgments/distributions/confidences/model/context count/timings.
It does not show source message text. Normal mode hides failed analyses silently.

## Coordinates and window behavior

- Observer bubble rectangles: capture-relative physical pixels.
- CaptureBounds: physical virtual-desktop pixels, including negative coordinates.
- Desktop bubble origin: capture origin + bubble origin.
- Host-local DIPs: bubble pixels / (current DPI / 96). Never divide desktop coordinates
  by a guessed monitor scale.
- PMv2 application manifest remains active. Explicit HWND `SetWindowPos` positions
  the host exactly on CaptureBounds; WPF scales content for the actual monitor.
- WS_EX_TRANSPARENT, WS_EX_NOACTIVATE, WS_EX_TOOLWINDOW, borderless/transparent,
  ShowActivated=false, ShowInTaskbar=false, nonfocusable Canvas. WM_MOUSEACTIVATE
  returns MA_NOACTIVATE; WM_NCHITTEST returns HTTRANSPARENT. No input injection.
- A 33 ms Dispatcher timer consumes a latest-wins scene mailbox. Perception never
  waits for rendering. Native foreground/visibility/minimize/physical-bounds/DPI
  checks hide stale placement even while OCR/network is busy. Restore requires a
  fresh scene; no reuse of the last pre-background scene.
- WeChat itself must be foreground. Controller/other-app foreground hides the host.

## Shared semantic rail (current; D-036)

Each active target retains its own epoch+message ID, result and 20 DIP bubble-local
anchor when onscreen. A neutral numbered marker links it to a stable chronological
ordinal in the rail (newest has the highest number within this epoch). Markers use
a 4 DIP gap to the bubble, follow its bounds, and must stay in
the ROI without covering any bubble or another marker. If neither side safely fits
a marker, that marker is omitted with the target still represented in rail metadata.

Default policy: newest tracked Pending/Ready item Expanded (even offscreen); up to two previous active
items Compact; remaining items represented by a `+N` footer. Capacity is configured
through `HudPresentationPolicyOptions`, independently of geometry. Selection is by
creation sequence, never Y or API completion order. A late older result cannot displace
the newest Expanded target. This is presentation only, not burst re-analysis.

Compute a single right-edge rail strip inside the chat ROI with 16 DIP margins.
Prefer 280 DIP width, then 220 DIP minimum. Remote bubbles normally leave this strip
free; Self/long bubbles and anchor markers divide it into safe vertical intervals.
Choose the largest safe interval (upper interval breaks ties). Rail rectangles must
avoid every supplied bubble and marker, with 4 DIP vertical clearance. Stack items
with an 8 DIP gap; do not search independent full-card positions or enlarge collision
tolerances. Ready Expanded height is 280 DIPs; Pending is 48. Compact is 56 DIPs.

If Expanded cannot safely fit, try a 200/160 DIP Compact rail with the newest item
first. If that cannot fit, use a 64×28 DIP `Jev · N` indicator plus anchors. Hidden
prior details increase overflow count; they do not become persistent state changes.
If even the counter cannot fit without covering a message, retain only safe anchors
and log `no_safe_rail_area` with newest key and active count. Never use arbitrary
desktop space or leave an older result posing as the newest. All dimensions are DIPs,
derived inside current capture-relative ROI; negative desktop origins remain physical.

The earlier 210×136 and 300×64 bubble-adjacent layouts (D-034/D-035) are historical,
superseded by the rail. Their real geometry is retained as regression input, not the
current display contract. Read-only/click-through/no-activate and capture safety are
unchanged. New rail visual/manual acceptance remains pending.

D-036 automated gate: native Windows format verification and full build passed with
zero warnings/errors; 338 .NET tests passed without failures/skips, including 46
Overlay tests. Frozen perception, Jev, lifecycle and native audit modules were not
changed. Real two-message and rapid-message rail acceptance remains pending.

## Fixed display policy

No thresholds, prompt changes or calibration from appearance. Expanded groups:

- Primary: speech act label + selected option probability.
- Conversation: expects_response, references_prior_context, contains_direct_request,
  expresses_disagreement_or_correction, contains_time_or_plan_commitment; all use
  Noul yes probability.
- Intensity: urgency and textual emotional_intensity, weighted Score `/3`.

Compact uses two lines: speech act + selected probability, then response and prior
context probabilities. The four original summary values remain in the presentation
model for diagnostics, not as a four-row layout requirement:

1. Selected speech act's Chinese label + **selected option probability**.
2. 期待回应 + Noul **yes probability**.
3. 依赖前文 + Noul **yes probability**.
4. 紧迫度 + **weighted Score**, one decimal, `/3` (never percent).

Choice/Score distribution confidence is retained in debug data, not substituted.
Noul .52 remains 52%, not categorical certainty. Labels:

| Choice | Label |
| --- | --- |
| question | 询问 |
| request | 请求 |
| answer | 回答 |
| acknowledgement | 回应/确认 |
| clarification | 澄清/纠正 |
| complaint_or_concern | 担忧/抱怨 |
| planning | 计划 |
| joke_or_banter | 玩笑/闲聊 |
| information | 信息 |
| other | 其他 |

## Lifecycle

Only successfully queued, trusted Remote LiveNew targets create Pending cards.
Keys are epoch + logical message ID. Successful same-key results compose Ready;
failures hide without fabricated rows. Self, History and Bootstrap never create cards.
Multiple requests/results retain individual keys, not a mutable latest-message slot.

Each healthy stable observation updates exact-ID anchors. Absence immediately sets
OffscreenRetained and clears CurrentBubbleRect, retaining LastKnownBubbleRect,
presentation/result, sequence and DisplayOrdinal. The rail shows `屏外`; it does not
invent above/below direction. Exact same-epoch logical-ID return restores the anchor
and its original ordinal. Different IDs never inherit results, even with identical
text/geometry. No Jev replay. This supersedes the earlier scroll-out retirement /
no-resurrection rule (D-037); Observer's historical identity remains best-effort.

Ordinals are chronological at successful scheduling, never presentation-list indices;
newest-first rail may read 5 Expanded, 4/3 Compact, +2. Default capacity is 25 semantic
items; `hud.ps1 -MaxTrackedSemanticItems 20` changes it. Oldest sequence is evicted
deterministically, without renumbering survivors; late results for evicted items are
ignored. Only eviction, confirmed epoch change, reset or shutdown retire items.
Unavailable analyses are retained but hidden. No Observer buffer is used as HUD history.

Foreground loss, minimize, unavailable capture, pending identity and layout transitions
hide without counting disappearance. Header/chat pixel changes provisionally hide
before waiting for Observer/OCR to resolve identity. Confirmed epoch change clears
all old items immediately and resets numbering. Scheduling still requires all frozen
semantic hard gates. Once accepted, an item's rail/result lifetime is independent of
later Observer-buffer membership/current-view trust. A same-key result may complete
offscreen; stale/evicted/unknown keys are rejected. No persisted cards or profile.

## Recursive capture protection

D-037 automated validation: native Windows format/verify/full build passed, zero
warnings/errors; all 348 .NET tests passed (56 Overlay), zero failures/skips. Retention
and chronological-ordinal regressions were red on old code. Real push-out/scroll-back
and stable-number evidence remains pending. No capture or semantic pipeline changes.

`WDA_EXCLUDEFROMCAPTURE` is requested on the host. **API success is not acceptance.**
Before processing real frames with HUD enabled, the runtime runs an in-memory audit:

1. Show a known 64-DIP colored marker on the actual host with affinity temporarily off.
2. Force desktop capture and require the marker as a positive control.
3. Enable exclusion and capture the RenderWindow and forced desktop paths again.
4. Require marker-free RenderWindow, preserved foreground, correct HWND styles and
   exact physical host bounds before permitting perception/display.
5. Hide the probe in `finally`. No probe frame reaches Observer/OCR or disk.

The probe reports whether desktop exclusion works, but **all VisibleDesktopFallback
frames are discarded in HUD mode**, even after an apparently passing desktop probe.
There is no screenshot-cleaning heuristic. If RenderWindow/audit is unavailable the
HUD remains hidden and those frames never enter perception. Two consecutive equivalent
tracker snapshots (HWND, render HWND, capture bounds, monitor and both DPI axes) are
required before auditing. A fresh snapshot after the probe must still match. Movement,
DPI changes and foreground loss are retryable, not permanent failures. Stable safety
failures latch only until configuration changes, avoiding a 200 ms retry loop.
ForegroundPreserved measures only foreground HWND preservation; configuration stability
is separate evidence. Presenter stale-geometry hiding never changes audit state.
The normal diagnostics command remains available without HUD.
Render-HWND, bounds, monitor and DPI transitions require a fresh probe. The brief colored patch is diagnostic,
not a semantic HUD or acceptance of text extraction.

## Commands (Windows PowerShell)

```powershell
.\scripts\hud.ps1 -CaptureAudit -Seconds 20
.\scripts\hud.ps1 -Demo -HudDebug
# Explicit upload opt-in, inherited local TYPESAFE_API_KEY only:
.\scripts\hud.ps1 -Jev -HudDebug
```

Keep WeChat foreground for the audit and Demo. Demo uses a synthetic capture-relative
test rectangle, not detected message identity, and creates no OCR worker/API request.
Without `-Jev`, the real observer does not upload semantic text. Never paste a key
into chat or a committed file. A new Windows shell may be needed after setting a
User environment variable. Stop via controller button. `-Seconds N` auto-stops for
bounded smoke. Existing manual Phase 1 capture remains `App --capture-debug`.
An unsigned UNC script may need process-scoped `powershell -ExecutionPolicy Bypass`;
no machine-wide execution-policy change is required.

## Metrics and privacy

Optional debug diagnostics include capture, change detection, bubble detection, OCR,
compose, layout, Jev queue/roundtrip/total and UI dispatch/update times. A Ready UI
update records total time from the start of the first captured frame containing the
NEW target. This is a sampled first-capture-to-UI-update proxy, not server message
arrival time or a physical display scan-out measurement. Frame polling adds latency.
No claims of measured live end-to-end performance until a real Remote sample runs.

No screenshots, API payloads, message text or logs are persisted by default. The user
may explicitly redirect redacted diagnostics to private `.ocr-cache`. Demo has no API
and must not be represented as real message/Jev acceptance.

## Verification record

- Initial coordinate and lifecycle tracer tests failed on missing implementation,
  then passed. A deterministic layout assertion initially used ImmutableArray reference
  equality; corrected to sequence equality, without weakening placement requirements.
- Native Windows focused suite: 24 tests passed, including real HWND styles,
  no-activation show/hide, physical bounds, hit-test/mouse-activate return values,
  affinity configuration and cleanup. This is not proof of human typing continuity,
  task switcher appearance or actual pixel capture exclusion.
- First bounded real capture-audit launch exited without an audit result. Foreground/
  visibility prerequisites were not established by the log; the controller must be
  checked with WeChat foreground. **Not a passing capture test.**
- Final Windows format and full build passed (zero warnings/errors). Full .NET suite:
  **316 passed**, zero failed/skipped: Overlay 24, TypeSafe 40, Observer 176,
  OCR 63, Vision 6, Windows 5, Capture 2. Python/model benchmarks were not rerun:
  extraction code is unchanged. No new TypeSafe API calls or private chat uploads.
- Local two-axis self-review used repository instructions/specification. The generic
  review skill's issue-tracker configuration is absent; no unrelated tracker setup
  or architecture rewrite was added to satisfy tooling.
- Shared-runtime native observer smoke (no Jev, three-second observation window):
  dual-model GPU worker initialized once; 9 frames, 8 unchanged, 9 bootstrap OCR calls,
  zero NEW, zero Paddle failures/fallbacks. This verifies the extracted construction
  path, not the real HUD/Remote acceptance. No screenshot or raw text was exported.

### Required manual sequence (not yet passed)

Initial Demo review confirmed card visibility, window-move following, Alt-Tab and
minimize hiding/restoration, and uninterrupted typing focus. Initial monitor transition
exposed conflated foreground/configuration evidence and a global failed-audit latch.
The scoped state-machine correction has 6 additional deterministic cases (30 Overlay
tests total). On the corrected runtime (`afc8521`), the user confirmed automatic
restoration after both monitor transitions, without restart. Native logs independently
show DISPLAY5/96 DPI Verified (e.g. generation 6), followed by DISPLAY1/144 DPI
Verified (generation 30), with further successful round trips. Positive control,
RenderExcluded, DesktopExcluded, ForegroundPreserved, ConfigurationStable, styles
and physical bounds all passed on those audits. No hard-failure latch occurred.
The user subsequently confirmed all three post-fix regressions: typing focus is not
stolen, Alt-Tab hides/restores, and minimize/restore hides/restores. The scoped
cross-monitor audit-state-machine regression is accepted. No real Jev HUD test yet.
Correction validation: Windows format/full build passed, zero warnings/errors;
full .NET suite 322 passed, zero failed/skipped. Frozen perception/TypeSafe code unchanged.

### Two-message Ready-render regression (2026-10-01)

The first real Remote path rendered one correctly keyed Pending/Ready HUD; user
confirmed no duplicate card or focus theft. Model `jev-1.13.0`, one batched request,
OCR 21.9 ms, HTTP 1604.8 ms, sampled capture-start to Ready 1967.4 ms. Later
two-message runs exposed a blocking layout case: older question result stayed
visible while the newer request result had Success but no Ready-render evidence.
The preserved log proves the newer Pending rendered and Success reached the Apply
call; it did not record Apply's return or the later gate/layout stages. Do not
claim an old Apply success from that incomplete evidence.

A no-OCR/no-API/no-image-export geometry replay of the existing real viewport
reproduced the failure: 96 DPI, ROI (251,80,707,344), older bubble
(316,318,127,36), newer bubble (316,374,113,36). Newer right anchor x=439
intersected the preceding bubble ending x=443 when vertically shifted; left
placement overflowed. Old layout omitted the newer completed card and placed
the older one. Both native geometry replay and the deterministic lifecycle→layout
regression were red before the layout correction and green afterward. The nearby
right candidate x=453 clears the wider neighbor, places the newest result, and
drops the older card for card collision in this constrained view. No geometry,
trust, semantic filter, Apply eligibility or disappearance rule was relaxed.
Scaled 96/144-DPI and bounded-horizontal-placement regressions are included.
Windows full build: zero warnings/errors; full .NET suite: 328 passed, zero
failed/skipped. Final diagnostic-field update was additionally checked with all
36 Overlay tests. Format passed; private replay files remain gitignored.

`--hud-debug` traces target/schedule, retained lifecycle visibility/stable ordinals,
Apply result/reason, current semantic display gate, each layout input/output and
rejection counts, scene generation/submission, Tick actions and rebuilt Canvas
rows. UI traces are queued for background serialization; no source chat text or
key is included. Traces diagnose current evidence without authorizing display.
That corrected retest remained pending; further real chat reproduced Success/Apply
without Ready rendering for shorter messages, establishing the fundamental density
limitation. D-036 supersedes independent-card collision fixes. First verify the new
two-message and three/five rapid-message rail; do not resume the lifecycle matrix
until those presentation gates pass.

1. Demo/audit with real foreground WeChat at 150%: confirm card style, unobstructed
   message/composer, typing focus and click-through. Audit must report positive control
   and RenderExcluded=true; inspect desktop result separately.
2. Move/resize narrow/wide, move to 100% display, back to 150%, negative desktop
   coordinates if practical. Card tracks capture bounds with consistent visual gap.
3. Alt-Tab away/back, minimize/restore: no floating unrelated HUD; fresh anchoring.
4. Real `-Jev`: Remote short message, question, two rapid Remote messages; confirm
   Pending → Ready on the exact IDs/bubbles. Then a Self message pushes them upward.
5. Push early targets above viewport, scroll newest targets below, then scroll back:
   retained rail entries show `屏外`, anchors disappear/restore only on exact IDs,
   stable numbers never collapse to 1, and no Jev replay. A→B→A clears old epochs.
6. Inspect redacted timing/result logs: no wrong association, no Self HUD, no replay,
   no capture contamination/focus theft. Record real latency and fallback counts.

No Phase 6 PASS/PR readiness until all required gates have real evidence.

## Sources / accepted limitations

Consulted 2026-09-24: [Microsoft display affinity](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowdisplayaffinity),
[extended styles](https://learn.microsoft.com/en-us/windows/win32/winmsg/extended-window-styles),
[SetWindowPos](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowpos).
TypeSafe skill and live index, Noul/Choice/Score, Confidence, State, API and parallel
questions docs were consulted for field meanings; Phase 5 API/criteria remain frozen.
See [Phase 5 sources](PHASE5_JEV.md#live-documentation-gate-2026-09-24).

Retain all accepted V0 limitations: best-effort historical ID reassociation (exact-ID
reattachment only, never different-ID guessing), strict clipped-width append false negatives, residual OCR semantic risk,
limited quote separation, omitted unverified quoted context, uncalibrated display policy,
probabilistic Jev rather than truth, small latency sample and bounded queue skips.
No replies, auto-send, dynamic questions, long-term profiles or interaction automation.
