"""Private Phase 4.5B experiments. Production worker/extraction and trust are unchanged.

Uses the production extract function, with both existing models resident once. A/B
repeat original pixels; C is a single-line detector crop diagnostic only; D is a
1.5x Lanczos diagnostic. D never replaces production pixels or output.
"""
import argparse
import hashlib
import html
import json
import os
from pathlib import Path
import re
import statistics
import sys
import time

from scripts.ocr_trust_policy import decide, summary, unsupported_unicode
from scripts.paddle_ocr_benchmark import normalize_text, character_error_rate, percentile
from scripts.compare_unified_bubble import language

POLICIES = ('repeat_only', 'cross_representation', 'strict_stability')
POLARITY = set('好 不好 行 不行 可以 不可以 要 不要 是 不是 有 没有 去 不去 能 不能'.split())
SHORT_REVIEW = '好 晚安 不行 不要 可以 不是 嗯 哦 收到'.split()


def private_output(path):
    path = path.resolve()
    if not path.is_relative_to(Path('.ocr-cache').resolve()):
        raise ValueError('Private calibration output must remain under .ocr-cache')
    return path


def load_corpus(root):
    fixtures = json.loads((root / 'manifest.json').read_text(encoding='utf-8'))['Fixtures']
    if len({f['Name'] for f in fixtures}) != len(fixtures):
        raise ValueError('Duplicate fixture IDs')
    labels_by_hash = {}
    for fixture in fixtures:
        crop = root / fixture['Crop']
        if hashlib.sha256(crop.read_bytes()).hexdigest() != fixture['Sha256']:
            raise ValueError('Immutable corpus crop changed: ' + fixture['Name'])
        if fixture['Sha256'] in labels_by_hash and labels_by_hash[fixture['Sha256']] != fixture['Expected']:
            raise ValueError('Conflicting labels for identical crop bytes')
        labels_by_hash[fixture['Sha256']] = fixture['Expected']
    return fixtures


def validate_review(fixture, review):
    if review.get('sha256') != fixture['Sha256'] or review.get('expected_text') != fixture['Expected']:
        raise ValueError('Review labels do not match crop/expected label: ' + fixture['Name'])
    if review.get('fully_visible') is not True or not review.get('completeness_review_source'):
        raise ValueError('Missing complete-crop manual review: ' + fixture['Name'])


def same_result(a, b):
    return (a['raw_text'] == b['raw_text'] and
            a['detected_line_count'] == b['detected_line_count'] and
            [x['raw_text'] for x in a['lines']] == [x['raw_text'] for x in b['lines']])


def collect_probes(detector, recognizer, image, device):
    import numpy as np
    from PIL import Image
    from scripts.paddle_ocr_worker import extract, recognize

    started = time.perf_counter()
    raw = np.asarray(image)
    a = extract(detector, recognizer, raw, device)
    b = extract(detector, recognizer, raw, device)
    c = None
    if a['detected_line_count'] == 1:
        x1, y1, x2, y2 = a['line_boxes'][0]
        text, score, elapsed = recognize(recognizer, raw[y1:y2, x1:x2], device)
        c = dict(raw_text=text, rec_score=score, recognition_ms=elapsed)
    resized = image.resize((round(image.width * 1.5), round(image.height * 1.5)), Image.Resampling.LANCZOS)
    d = extract(detector, recognizer, np.asarray(resized), device)
    return dict(production=a, repeat=b, detector_line_probe=c, resize_probe=d,
                all_probes_ms=(time.perf_counter() - started) * 1000)


def side_evidence(image):
    # Descriptive label only, never trust evidence. Not a human annotation.
    import numpy as np
    rgb = np.asarray(image).astype('int16')
    green = ((rgb[:, :, 1] > rgb[:, :, 0] + 25) & (rgb[:, :, 1] > rgb[:, :, 2] + 15)).mean()
    gray = ((rgb.max(axis=2) - rgb.min(axis=2) < 12) & (rgb.mean(axis=2) > 30)
            & (rgb.mean(axis=2) < 90)).mean()
    return dict(side='Self' if green > .3 else 'Remote' if gray > .3 else 'Unknown',
                source='bubble_background_inference_not_manual_side_label',
                green_fraction=float(green), gray_fraction=float(gray))


def evidence_for(probes, review):
    a, b, c, d = (probes[k] for k in ('production', 'repeat', 'detector_line_probe', 'resize_probe'))
    scores = [line['rec_score'] for line in a['lines']]
    resized_agrees = same_result(a, d)
    return dict(is_fully_visible=review.get('fully_visible'),
                has_complete_text=review.get('fully_visible'),
                region_separation_verified=review.get('region_separation_verified'),
                extraction_completed=True, runtime_fallback=False,
                raw_text=a['raw_text'], detected_line_count=a['detected_line_count'],
                extraction_path='whole_bubble' if a['detected_line_count'] <= 1 else 'detected_multiline',
                same_model_stability=same_result(a, b),
                cross_representation_agreement=(c['raw_text'] == a['raw_text']) if c is not None else resized_agrees,
                resize_stability=resized_agrees,
                cross_representation_kind='detector_line_crop' if c is not None else 'resized_multiline',
                rec_scores=scores, min_rec_score=min(scores) if scores else None,
                mean_rec_score=statistics.mean(scores) if scores else None,
                rec_score_spread=max(scores)-min(scores) if scores else None,
                empty_recognized_line_count=sum(not line['raw_text'].strip() for line in a['lines']),
                invalid_line_structure=(len(a['lines']) != max(1, a['detected_line_count'])
                                        or any(not line['raw_text'].strip() for line in a['lines'])),
                zero_detection=a['detected_line_count'] == 0,
                unsupported_unicode=unsupported_unicode(a['raw_text']))


def evaluate_row(fixture, probes, review, side, image_path):
    validate_review(fixture, review)
    expected, text = fixture['Expected'], probes['production']['raw_text']
    if any(k in review for k in ('semantically_equivalent', 'dangerous_error', 'polarity_error')):
        if review.get('labelled_output') != text:
            raise ValueError('Semantic annotation is stale for changed output')
        if review.get('semantically_equivalent') is True and (
                review.get('dangerous_error') is True or review.get('polarity_error') is True):
            raise ValueError('Contradictory semantic annotation')
    exact = expected == text
    normalized = normalize_text(expected) == normalize_text(text)
    evidence = evidence_for(probes, review)
    # Only exact equality establishes automatic equivalence. All non-exact semantic
    # labels require explicit human review; punctuation similarity is merely a hint.
    return dict(fixture=fixture['Name'], crop_sha256=fixture['Sha256'], image=image_path,
                expected_text=expected, paddle_output=text, raw_exact=exact,
                normalized_exact=normalized, raw_CER=character_error_rate(expected, text),
                normalized_CER=character_error_rate(normalize_text(expected), normalize_text(text)),
                is_semantically_equivalent=True if exact else review.get('semantically_equivalent'),
                is_semantically_dangerous_error=False if exact else review.get('dangerous_error'),
                is_polarity_error=False if exact else review.get('polarity_error'),
                review_reason=review.get('review_reason'),
                review_status=review.get('review_status', 'unreviewed_semantics'),
                punctuation_spacing_only_hint=(not exact and re.sub(r'(?<=[,.;:!?]) +', '', expected) == re.sub(r'(?<=[,.;:!?]) +', '', text)),
                line_count=evidence['detected_line_count'], dpi=fixture.get('CaptureDpi'),
                self_or_remote=review.get('side', side['side']), side_evidence=side,
                review=review, evidence=evidence, probes=probes,
                decisions={policy: decide(evidence, policy) for policy in POLICIES})


def cohorts(rows):
    groups = {'overall_entries': rows}
    seen = set()
    distinct = []
    for row in rows:
        if row['crop_sha256'] not in seen:
            distinct.append(row)
            seen.add(row['crop_sha256'])
    groups['distinct_crops'] = distinct
    groups['single_character'] = [r for r in rows if len(r['expected_text']) == 1]
    groups['short_Chinese'] = [r for r in rows if language(r['expected_text']) == 'Chinese' and len(r['expected_text']) <= 6]
    groups['polarity'] = [r for r in rows if r['expected_text'] in POLARITY]
    for label in ('English', 'mixed'):
        groups[label] = [r for r in rows if language(r['expected_text']) == label]
    groups['multiline'] = [r for r in rows if r['line_count'] >= 2]
    for side in ('Self', 'Remote', 'Unknown'):
        groups[side] = [r for r in rows if r['self_or_remote'] == side]
    for dpi in (96, 144, None):
        groups[f'dpi_{dpi}'] = [r for r in rows if r['dpi'] == dpi]
    for text in SHORT_REVIEW:
        groups[f'short:{text}'] = [r for r in rows if r['expected_text'] == text]
    return groups


def report(output, rows, metadata):
    groups = cohorts(rows)
    summaries = {policy: {label: summary(group, policy) for label, group in groups.items()} for policy in POLICIES}
    payload = dict(metadata=metadata, summaries=summaries, rows=rows)
    (output / 'results.json').write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding='utf-8')
    sections = {
        'Trusted (PROPOSED, not accepted)': [r for r in rows if r['decisions']['strict_stability']['trusted']],
        'Untrusted-but-correct': [r for r in rows if not r['decisions']['strict_stability']['trusted'] and r['raw_exact']],
        'Wrong (raw non-exact; semantic review separate)': [r for r in rows if not r['raw_exact']],
        'Dangerous wrong': [r for r in rows if r['is_semantically_dangerous_error'] is True],
        'Danger unknown — review required': [r for r in rows if r['is_semantically_dangerous_error'] is None],
    }
    esc = lambda value: html.escape(str(value))
    page = ['<!doctype html><meta charset="utf-8"><title>Private OCR trust review</title>',
            '<style>body{font:15px sans-serif;background:#eee}article{background:white;margin:16px 0;padding:12px}pre{white-space:pre-wrap} .crop{overflow-x:auto}img{max-width:none}table{border-collapse:collapse}td,th{border:1px solid #aaa;padding:5px}</style>',
            '<h1>Phase 4.5B — candidate trust only</h1>',
            '<p>Production extraction/trust unchanged. Scores are uncalibrated; all stability probes use the SAME model. No independent-engine agreement. Partial fixture excluded. Crops display at native CSS pixels with horizontal scrolling.</p>',
            '<p id="scale"></p><script>document.getElementById("scale").textContent="devicePixelRatio="+devicePixelRatio+"; viewportScale="+(visualViewport?.scale??1);</script>',
            '<p>Non-exact semantic labels are unknown until manually reviewed; an unknown dangerous outcome is NOT evidence of zero dangerous errors. This is in-sample exploration, not held-out calibration.</p>',
            '<pre>' + esc(json.dumps(summaries, ensure_ascii=False, indent=2)) + '</pre>']
    for title, group in sections.items():
        page.append('<h2>' + esc(title) + f' ({len(group)})</h2>')
        for row in group:
            page.append('<article><h3>' + esc(row['fixture']) + '</h3><div class="crop"><img src="' + esc(row['image']) + '"></div>')
            fields = {k: row[k] for k in ('expected_text', 'paddle_output', 'raw_exact', 'normalized_exact', 'raw_CER',
                      'is_semantically_equivalent', 'is_semantically_dangerous_error', 'is_polarity_error',
                      'review_reason', 'review_status', 'line_count', 'dpi', 'self_or_remote', 'review', 'evidence', 'decisions', 'probes')}
            page.append('<pre>' + esc(json.dumps(fields, ensure_ascii=False, indent=2)) + '</pre></article>')
    (output / 'review.html').write_text('\n'.join(page), encoding='utf-8')
    md = ['# Private Phase 4.5B trust calibration', '',
          'Experimental proposals only; production remains untrusted. Same-model stability is NOT independent-engine agreement.',
          'Unknown dangerous outcomes require review; zero confirmed dangerous labels is not proof of zero dangerous errors.',
          'False trust rate = trusted non-exact/non-approved-equivalent / all trusted. Raw false trust also counts equivalent spacing errors.',
          'All non-exact semantic labels default to unknown; no punctuation normalization relabels raw exactness.',
          'Side labels use background inference unless manually overridden. Missing DPI stays unknown.', '',
          '| policy / cohort | n | trusted correct | trusted equivalent | trusted wrong | dangerous wrong | danger unknown | untrusted correct | untrusted wrong | coverage | false trust |',
          '| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |']
    for policy, values in summaries.items():
        for label, s in values.items():
            fields = [f'{policy} / {label}', *[s[k] for k in ('n', 'trusted_correct', 'trusted_semantically_equivalent',
                      'trusted_wrong', 'trusted_dangerous_wrong', 'trusted_danger_unknown', 'untrusted_correct',
                      'untrusted_wrong', 'semantic_ready_coverage', 'false_trust_rate')]]
            md.append('| ' + ' | '.join(str(x) for x in fields) + ' |')
    md += ['', 'Full per-sample text, raw/normalized CER, semantic labels, all probe scores/timings and decisions: results.json and review.html.',
           'Strict stability is rejected as an automatic trust gate when stable substantive errors remain. Do not blacklist observed failing words or treat Self-only performance as a general guarantee.']
    (output / 'report.md').write_text('\n'.join(md), encoding='utf-8')
    print(json.dumps({p: summaries[p]['distinct_crops'] for p in POLICIES}))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--corpus', type=Path, default=Path('.ocr-cache/phase4.5-paddle-bubble'))
    parser.add_argument('--review-labels', type=Path, required=True,
                        help='Private human-completeness provenance keyed by fixture ID')
    parser.add_argument('--output', type=Path, default=Path('.ocr-cache/phase4.5b-trust'))
    parser.add_argument('--device', default='gpu:0')
    parser.add_argument('--report-only', action='store_true')
    args = parser.parse_args()
    output = private_output(args.output)
    output.mkdir(parents=True, exist_ok=True)
    fixtures = load_corpus(args.corpus)
    labels = json.loads(args.review_labels.read_text(encoding='utf-8'))
    if args.report_only:
        previous = json.loads((output / 'results.json').read_text(encoding='utf-8'))
        by_name = {f['Name']: f for f in fixtures}
        if len(previous['rows']) != len(fixtures) or {r['fixture'] for r in previous['rows']} != set(by_name):
            raise ValueError('Cached experiment corpus changed')
        for row in previous['rows']:
            fixture = by_name[row['fixture']]
            if row['crop_sha256'] != fixture['Sha256'] or row['expected_text'] != fixture['Expected']:
                raise ValueError('Cached experiment crop/label changed')
        rows = [evaluate_row(by_name[r['fixture']], r['probes'], labels[r['fixture']], r['side_evidence'], r['image'])
                for r in previous['rows']]
        report(output, rows, previous['metadata'])
        return
    # Fail closed BEFORE loading models: no known partial or unreviewed crop runs.
    for fixture in fixtures:
        validate_review(fixture, labels.get(fixture['Name'], {}))
    os.environ.setdefault('PADDLE_PDX_MODEL_SOURCE', 'BOS')
    os.environ.setdefault('PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK', 'True')
    from PIL import Image
    from scripts.paddle_ocr_worker import load_runtime
    detector, recognizer, ready = load_runtime('PP-OCRv6_small_rec', args.device, 1)
    rows, cached = [], {}
    for fixture in fixtures:
        path = args.corpus / fixture['Crop']
        with Image.open(path) as source:
            image = source.convert('RGB')
        sha = fixture['Sha256']
        if sha not in cached:
            cached[sha] = collect_probes(detector, recognizer, image, args.device)
        probes = cached[sha]
        row = evaluate_row(fixture, probes, labels[fixture['Name']], side_evidence(image),
                           os.path.relpath(path.resolve(), output).replace('\\', '/'))
        rows.append(row)
        (output / 'progress.json').write_text(json.dumps(rows, ensure_ascii=False, indent=2), encoding='utf-8')
        print(f'{len(rows)}/{len(fixtures)}', file=sys.stderr, flush=True)
    metadata = dict(ready=ready, production_function='scripts.paddle_ocr_worker.extract (unchanged)',
                    diagnostic_resize='1.5x Lanczos; never production input',
                    sample_count=len(rows), distinct_crops=len(cached), model_instances={'det': 1, 'rec': 1},
                    timing='same-process synchronized inference; not IPC/observer latency; aliases share probes',
                    probe_p50_ms=percentile([p['all_probes_ms'] for p in cached.values()], .5),
                    probe_p95_ms=percentile([p['all_probes_ms'] for p in cached.values()], .95),
                    production_trust_changed=False, held_out=False)
    report(output, rows, metadata)


if __name__ == '__main__':
    main()
