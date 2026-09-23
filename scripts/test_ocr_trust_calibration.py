import unittest
from scripts.ocr_trust_calibration import evaluate_row, validate_review, cohorts, private_output
from pathlib import Path


class CalibrationTests(unittest.TestCase):
    def fixture(self, expected='不行'):
        return dict(Name='fixture', Expected=expected, Sha256='hash', CaptureDpi=None)

    def review(self, expected='不行'):
        return dict(fully_visible=True, region_separation_verified=True, sha256='hash',
                    expected_text=expected, completeness_review_source='human inspected full crop')

    def probes(self, text):
        a = dict(raw_text=text, detected_line_count=1, lines=[dict(raw_text=text, rec_score=.999)])
        return dict(production=a, repeat=a, detector_line_probe=dict(raw_text=text), resize_probe=a)

    def test_stable_wrong_output_cannot_be_hidden_by_expected_labels(self):
        probes = self.probes('行')
        bad = evaluate_row(self.fixture(), probes, self.review(), dict(side='Self'), 'crop.png')
        good = evaluate_row(self.fixture('行'), probes, self.review('行'), dict(side='Self'), 'crop.png')
        self.assertEqual(bad['decisions'], good['decisions'])
        self.assertFalse(bad['raw_exact'])
        self.assertIsNone(bad['is_semantically_dangerous_error'])
        self.assertTrue(good['raw_exact'])

    def test_latin_spacing_retains_raw_error_and_unknown_equivalence(self):
        expected = '123, I got home.'
        row = evaluate_row(self.fixture(expected), self.probes('123,I got home.'),
                           self.review(expected), dict(side='Self'), 'crop.png')
        self.assertFalse(row['raw_exact'])
        self.assertFalse(row['normalized_exact'])
        self.assertIsNone(row['is_semantically_equivalent'])
        self.assertTrue(row['punctuation_spacing_only_hint'])

    def test_incomplete_unreviewed_or_stale_crop_cannot_enter_corpus(self):
        for replacement in ({'fully_visible': False}, {'completeness_review_source': ''},
                            {'sha256': 'different'}, {'expected_text': 'different'}):
            review = dict(self.review(), **replacement)
            with self.assertRaises(ValueError):
                validate_review(self.fixture(), review)

    def test_stale_semantic_annotation_is_rejected(self):
        review = dict(self.review(), labelled_output='不行', dangerous_error=False)
        with self.assertRaisesRegex(ValueError, 'stale'):
            evaluate_row(self.fixture(), self.probes('行'), review, dict(side='Self'), 'crop.png')

    def test_aliases_not_independent_and_unknown_dpi_retained(self):
        row = evaluate_row(self.fixture(), self.probes('不行'), self.review(), dict(side='Self'), 'crop.png')
        groups = cohorts([row, dict(row, fixture='alias')])
        self.assertEqual(len(groups['overall_entries']), 2)
        self.assertEqual(len(groups['distinct_crops']), 1)
        self.assertEqual(len(groups['dpi_None']), 2)
        self.assertEqual(len(groups['dpi_144']), 0)

    def test_private_artifacts_cannot_write_outside_cache(self):
        with self.assertRaises(ValueError):
            private_output(Path('docs/trust-private'))

    def test_ai_review_suggestion_does_not_become_human_semantic_label(self):
        review = dict(self.review(), review_status='ai_visual_pending_human',
                      review_reason='Needs human decision',
                      proposed_semantic_review={'is_semantically_equivalent': True})
        row = evaluate_row(self.fixture(), self.probes('行'), review, dict(side='Self'), 'crop.png')
        self.assertIsNone(row['is_semantically_equivalent'])
        self.assertIsNone(row['is_semantically_dangerous_error'])
        self.assertIsNone(row['is_polarity_error'])
        self.assertEqual(row['review_reason'], 'Needs human decision')


if __name__ == '__main__':
    unittest.main()
