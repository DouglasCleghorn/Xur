#!/usr/bin/env python3
"""Known synthetic geometry proof for the exact native AprilTag helper; no hardware."""
import argparse
import json
import math
from pathlib import Path
import subprocess


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--helper', type=Path, default=Path('/opt/xur/estimate-marker-pose'))
    parser.add_argument('--evidence', type=Path)
    options = parser.parse_args()
    passed, cases = [], []
    def check(value, description):
        if not value:
            raise AssertionError(description)
        passed.append(description)
    def corners(edge):
        s = edge / 2
        return [(-s, s, 0), (s, s, 0), (s, -s, 0), (-s, -s, 0)]
    def transform(rotation, translation, point):
        return [sum(rotation[row * 3 + column] * point[column] for column in range(3)) + translation[row] for row in range(3)]
    def project(point):
        return [800 * point[0] / point[2] + 320, 810 * point[1] / point[2] + 240]
    def solve(edge, pixels):
        arguments = [str(options.helper), str(edge), '800', '810', '320', '240'] + [format(v, '.17g') for p in pixels for v in p]
        result = subprocess.run(arguments, check=True, capture_output=True, text=True, timeout=5)
        return json.loads(result.stdout)['poses']
    for label, angle, translation in [('oblique', 40, [0.03, -0.01, 0.35]), ('weak-perspective', 5, [0.0, 0.0, 2.0])]:
        radians = math.radians(angle)
        rotation = [math.cos(radians), 0, math.sin(radians), 0, 1, 0, -math.sin(radians), 0, math.cos(radians)]
        edge = .05
        pixels = [project(transform(rotation, translation, p)) for p in corners(edge)]
        solutions = solve(edge, pixels)
        evaluated = []
        for pose in solutions:
            points = [transform(pose['rotation'], pose['translation'], p) for p in corners(edge)]
            check(all(p[2] > 0 for p in points), label + ': candidate remains in front of the camera')
            check(all(math.isfinite(v) for v in pose['rotation'] + pose['translation']), label + ': candidate contains only finite values')
            reprojection = [project(p) for p in points]
            rms = math.sqrt(sum(sum((a - b) ** 2 for a, b in zip(predicted, observed)) for predicted, observed in zip(reprojection, pixels)) / 4)
            evaluated.append(dict(pose, reprojectionRmsPixels=rms))
        evaluated.sort(key=lambda value: value['reprojectionRmsPixels'])
        check(evaluated and evaluated[0]['reprojectionRmsPixels'] < .001, label + ': native estimation matches known synthetic image corners')
        check(max(abs(a - b) for a, b in zip(evaluated[0]['translation'], translation)) < .00005,
              label + ': known metric translation recovered within 0.05 mm')
        if label == 'weak-perspective':
            check(len(evaluated) == 2 and evaluated[1]['reprojectionRmsPixels'] - evaluated[0]['reprojectionRmsPixels'] < .5,
                  'Weak perspective exposes both plausible planar candidates for conservative ambiguity handling')
        else:
            check(len(evaluated) < 2 or evaluated[1]['reprojectionRmsPixels'] - evaluated[0]['reprojectionRmsPixels'] > .5,
                  'Oblique near-field fixture separates the alternate planar fit in raw pixel error')
        cases.append(dict(name=label, suppliedEdgeMeters=edge, trueTranslationMeters=translation, observedPixels=pixels, candidates=evaluated))
    for value in ['nan', 'inf', '-1', '0']:
        arguments = [str(options.helper), '.05', value, '810', '320', '240'] + [str(v) for p in pixels for v in p]
        result = subprocess.run(arguments, capture_output=True, text=True, timeout=5)
        check(result.returncode != 0, 'Native helper rejects invalid focal input: ' + value)
    receipt = dict(suite='NativeAprilTagMetrology', scope='Synthetic known geometry only; no real camera calibration or motor commands', passed=passed, cases=cases)
    if options.evidence:
        options.evidence.parent.mkdir(parents=True, exist_ok=True)
        options.evidence.write_text(json.dumps(receipt, indent=2) + '\n')
    print(json.dumps(receipt))


if __name__ == '__main__':
    main()
