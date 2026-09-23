"""Experimental code-owned trust proposals. Not imported by production OCR/Observer.

No expected labels, engine score thresholds, or Adaptive agreement enter decisions.
Same-model stability is correlated evidence, never independent-engine confidence.
"""
import unicodedata


def unsupported_unicode(text):
    return any(c == '\ufffd' or unicodedata.category(c) in {'Cc', 'Cf', 'Cs', 'Co', 'Cn'}
               or (unicodedata.category(c).startswith('L') and not any(
                   script in unicodedata.name(c, '') for script in ('LATIN', 'CJK')))
               for c in text if c not in '\n\r\t')


def decide(evidence, policy='strict_stability'):
    """Return a proposal, not an OcrTextStatus or calibrated probability."""
    for field in ('is_fully_visible', 'has_complete_text', 'extraction_completed',
                  'region_separation_verified'):
        if evidence.get(field) is not True:
            return {'trusted': False, 'reason': 'hard_reject:' + field}
    if evidence.get('runtime_fallback') is not False:
        return {'trusted': False, 'reason': 'hard_reject:runtime_fallback_or_unknown'}
    text = evidence.get('raw_text', '')
    if not text.strip():
        return {'trusted': False, 'reason': 'hard_reject:empty'}
    if unsupported_unicode(text):
        return {'trusted': False, 'reason': 'hard_reject:unsupported_unicode'}
    if evidence.get('invalid_line_structure'):
        return {'trusted': False, 'reason': 'hard_reject:invalid_line_structure'}
    if policy not in {'repeat_only', 'cross_representation', 'strict_stability'}:
        raise ValueError('Unknown policy')
    if not evidence.get('same_model_stability'):
        return {'trusted': False, 'reason': 'SameModelStability:failed_or_missing'}
    if policy != 'repeat_only' and not evidence.get('cross_representation_agreement'):
        return {'trusted': False, 'reason': 'CrossRepresentationAgreement:failed_or_missing'}
    if policy == 'strict_stability' and not evidence.get('resize_stability'):
        return {'trusted': False, 'reason': 'ResizeStability:failed_or_missing'}
    if evidence.get('detected_line_count', 0) == 0:
        return {'trusted': False, 'reason': 'no_line_structure_evidence'}
    return {'trusted': True, 'reason': policy + ':correlated_evidence_only'}


def summary(rows, policy='strict_stability'):
    result = dict(n=len(rows), trusted_correct=0, trusted_semantically_equivalent=0,
                  trusted_wrong=0, trusted_dangerous_wrong=0, trusted_danger_unknown=0,
                  untrusted_correct=0, untrusted_wrong=0)
    for row in rows:
        trusted = row['decisions'][policy]['trusted']
        if row['raw_exact']:
            result['trusted_correct' if trusted else 'untrusted_correct'] += 1
        elif trusted:
            if row.get('is_semantically_equivalent') is True:
                result['trusted_semantically_equivalent'] += 1
            else:
                result['trusted_wrong'] += 1
            if row.get('is_semantically_dangerous_error') is True:
                result['trusted_dangerous_wrong'] += 1
            elif row.get('is_semantically_dangerous_error') is None:
                result['trusted_danger_unknown'] += 1
        else:
            result['untrusted_wrong'] += 1
    trusted = sum(row['decisions'][policy]['trusted'] for row in rows)
    result['semantic_ready_coverage'] = trusted / len(rows) if rows else None
    result['false_trust_rate'] = result['trusted_wrong'] / trusted if trusted else None
    result['raw_false_trust_rate'] = sum(r['decisions'][policy]['trusted'] and not r['raw_exact']
                                       for r in rows) / trusted if trusted else None
    return result
