#!/usr/bin/env python3
"""No-hardware, no-weights counterexamples for saved-image registration."""
import importlib.util
import json
import os
from pathlib import Path
import sys
import subprocess
import tempfile
import unittest

import cv2
import numpy as np

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = Path(os.environ.get('XUR_IMAGE_MOTION_SCRIPT', str(ROOT / 'tools/Xur.Robotics/image_motion.py')))
spec = importlib.util.spec_from_file_location('xur_image_motion', SCRIPT)
motion = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = motion
spec.loader.exec_module(motion)
cv2.setNumThreads(1)


def scene():
    rng = np.random.default_rng(12)
    image = cv2.GaussianBlur(rng.integers(30, 220, (480, 640), np.uint8), (3, 3), 0)
    for i in range(85):
        x, y = int(rng.integers(10, 620)), int(rng.integers(10, 460))
        cv2.circle(image, (x, y), int(rng.integers(4, 12)), int(rng.integers(35, 220)), 2)
        cv2.putText(image, str(i), (x, y), cv2.FONT_HERSHEY_SIMPLEX, .35, int(rng.integers(35, 220)), 1)
    return image


def translate(image, dx, dy):
    return cv2.warpAffine(image, np.float32([[1, 0, dx], [0, 1, dy]]), (image.shape[1], image.shape[0]),
                          borderMode=cv2.BORDER_REFLECT)


class ImageMotionTests(unittest.TestCase):
    def test_broad_translation_direction_and_original_scale(self):
        image = scene()
        result = motion.compare_images(image, translate(image, 14, -9), motion.Parameters(max_dimension=320))
        self.assertEqual(result['classification'], 'global-motion-candidate')
        self.assertAlmostEqual(result['phaseCorrelation']['dxPixels'], 14, delta=1)
        self.assertAlmostEqual(result['phaseCorrelation']['dyPixels'], -9, delta=1)
        self.assertGreater(result['orbHomography']['inliers'], 25)
        self.assertGreater(result['orbHomography']['coverageBefore']['hullFraction'], .25)
        self.assertFalse(result['cameraMotorIdentified'])
        self.assertFalse(result['motorCommandsIssued'])
        self.assertFalse(result['thresholdsValidatedForInstallation'])

    def test_affine_exposure_alone_is_not_global_motion(self):
        image = scene()
        exposed = np.clip(image.astype(float) * 1.12 + 10, 0, 255).astype(np.uint8)
        result = motion.compare_images(image, exposed)
        self.assertEqual(result['classification'], 'static-or-jitter')
        self.assertAlmostEqual(result['photometricResidual']['gain'], 1.12, delta=.03)
        self.assertAlmostEqual(result['photometricResidual']['bias'], 10, delta=2)

    def test_subpixel_jitter_never_identifies_global_motion(self):
        image = scene()
        result = motion.compare_images(image, translate(image, .6, .3))
        self.assertEqual(result['classification'], 'static-or-jitter')

    def test_uniform_and_gradient_images_remain_ambiguous(self):
        for image in [np.full((480, 640), 100, np.uint8), np.tile(np.linspace(50, 200, 640).astype(np.uint8), (480, 1))]:
            result = motion.compare_images(image, translate(image, 16, 5))
            self.assertEqual(result['classification'], 'ambiguous')
            self.assertFalse(result['orbHomography'].get('broadSpatialSupport', False))

    def test_features_confined_to_one_object_cannot_establish_global_motion(self):
        image = np.full((480, 640), 120, np.uint8)
        image[160:280, 250:370] = scene()[:120, :120]
        result = motion.compare_images(image, translate(image, 15, 0))
        self.assertEqual(result['classification'], 'ambiguous')
        self.assertFalse(result['orbHomography'].get('broadSpatialSupport', False))

    def test_local_patch_motion_preserves_static_background_model(self):
        background = scene()
        patch = np.random.default_rng(42).integers(30, 220, (125, 125), np.uint8)
        before, after = background.copy(), background.copy()
        before[145:270, 255:380] = patch
        after[170:295, 290:415] = patch
        result = motion.compare_images(before, after)
        self.assertEqual(result['classification'], 'local-motion-candidate')
        self.assertLess(result['orbHomography']['projectedGridMotion']['medianMagnitudePixels'], 1.5)
        self.assertGreater(result['photometricResidual']['changedFraction'], .025)

    def test_reversible_translation_requires_return_control(self):
        image = scene()
        displaced = translate(image, 16, -8)
        result = motion.analyze_sequence(image, displaced, image)
        self.assertEqual(result['conclusion'], 'reversible-global-image-motion-candidate')
        self.assertTrue(result['repeatControl']['reversibleGlobalCandidate'])
        self.assertFalse(result['cameraMotorIdentified'])
        drift = motion.analyze_sequence(image, displaced, translate(image, 7, 0))
        self.assertEqual(drift['conclusion'], 'ambiguous-repeat-control')

    def test_static_sequence_and_distributed_noise_are_not_local_motion(self):
        image = scene()
        result = motion.analyze_sequence(image, image, image)
        self.assertEqual(result['conclusion'], 'stable-through-sequence')
        noisy = np.clip(image.astype(float) + np.random.default_rng(88).normal(0, 15, image.shape), 0, 255).astype(np.uint8)
        result = motion.compare_images(image, noisy)
        self.assertNotIn(result['classification'], ('global-motion-candidate', 'local-motion-candidate'))

    def test_scene_wide_motion_is_explicit_camera_cause_counterexample(self):
        # A stationary camera observing a moving screen can generate these same
        # pixels. The positive registration must never turn into motor identity.
        image = scene()
        result = motion.analyze_sequence(image, translate(image, 12, 4), image)
        self.assertEqual(result['conclusion'], 'reversible-global-image-motion-candidate')
        self.assertFalse(result['cameraMotorIdentified'])
        self.assertIn('moving scene', result['beforeToDisplaced']['limitations'][0])

    def test_unrelated_scenes_and_strong_clipping_remain_ambiguous(self):
        image = scene()
        unrelated = np.random.default_rng(99).integers(20, 230, image.shape, np.uint8)
        self.assertEqual(motion.compare_images(image, unrelated)['classification'], 'ambiguous')
        clipped = np.where(image > 120, 255, 0).astype(np.uint8)
        self.assertEqual(motion.compare_images(image, clipped)['classification'], 'ambiguous')

    def test_opposing_large_regions_do_not_claim_one_global_transform(self):
        image = scene()
        left, right = translate(image, -14, 0), translate(image, 14, 0)
        displaced = np.concatenate((left[:, :320], right[:, 320:]), axis=1)
        self.assertEqual(motion.compare_images(image, displaced)['classification'], 'ambiguous')

    def test_invalid_dimensions_and_nonimage_inputs_fail(self):
        with self.assertRaises(ValueError):
            motion.compare_images(np.zeros((32, 32), np.uint8), np.zeros((64, 32), np.uint8))
        with self.assertRaises(ValueError):
            motion.compare_images(np.zeros((32, 32), float), np.zeros((32, 32), float))
        with self.assertRaises(ValueError):
            motion.load_image(ROOT / 'LICENSE')
        with self.assertRaises(ValueError):
            motion.load_image(ROOT / '.build/no-such-file.jpg')

    def test_pole_reflection_and_collapsed_transforms_fail(self):
        for h in [np.diag([-1., 1., 1.]), np.diag([.1, .1, 1.]), np.array([[1.,0,0],[0,1.,0],[.01,0,-2.]])]:
            with self.assertRaises(ValueError):
                motion._projected_motion(h, 640, 480, 1., 1.)

    def test_cli_preserves_inputs_and_records_duplicate_content(self):
        evidence = ROOT / '.build/evidence/image-motion'
        evidence.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=evidence) as folder:
            before, moved, after, output = (Path(folder) / name for name in ('before.png', 'moved.png', 'after.png', 'result.json'))
            image = scene()
            cv2.imwrite(str(before), image)
            cv2.imwrite(str(moved), translate(image, 14, -5))
            after.write_bytes(before.read_bytes())
            args = [sys.executable, str(SCRIPT), '--before', str(before), '--displaced', str(moved), '--after', str(after)]
            run = subprocess.run(args + ['--output', str(output)], capture_output=True, text=True, timeout=15)
            self.assertEqual(run.returncode, 0, run.stderr)
            data = json.loads(output.read_text())
            self.assertEqual(data['duplicateInputContent'], [['before', 'after']])
            self.assertFalse(data['temporalBaselineEstablished'])
            original = before.read_bytes()
            rejected = subprocess.run(args + ['--output', str(before)], capture_output=True, text=True, timeout=15)
            self.assertEqual(rejected.returncode, 2)
            self.assertEqual(original, before.read_bytes())


if __name__ == '__main__':
    suite = unittest.defaultTestLoader.loadTestsFromTestCase(ImageMotionTests)
    result = unittest.TextTestRunner(verbosity=2).run(suite)
    evidence = ROOT / '.build/evidence/image-motion/tests.json'
    evidence.parent.mkdir(parents=True, exist_ok=True)
    evidence.write_text(json.dumps(dict(suite='ClassicalImageMotion', tests=result.testsRun,
        failures=len(result.failures), errors=len(result.errors), opencv=cv2.__version__, numpy=np.__version__), indent=2) + '\n')
    raise SystemExit(0 if result.wasSuccessful() else 1)
