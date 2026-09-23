# Phase 5 — TypeSafe / Jev integration

Status: **Phase 5 PASS — 2026-09-24**, automated Windows and real synthetic Chinese API smoke.
Phase 4.5 is frozen. No perception algorithm, OCR/trust gate, epoch or history policy
changes are part of Phase 5. No HUD, display thresholds, generated replies or interaction.

## Live documentation gate (2026-09-24)

Read the installed `.agents/skills/typesafe-ai/SKILL.md`, then the live official pages:

- [Index](https://docs.typesafe.ai/llms.txt)
- [System One](https://docs.typesafe.ai/concepts/system-one.md)
- [Building guide](https://docs.typesafe.ai/concepts/how-to-build-with-system-one.md)
- [State](https://docs.typesafe.ai/concepts/state.md)
- [Primitives / batching](https://docs.typesafe.ai/primitives.md)
- [Noul](https://docs.typesafe.ai/primitives/noul.md)
- [Choice](https://docs.typesafe.ai/primitives/choice.md)
- [Score](https://docs.typesafe.ai/primitives/score.md)
- [Confidence](https://docs.typesafe.ai/confidence.md)
- [HTTP API](https://docs.typesafe.ai/api.md)
- [SDK index](https://docs.typesafe.ai/sdk.md) and [authentication setup](https://docs.typesafe.ai/sdk/python.md)
- [Models / limits / language support](https://docs.typesafe.ai/models.md)
- [Fan-out pattern](https://docs.typesafe.ai/patterns/fan-out.md)
- [Parallel-questions cookbook](https://docs.typesafe.ai/cookbooks/parallel_questions.md)

The web reader could read the index but failed on some Markdown pages. Direct HTTPS
retrieval of the official Markdown pages succeeded; this is live access, not recalled
API knowledge. The current SDK index lists Python and JS/TS, not .NET. Use HttpClient.

## Contract and types

`POST https://api.typesafe.ai/v1/systemone`, `Authorization: Bearer <key>`, JSON body:
`state`, `model: "jev-latest"`, `questions` map. Responses contain `model`, `answers`
under the same question IDs and `usage` (`input_tokens`, `output_tokens` where present).
The model alias currently resolves to `jev-1.13.0`; retain the actual response model.

Noul returns `noul`, the yes probability, with no separate confidence. Choice returns
`choice`, option `probabilities`, `confidence`. Score returns probability-weighted
`score`, level `probabilities`, `legend`, `confidence`. Internal immutable records
separate these concepts; Choice/Score confidence is named `DistributionConfidence`.
Do not convert any of them to OCR confidence or a correctness guarantee.

Validate all eight answer IDs/types, bounded finite numeric values, option/level sets,
legends, distribution sums and score/choice consistency. Reject missing, duplicated,
malformed or oversized responses; never manufacture default judgments. Small numeric
rounding tolerances validate serialized distributions, not product display thresholds.

## Static questions: jev-v0.1

Five Noul questions: `expects_response`, `references_prior_context`,
`contains_direct_request`, `expresses_disagreement_or_correction`,
`contains_time_or_plan_commitment`.

One Choice: `speech_act`, with `question`, `request`, `answer`, `acknowledgement`,
`clarification`, `complaint_or_concern`, `planning`, `joke_or_banter`, `information`,
`other`. Criteria distinguish requested actions from information questions, answers
from acknowledgements, clarification from ordinary questions, and plans from facts.

Two Score questions: `urgency` and `emotional_intensity`, each four explicit levels
0–3. Timing pressure must be expressed, not inferred from affect. Intensity measures
textual expression only, never personality, diagnosis or hidden emotional state.
All complete instructions/criteria live in `JevQuestionSet`; IDs alone are not prompts.
All eight independent questions share one state in one HTTP request.

## Data flow and privacy

After `ObserveAsync` completes, the diagnostics composition root passes `observer.State`
and `result.NewMessages` to the semantic coordinator. Observer has no TypeSafe reference.
Only same-current-epoch, trusted Remote LiveNew targets are eligible. No calls for
Self, History, Bootstrap, uncertain/incomplete/edge/region/fallback targets.

`JevContextBuilder` uses chronological timeline order, not timestamps or coordinates.
It finds the target and considers only prior same-epoch trusted messages, including
Self as context. Later siblings from the same observation cannot enter earlier input.
Default: last 8 trusted prior messages, 2,000 Unicode scalar text characters total.
Configurable ceilings: 25 prior / 4,000 text scalars, plus 16,000 UTF-8 bytes for the
serialized state. These deliberately conservative byte/text budgets are not a token
counter; docs currently allow 32k state+longest-question and 64k total input tokens.
Drop oldest whole messages, never split a Unicode scalar or truncate the target's
meaning. An oversized/missing target is explicitly skipped. Excluding untrusted or
budget-omitted context sets `context_has_gaps`; uncertain text never enters the state.

State contains `current_message` and chronological `recent_messages` (`side`, `text`,
`quoted_text: null`), `locale: "zh-CN"`, `context_has_gaps`. The frozen Observer has no
independent quote-trust provenance, so optional `QuotedText` is omitted rather than
borrowing main-text trust. This is a limitation, not automatic quote separation.
No HWND, coordinates, pixels, scores, IDs, keys or logs are sent as semantic state.

SHA-256 fingerprints the exact deterministic serialized state. Dedupe keys include
epoch, logical ID, question-set version and fingerprint. Memory only; no raw payload
logging or persistent semantic profile. A 2,048-key cap per epoch fails closed rather
than evicting old successful keys and permitting replays; a new epoch resets it.

## Scheduling and failures

One background consumer, a bounded 8-item Channel, non-waiting TryWrite on perception.
Queue-full and dedupe-cap skips are explicit. Failed/skipped keys are not automatically
resubmitted on another frame. Up to 32 immutable results retained; overflow drops the
oldest result with a counter. The tagged result stream is drained in memory, not parsed
from console output. Future consumers must honor epoch tags.

Epoch changes cancel old work, discard queued stale jobs and clear prior results.
Even a client ignoring cancellation cannot publish a late old-epoch result. Drain and
epoch update use the same short lock; no network request or external callback holds it.

Missing process `TYPESAFE_API_KEY` → NotConfigured with zero network calls. 401/403 →
Unauthorized, 429 → RateLimited, 5xx/529/network error → ServiceUnavailable, invalid
typed data → MalformedResponse, 15-second deadline → Timeout. Cancellation is separate.
No automatic retries for V0: although the docs recommend backoff when retrying 429/529,
this observer prefers one unavailable analysis to resubmission storms. No error bodies,
exception strings or authorization values enter diagnostics. HTTP redirects disabled.

Metrics include context_build_ms, queue_wait_ms, typesafe_roundtrip_ms,
result_mapping_ms, total_semantic_ms, request/response bytes, token usage, state character
count, question count and success/failure/duplicate/stale/backpressure counters.

## Commands and real acceptance

PowerShell, in the repository. Set a process-only key securely, without pasting it in
chat or a committed file (environment variables are local secrets, not encryption):

```powershell
$typeSafeSecret = Read-Host 'TypeSafe API key' -AsSecureString
$env:TYPESAFE_API_KEY = [System.Net.NetworkCredential]::new('', $typeSafeSecret).Password
.\scripts\jev-smoke.ps1
```

The smoke sends ONLY six code-owned non-sensitive Chinese examples, not WeChat data.
One batch of eight questions per example. Exit 0 requires all six successful typed
responses; missing configuration exits 2, not a passing real smoke. No probabilities
are hardcoded as expected. Inspect answer/question/request/correction/plan/banter
behavior qualitatively and record actual timings before PASS.

To explicitly enable live observer semantic uploads after smoke acceptance:

```powershell
.\scripts\observe.ps1 -Jev
```

Without `-Jev`, no semantic upload occurs. `-DebugText` is not needed for Jev outputs.
Default Jev diagnostics contain IDs, counts, timings and typed judgments, not chat text.
Stop the process before removing the process key: `Remove-Item Env:TYPESAFE_API_KEY`.

## Known limitations

- The six synthetic examples below verify the real API, not the entire real-WeChat
  perception-to-network flow. No private chat was uploaded for this acceptance.
- Official docs warn non-English/CJK accuracy is lower than English; six synthetic
  cases cannot establish general correctness or probability calibration.
- Aliases may change the answering model. No threshold/display policy is calibrated.
- Prior OCR and best-effort historical identity limitations remain exactly as accepted.
- Only trusted prior messages still present in the bounded Observer snapshot are available.
- Explicit quotes remain omitted pending an independently trusted quote contract.
- Queue/dedupe/result caps can skip analysis; perception remains independent.

## Final validation evidence (2026-09-24)

- Windows format, full Windows build (zero warnings/errors), 292 .NET tests passed:
  40 TypeSafe, 176 Observer, 63 OCR, 6 Vision, 5 Windows, 2 Capture; no failures/skips.
  Python/model suites were not rerun because no Python/OCR implementation changed.
- Fake transport/coordinator tests cover eligibility, same-ID dedupe, equal-text
  distinct IDs, prior-only siblings, budgets/Unicode/gaps, backpressure, late and queued
  old-epoch work, missing config, timeout, cancellation, HTTP errors, malformed typed
  results, secret/error-text redaction. These demonstrate failures/staleness without
  deliberately causing a real service outage or transmitting private chat.
- The diagnostic smoke initially exited 2 with six NotConfigured results, requests=0.
  After the user configured their Windows User environment, a fresh child explicitly
  imported the key in memory; the key was never printed, placed on the command line,
  written to a report, or committed.
- Real smoke exited 0: 6 requests, each 8 questions, 48 answers, all Success,
  model `jev-1.13.0`. Zero retries, failures, queue drops or fabricated judgments.
  Private numeric log: `.ocr-cache/phase5-real-smoke.log`.

The following is observed output, not hardcoded expected probabilities. Noul columns
are probability of yes; Score columns are weighted positions on the 0–3 levels.

| Synthetic current message (prior when relevant) | Response | Prior context | Direct request | Correction | Plan | Speech act | Urgency | Intensity | HTTP ms |
| --- | ---: | ---: | ---: | ---: | ---: | --- | ---: | ---: | ---: |
| 到了 (你到了吗) | .45 | .66 | .02 | .02 | .59 | answer | .02 | .00 | 1420.95 |
| 你到家了吗？ | .96 | .11 | .92 | .02 | .10 | question | .21 | .01 | 385.47 |
| 帮我把文件发一下 | .94 | .66 | .77 | .02 | .10 | request | .20 | .01 | 409.15 |
| 不是，我说的是周六 (我们周五见) | .84 | .73 | .29 | .97 | .95 | clarification | .03 | .53 | 500.99 |
| 今晚八点见 | .80 | .25 | .39 | .01 | .97 | planning | .63 | .00 | 409.18 |
| 哈哈哈哈哈 | .52 | .15 | .02 | .01 | .02 | joke_or_banter | .00 | 2.33 | 477.92 |

Qualitative review: all six primary functions are sensible; explicit correction and
planning receive high corresponding Nouls. Laughter has higher expressed intensity
but zero urgency, preserving the distinction. Nuance remains: `到了` plan probability
.59 and laughter response probability .52 are uncertain/model-dependent, not facts;
an information question has high direct-request probability because the definition
includes asking the user to answer. No threshold or prompt tuning followed this run.
Speech-act selected probabilities are 1.0 except clarification .97; returned confidence
is separately retained (planning confidence .99 despite rounded probability 1.0).
Intensity for laughter has confidence .33, spread .11/.44/.45 across levels 1/2/3.

| Stage, milliseconds | Median (6 requests) | Max / nearest-rank p95 (n=6) |
| --- | ---: | ---: |
| context build | .031 | 35.290 |
| queue wait | 2044.776 | 3160.946 |
| HTTP roundtrip | 443.549 | 1420.947 |
| result mapping | .298 | 21.313 |
| total semantic, including queue | 2500.669 | 3639.310 |

All six inputs were enqueued as a burst with one consumer. The first call includes
connection/JIT startup; the five subsequent HTTP calls range 385.47–500.99 ms, median
409.18 ms. Their tiny sample is not a long-run latency SLA. Queue delay is not server
inference time; perception never waits for this burst to drain. No premature concurrency
optimization is included. Interactive latency acceptability remains a real-use judgment.

Actual wire payloads: 5,233–5,304 request bytes (31,528 total), 1,314–1,328 response
bytes (7,915 total); 134–187 state Unicode scalars including field syntax. Reported
usage: 8,704 input tokens, 1,430 output tokens. At the consulted model page's $0.042/M
input rate and free output, this is approximately $0.000366, not an invoice/account
billing verification. The price may change; recorded token counts are the evidence.
