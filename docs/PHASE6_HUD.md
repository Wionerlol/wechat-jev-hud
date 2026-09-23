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
`WpfOverlayPresenter`. One transparent host HWND/Canvas contains up to three newest
cards (configurable `OverlayLayoutOptions`). No per-message windows. A small controller
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

Cards are 210 DIP wide with 11 DIP padding, 9 DIP rounded corners, subtle border,
dark near-opaque background, 12–13 DIP text, no animations/shadows. The preferred
gap is 10 DIP to the Remote bubble's right. Try right, then left; small vertical
shifts in 8 DIP steps up to 96 DIP avoid cards and all visible message rectangles.
Everything must fit the usable chat ROI. Unsafe/impossible placements hide. Large
multiline bubbles in a narrow window may leave no safe placement; hiding is expected.

## Fixed display policy

No thresholds, prompt changes or calibration from appearance. Four rows always:

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

Each healthy stable observation updates anchors. A single missing changed observation
retains the card as a grace; two consecutive changed observations retire it. Unchanged
frames do not advance that counter. If the first missing view stays static, its next
unchanged observation hides the grace anchor (without advancing retirement), avoiding
an indefinitely floating ghost while waiting for another changed observation.
A retired key cannot be recreated on history
rediscovery. This intentionally does not treat best-effort old historical association
as a guarantee. No new Jev calls are made by scroll or layout.

Foreground loss, minimize, unavailable capture, pending identity and layout transitions
hide without counting disappearance. Header/chat pixel changes provisionally hide
before waiting for Observer/OCR to resolve identity. Confirmed epoch change clears
all old cards immediately on receipt. Current untrusted/partial semantic evidence
hides rows without modifying Observer text/trust. Late results lacking a current
visible active key cannot render. No persisted cards or semantic profile.

## Recursive capture protection

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
HUD remains hidden and those frames never enter perception. Restart explicitly to
retry a failed audit. The normal diagnostics command remains available without HUD.
Render-HWND, monitor and DPI transitions require a fresh probe. The brief colored patch is diagnostic,
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

1. Demo/audit with real foreground WeChat at 150%: confirm card style, unobstructed
   message/composer, typing focus and click-through. Audit must report positive control
   and RenderExcluded=true; inspect desktop result separately.
2. Move/resize narrow/wide, move to 100% display, back to 150%, negative desktop
   coordinates if practical. Card tracks capture bounds with consistent visual gap.
3. Alt-Tab away/back, minimize/restore: no floating unrelated HUD; fresh anchoring.
4. Real `-Jev`: Remote short message, question, two rapid Remote messages; confirm
   Pending → Ready on the exact IDs/bubbles. Then a Self message pushes them upward.
5. Slight scroll follows; scroll fully out for two changed observations retires;
   scroll back does not resurrect. A→B→A hides pending and clears confirmed epochs.
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

Retain all accepted V0 limitations: best-effort historical ID reassociation (no HUD
resurrection), strict clipped-width append false negatives, residual OCR semantic risk,
limited quote separation, omitted unverified quoted context, uncalibrated display policy,
probabilistic Jev rather than truth, small latency sample and bounded queue skips.
No replies, auto-send, dynamic questions, long-term profiles or interaction automation.
