import unittest
from PIL import Image
from scripts.ocr_remote_polarity_audit import variants, outcome, select_failures


class RemotePolarityTests(unittest.TestCase):
    def test_three_variants_preserve_geometry_and_raw_pixels(self):
        image = Image.new('RGB', (11, 7), (40, 50, 60))
        image.putpixel((5, 3), (220, 210, 200))
        result = variants(image)
        self.assertEqual(list(result), ['A_raw', 'B_light_background', 'C_grayscale'])
        self.assertEqual(result['A_raw'].tobytes(), image.tobytes())
        for variant in result.values():
            self.assertEqual(variant.size, image.size)
        self.assertEqual(result['B_light_background'].getpixel((0, 0)), (215, 205, 195))
        self.assertEqual(result['B_light_background'].getpixel((5, 3)), (35, 45, 55))
        gray = result['C_grayscale']
        self.assertEqual(len(set(gray.getpixel((0, 0)))), 1)
        self.assertGreater(gray.getpixel((5, 3))[0], gray.getpixel((0, 0))[0])
        self.assertNotIn(gray.getpixel((0, 0))[0], (0, 255))

    def test_outcomes_do_not_normalize_or_relabel_ground_truth(self):
        self.assertEqual(outcome('不行', '行', '不行'), 'matches_recorded_expected')
        self.assertEqual(outcome('不行', '行', '行'), 'unchanged_nonexact')
        self.assertEqual(outcome('不行', '行', '不'), 'different_nonexact')

    def test_remote_aliases_run_once_and_stale_evidence_fails(self):
        fixtures = [dict(Name=n, Sha256='hash', Expected='不行') for n in ('a', 'alias')]
        rows = [dict(fixture=n, raw_exact=False, self_or_remote='Remote', crop_sha256='hash', expected_text='不行')
                for n in ('a', 'alias')]
        selected = select_failures({'rows': rows}, fixtures)
        self.assertEqual(len(selected), 1)
        self.assertEqual(selected[0]['aliases'], ['a', 'alias'])
        rows[0]['crop_sha256'] = 'stale'
        with self.assertRaises(ValueError):
            select_failures({'rows': rows}, fixtures)


if __name__ == '__main__':
    unittest.main()
