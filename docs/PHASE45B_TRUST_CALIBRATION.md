# Phase 4.5B — OCR trust calibration (experimental, not accepted)

## Scope and recommendation

Unified extraction is accepted separately. This experiment changes no production
OCR, worker, Observer, completeness, identity or append code. Do **not** promote the
tested same-model stability rules to production: they still trust substantive errors.
Keep production candidates untrusted until a separately reviewed policy passes.
Stability is useful negative evidence (reject disagreement), not sufficient positive
evidence of correct text. No Adaptive second opinion or score threshold was added.

The confirmed completeness blocker is deferred, not resolved: at 144 DPI the same
real message produced a 496×26 fragment 2px from the bottom, incorrectly Complete,
versus a full 496×111 crop 13px from the bottom. Its private pair is under
`.ocr-cache/completeness/{partial,full}-attempt-2.*`. Neither enters this corpus.
Even a good trust gate cannot compensate for an incorrect upstream visibility flag.

## Corpus and method

Reused the immutable 84 entries / 76 byte-distinct crops. Prior manual completeness
and main/quote inspection provenance is recorded per fixture, independently of the
known-buggy Observer classifier. Aliases share inference and are not independent
evidence. No new captures or synthetic OCR samples were added in this run.

The native Windows Python GPU runtime loaded small_det/small_rec once, using the
unchanged production `scripts.paddle_ocr_worker.extract` function:

- A: production Unified extraction from original pixels.
- B: identical-pixel repeat; compare final and per-line text and line count.
- C: single-line-only detector crop recognition (diagnostic, not output replacement).
- D: 1.5× Lanczos representation with Unified extraction, diagnostic only.

All A outputs match the accepted production benchmark (84/84 parity, 79/84 exact).
No Adaptive was invoked. These are direct production-function calls in one process,
not a new .NET IPC or Observer validation. Whole multiline crops are not compared to
recognition-only output because that path is known to truncate.

The policy function never receives expected labels, side, fixture ID or scores as
decision inputs. Hard rejects cover incomplete/partial/unknown visibility, empty text,
failed operation, runtime fallback, unverified region separation, unsupported Unicode,
and invalid line structure. Zero detected lines is extraction success but insufficient
structure evidence for these trust proposals. Paddle scores (per-line/min/mean/spread)
remain uncalibrated diagnostics and never become OcrConfidence.

Three exploratory rules, after hard gates:

1. repeat_only: A/B stability.
2. cross_representation: above plus A/C single-line agreement, or A/D multiline agreement.
3. strict_stability: above plus A/D stability for all crops.

Agreement is literal; punctuation disagreement is not silently normalized away.
These are **correlated same-model signals**, never independent-engine agreement.

## Observed results

| Policy | Trusted exact | Trusted non-exact | Coverage | False trust rate |
| --- | ---: | ---: | ---: | ---: |
| Repeat only | 79 | 5 | 84/84 (100%) | 5/84 (5.95%) |
| Cross representation | 75 | 3 | 78/84 (92.86%) | 3/78 (3.85%) |
| Strict stability | 72 | 3 | 75/84 (89.29%) | 3/75 (4.00%) |

Strict stability leaves 7 correct and 2 incorrect entries untrusted. On 76 distinct
crops it trusts 67 exact and 2 wrong (69/76 coverage; 2/69 false trust). Both wrong
crop outputs are stable across **every** probe, with production rec_score above 0.998.
They are substantive glyph substitution/deletion errors, not punctuation spacing.

Semantic-equivalence and dangerous/polarity labels for non-exact outputs are still
**unknown pending human review**. Thus trusted_semantically_equivalent=0 means none
approved yet, not that none could be equivalent. trusted_dangerous_wrong=0 is only
the confirmed-label count; all 3 trusted-wrong entries have danger unknown. This is
NOT evidence that the zero-dangerous-error acceptance goal has passed.

| Strict cohort | n | Trusted exact | Trusted wrong | Untrusted exact |
| --- | ---: | ---: | ---: | ---: |
| Single character | 9 | 9 | 0 | 0 |
| Short Chinese (≤6 characters) | 54 | 47 | 3 | 4 |
| Polarity vocabulary | 17 | 17 | 0 | 0 |
| English | 8 | 6 | 0 | 0 |
| Mixed | 9 | 7 | 0 | 2 |
| Multiline | 7 | 6 | 0 | 1 |
| Self (background-inferred) | 74 | 67 | 0 | 5 |
| Remote (background-inferred) | 10 | 5 | 3 | 2 |
| 96 DPI | 7 | 7 | 0 | 0 |
| 144 DPI | 7 | 7 | 0 | 0 |
| Unknown DPI | 70 | 58 | 3 | 7 |

The original 52-crop subset reaches 47/52 trusted-exact (90.38%), zero trusted wrong,
versus historical 6/52 coverage. The broader corpus disproves treating that subset
success as general safety. Do not add a Self-only rule or blacklist known failing
words to manufacture acceptance.

All represented special short cases pass the strict candidate: 好 4/4, 晚安 4/4,
不行 3/3, 不要 1/1, 可以 1/1, 不是 1/1, 嗯 1/1. 哦 and 收到 are absent. The original
12 polarity concepts are covered; 去/不去/能/不能 remain absent. Additional complete
real crops were requested, not yet collected. 76 distinct crops is below the desired
80–150 useful samples. Side labels are inferred from bubble background, not asserted
as new human annotations; older missing DPI metadata is not guessed. Dual-DPI
multiline and broader Remote/polarity coverage remain gaps. No held-out claims.

Startup 3323.9ms, warmup 579.8ms; all-probe per-distinct-crop p50/p95 58.1/111.2ms.
Timings exclude startup, PNG loading, report IO and IPC; not Observer latency.

## Reproduce and manually review

Native Windows, using the already installed runtime:

```powershell
& "$env:LOCALAPPDATA\WeChatJevHud\paddle-ocr\.venv\Scripts\python.exe" `
  -m scripts.ocr_trust_calibration `
  --review-labels .ocr-cache/phase4.5b-trust/review-labels.json
```

Private artifacts:

- `.ocr-cache/phase4.5b-trust/review.html`: native-pixel crops, expected/recognized,
  evidence/probes, decisions, grouped Trusted, Untrusted-but-correct, Wrong,
  Dangerous wrong, and Danger unknown.
- `results.json`: all raw/normalized exactness/CER, nullable semantic labels, scores,
  runtime metadata, timings and per-policy/cohort confusion counts.
- `report.md`: compact metrics. False trust excludes only explicitly approved semantic
  equivalence; raw false trust always retains every raw mismatch.
- `review-labels.json`: manual provenance and optional semantic labels. Label edits
  must match SHA, expected text and `labelled_output`; `--report-only` regenerates
  reports without rerunning inference. Do not label unreviewed ambiguity safe.

Manual next steps: inspect all five non-exact entries, decide semantic-equivalence,
danger and polarity labels separately, and review the complete crops/side labels.
Collect the six missing short/polarity messages as full real bubbles and extend
coverage without modifying the immutable benchmark. This experiment establishes a
failure of stability-only positive trust, not an accepted replacement policy.

Phase 4.5B and overall Phase 4.5 remain IN PROGRESS; no Jev/HUD.

Validation: native Windows Python `compileall` and all 42 Python tests passed,
including 11 new policy/evaluation tests. Tests cover hard gates overriding stability,
score independence, unsupported Unicode/structure, stale labels, private-output
constraints, raw Latin-spacing truth and alias/unknown-DPI accounting. The actual
GPU experiment ran on all 76 distinct crops. Production source/worker files are
unchanged; prior .NET evidence was not rerun or relabeled as a new integration run.

## Focused Remote polarity follow-up (diagnostic only)

The user rejected all three stability proposals for production. A focused audit uses
only the two distinct non-exact Remote crops (three aliased entries), one resident
`PP-OCRv6_small_rec`, no detection, and exactly these inputs:

- A: original RGB pixels.
- B: `255 - RGB`, converting the dark background/light text to light background/dark
  text without thresholding, clipping, padding or geometry change.
- C: standard luminance grayscale, converted to RGB without contrast stretching.

Native Windows GPU baseline assertion against recorded expected labels fails (0/2).
All six audit recognitions reproduce the same recorded non-exact outputs: inversion
corrects 0/2, grayscale corrects 0/2, neither introduces a different output. Scores
remain above 0.997 but have no trust role. No new generic stability probes or
production preprocessing changes. Private images/results:
`.ocr-cache/phase4.5b-trust/remote-polarity/{results.json,review.html}`.

This finds no evidence that **these two simple polarity treatments** repair the
recorded failures. It does not prove a model-intrinsic cause or rule out all rendering
effects. Ground truth must also be verified: AI visual review raised a transcription
question about one source glyph label. The immutable expected labels/raw exactness
have not been changed to agree with OCR.

All five non-exact entries have been visually inspected by the assistant. Explicit
semantic fields remain null pending actual human approval; separate AI suggestions,
`review_reason` and `review_status=ai_visual_pending_human` are recorded in the private
`semantic-review-pending.json` and review HTML. In particular, the two English alias
entries have a proposed spacing-only equivalence, while the three Chinese entries
require source-transcription and semantic review. Do not call AI inspection a completed
human semantic review or convert unknown dangerousness to false.

### Independent verifier recommendation (design only)

If human transcription confirms the substantive errors, evaluate a genuinely
independent verifier before automatic trust promotion. A second call/representation
of the same Paddle recognizer is not such a verifier. No verifier is implemented here.

- Unified Paddle remains the sole successful production text source. Adaptive stays
  runtime-failure-only and untrusted, never the normal extraction route.
- A verifier independently reads only the same complete semantic-region crop, without
  seeing Paddle's text first, to limit confirmation bias. Compare its evidence with
  the immutable Paddle candidate afterward; never replace or merge candidate text.
- The comparison returns Agreement / Disagreement / Inconclusive, provenance, aligned
  disagreements (especially negation/digits), timings and optional engine diagnostics.
  It does not manufacture OcrConfidence or equate agreement to correctness.
- Failure, missing evidence or disagreement cannot promote trust. Any future positive
  rule needs calibration on matched real Self/Remote, short/polarity, multiline and
  held-out examples; independent engines can also share errors (D-019).
- Visibility/complete-text/normal-completion/nonempty/no-fallback and region-separation
  gates still precede verification. The known completeness blocker remains deferred.

### Real expansion status

The current live frame was captured and inspected but still showed the prior test
page, not the six requested new concepts. No new crop is claimed as collected.
Private batch files prepare 24 Remote short texts and matched 24 Self where practical:
missing concepts, polarity pairs, common short phrases and the difficult glyph cases.
User preparation/confirmation is required; use existing collection tooling and inspect
every crop. Do not turn side imbalance into a side-based trust rule.

Follow-up validation: native Windows Python static compilation and all 46 Python
tests passed. The baseline `--assert-exact` intentionally failed against the recorded
labels (0/2); the six-run audit completed successfully but corrected neither crop.
No .NET/production files changed and .NET gates were not rerun. Private artifacts
remain gitignored. Human labels and new real captures are unfinished, not waived.
