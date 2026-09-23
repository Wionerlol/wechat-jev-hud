import unittest
from scripts.ocr_trust_policy import decide, summary


def complete(**overrides):
    return dict(dict(is_fully_visible=True, has_complete_text=True,
                     extraction_completed=True, region_separation_verified=True,
                     runtime_fallback=False, raw_text='不行', detected_line_count=1,
                     same_model_stability=True, cross_representation_agreement=True,
                     resize_stability=True), **overrides)


class TrustPolicyTests(unittest.TestCase):
    def test_safety_preconditions_override_all_stability_and_scores(self):
        for field in ('is_fully_visible', 'has_complete_text', 'extraction_completed',
                      'region_separation_verified'):
            for value in (False, None):
                with self.subTest(field=field, value=value):
                    self.assertFalse(decide(complete(**{field: value}, rec_score=1))['trusted'])
        self.assertFalse(decide(complete(runtime_fallback=True))['trusted'])
        self.assertFalse(decide(complete(raw_text=' '))['trusted'])

    def test_scores_are_diagnostic_only(self):
        for score in (0, .5, .99999, 1, None):
            self.assertTrue(decide(complete(rec_score=score))['trusted'])
            self.assertFalse(decide(complete(rec_score=score, same_model_stability=False))['trusted'])

    def test_missing_probes_or_disagreement_do_not_trust(self):
        for field in ('same_model_stability', 'cross_representation_agreement', 'resize_stability'):
            self.assertFalse(decide(complete(**{field: False}))['trusted'])
        self.assertFalse(decide(complete(detected_line_count=0))['trusted'])

    def test_unicode_and_quote_ambiguity_rejected(self):
        for text in ('好\ufffd', '好\u202e', '好\x00', '\ud800', 'Hеllo'):
            self.assertFalse(decide(complete(raw_text=text))['trusted'])
        self.assertFalse(decide(complete(region_separation_verified=False))['trusted'])
        self.assertFalse(decide(complete(invalid_line_structure=True))['trusted'])

    def test_equivalence_does_not_change_raw_exact_or_hide_unknown_danger(self):
        rows = [dict(raw_exact=False, is_semantically_equivalent=True,
                     is_semantically_dangerous_error=False,
                     decisions={'strict_stability': {'trusted': True}}),
                dict(raw_exact=False, is_semantically_equivalent=None,
                     is_semantically_dangerous_error=None,
                     decisions={'strict_stability': {'trusted': True}})]
        report = summary(rows)
        self.assertEqual(report['trusted_semantically_equivalent'], 1)
        self.assertEqual(report['trusted_wrong'], 1)
        self.assertEqual(report['trusted_danger_unknown'], 1)
        self.assertEqual(report['raw_false_trust_rate'], 1)
        self.assertFalse(rows[0]['raw_exact'])


if __name__ == '__main__':
    unittest.main()
