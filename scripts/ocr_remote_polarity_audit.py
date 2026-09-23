"""Diagnostic-only Remote crop polarity experiment; never imported by production.

Exactly three representations, unchanged geometry: raw RGB, 255-RGB inversion,
and standard luminance grayscale. No resize, thresholding or detector-box recrop.
"""
import argparse
import contextlib
import hashlib
import html
import json
import os
from pathlib import Path
import sys
import time

from PIL import Image, ImageOps
from scripts.ocr_trust_calibration import load_corpus, private_output
from scripts.paddle_ocr_worker import recognize
from scripts.paddle_ocr_benchmark import character_error_rate, normalize_text


def variants(image):
    rgb = image.convert('RGB')
    return {'A_raw': rgb,
            'B_light_background': ImageOps.invert(rgb),
            'C_grayscale': ImageOps.grayscale(rgb).convert('RGB')}


def outcome(expected, baseline, recognized):
    if recognized == expected:
        return 'matches_recorded_expected'
    if recognized == baseline:
        return 'unchanged_nonexact'
    return 'different_nonexact'


def select_failures(results, fixtures):
    by_name = {f['Name']: f for f in fixtures}
    selected = {}
    for row in results['rows']:
        if row['raw_exact'] or row['self_or_remote'] != 'Remote':
            continue
        fixture = by_name[row['fixture']]
        if fixture['Sha256'] != row['crop_sha256'] or fixture['Expected'] != row['expected_text']:
            raise ValueError('Stale Remote audit fixture')
        group = selected.setdefault(fixture['Sha256'], dict(fixture=fixture, aliases=[]))
        group['aliases'].append(row['fixture'])
    if not selected:
        raise ValueError('No failing Remote crops selected')
    return list(selected.values())


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--corpus', type=Path, default=Path('.ocr-cache/phase4.5-paddle-bubble'))
    parser.add_argument('--results', type=Path, default=Path('.ocr-cache/phase4.5b-trust/results.json'))
    parser.add_argument('--output', type=Path, default=Path('.ocr-cache/phase4.5b-trust/remote-polarity'))
    parser.add_argument('--device', default='gpu:0')
    parser.add_argument('--baseline-only', action='store_true')
    parser.add_argument('--assert-exact', action='store_true', help='Red-capable diagnostic gate; expected labels may still need human correction')
    args = parser.parse_args()
    output = private_output(args.output)
    output.mkdir(parents=True, exist_ok=True)
    selected = select_failures(json.loads(args.results.read_text(encoding='utf-8')), load_corpus(args.corpus))
    os.environ.setdefault('PADDLE_PDX_MODEL_SOURCE', 'BOS')
    os.environ.setdefault('PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK', 'True')
    import numpy as np
    with contextlib.redirect_stdout(sys.stderr):
        import paddle
        import paddleocr
        from paddleocr import TextRecognition
        started = time.perf_counter()
        model = TextRecognition(model_name='PP-OCRv6_small_rec', device=args.device)
        startup_ms = (time.perf_counter() - started) * 1000
    _, _, warmup_ms = recognize(model, np.full((48, 256, 3), 255, dtype=np.uint8), args.device)
    rows = []
    for item in selected:
        fixture = item['fixture']
        with Image.open(args.corpus / fixture['Crop']) as source:
            representations = variants(source)
        baseline = None
        for name, image in representations.items():
            if args.baseline_only and name != 'A_raw':
                continue
            text, score, elapsed = recognize(model, np.asarray(image), args.device)
            if name == 'A_raw':
                baseline = text
            filename = fixture['Sha256'][:16] + '-' + name + '.png'
            image.save(output / filename)
            expected = fixture['Expected']
            rows.append(dict(fixture=fixture['Name'], aliases=item['aliases'], sha256=fixture['Sha256'],
                             variant=name, expected_text=expected, recognized=text,
                             rec_score=score, raw_exact=text == expected,
                             normalized_exact=normalize_text(text) == normalize_text(expected),
                             raw_CER=character_error_rate(expected, text), inference_ms=elapsed,
                             outcome=outcome(expected, baseline, text), image=filename,
                             width=image.width, height=image.height,
                             raw_pixel_sha256=hashlib.sha256(image.tobytes()).hexdigest()))
    metadata = dict(model='PP-OCRv6_small_rec', model_instances=1, detector_used=False,
                    device=paddle.device.get_device(), paddleocr=paddleocr.__version__,
                    paddlepaddle=paddle.__version__, startup_ms=startup_ms, warmup_ms=warmup_ms,
                    diagnostic_only=True, source_entries=sum(len(i['aliases']) for i in selected),
                    distinct_crops=len(selected),
                    transforms={'A_raw': 'RGB unchanged', 'B_light_background': '255 - RGB (no clipping/threshold)',
                                'C_grayscale': 'Pillow luminance L -> RGB (no contrast stretch)'},
                    ground_truth_warning='Recorded labels retained; image/semantic human review required, especially glyph labels.')
    (output / 'results.json').write_text(json.dumps(dict(metadata=metadata, rows=rows), ensure_ascii=False, indent=2), encoding='utf-8')
    esc = lambda x: html.escape(str(x))
    page = ['<!doctype html><meta charset="utf-8"><title>Private Remote polarity audit</title>',
            '<style>body{font:16px sans-serif;background:#eee}article{margin:15px;padding:10px;background:white}.image{overflow:auto}img{max-width:none}pre{white-space:pre-wrap}</style>',
            '<h1>Remote polarity diagnostic — not production preprocessing</h1>',
            '<p>Recorded expected labels are not revised by OCR. Human verification is required.</p>',
            '<label>Inspection zoom (display only): <select onchange="document.querySelectorAll(\'img\').forEach(i=>i.style.width=(i.naturalWidth*this.value)+\'px\')"><option>1</option><option>2</option><option>4</option></select></label>',
            '<pre>' + esc(json.dumps(metadata, ensure_ascii=False, indent=2)) + '</pre>']
    for row in rows:
        page += ['<article><h2>' + esc(row['fixture'] + ' / ' + row['variant']) + '</h2>',
                 '<div class="image"><img src="' + esc(row['image']) + '"></div>',
                 '<pre>' + esc(json.dumps(row, ensure_ascii=False, indent=2)) + '</pre></article>']
    (output / 'review.html').write_text('\n'.join(page), encoding='utf-8')
    print(json.dumps(dict(distinct_crops=len(selected), recognition_runs=len(rows),
                         exact_against_recorded_labels=sum(r['raw_exact'] for r in rows))))
    return 1 if args.assert_exact and any(not row['raw_exact'] for row in rows) else 0


if __name__ == '__main__':
    raise SystemExit(main())
