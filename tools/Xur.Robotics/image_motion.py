#!/usr/bin/env python3
"""Classical saved-image motion evidence. Original Xur code: MIT (root LICENSE).

No model weights, network, hardware access, robot IDs, or motor commands. Image
motion alone cannot distinguish camera motion from a moving scene filling the
view. All classifications are candidates, not camera/motor identification.
"""
from __future__ import annotations

import argparse
from dataclasses import asdict, dataclass
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import sys

import cv2
import numpy as np


@dataclass(frozen=True)
class Parameters:
    max_dimension: int = 1280
    max_features: int = 1800
    ratio_test: float = 0.75
    ransac_pixels_at_analysis_scale: float = 3.0
    minimum_inliers: int = 25
    minimum_inlier_ratio: float = 0.55
    minimum_hull_fraction: float = 0.25
    minimum_grid_cells: int = 6
    grid_columns: int = 4
    grid_rows: int = 3
    minimum_texture_std: float = 8.0
    minimum_gradient_mean: float = 3.0
    minimum_phase_response: float = 0.12
    minimum_local_changed_fraction: float = 0.025
    maximum_local_changed_fraction: float = 0.35
    minimum_local_top_three_tile_fraction: float = 0.65
    residual_change_threshold: float = 20.0
    maximum_saturated_fraction: float = 0.35
    # These screening defaults are unverified on any robot/camera installation.
    jitter_pixels: float = 1.5


def _gray(image: np.ndarray) -> np.ndarray:
    if image.dtype != np.uint8 or image.ndim not in (2, 3):
        raise ValueError("Images must be decoded uint8 grayscale or color arrays")
    if image.ndim == 3:
        if image.shape[2] not in (3, 4):
            raise ValueError("Unsupported color channel count")
        return cv2.cvtColor(image, cv2.COLOR_BGRA2GRAY if image.shape[2] == 4 else cv2.COLOR_BGR2GRAY)
    return image


def _texture(gray: np.ndarray) -> dict:
    gradient = cv2.magnitude(cv2.Sobel(gray, cv2.CV_32F, 1, 0), cv2.Sobel(gray, cv2.CV_32F, 0, 1))
    return dict(mean=float(gray.mean()), std=float(gray.std()), gradientMean=float(gradient.mean()),
                saturatedFraction=float(np.mean((gray <= 2) | (gray >= 253))))


def _coverage(points: np.ndarray, width: int, height: int, p: Parameters) -> dict:
    if len(points) < 3:
        return dict(hullFraction=0.0, occupiedCells=0, totalCells=p.grid_columns * p.grid_rows)
    hull = float(cv2.contourArea(cv2.convexHull(points.astype(np.float32)))) / (width * height)
    cells = {(min(p.grid_columns - 1, max(0, int(x * p.grid_columns / width))),
              min(p.grid_rows - 1, max(0, int(y * p.grid_rows / height)))) for x, y in points}
    return dict(hullFraction=hull, occupiedCells=len(cells), totalCells=p.grid_columns * p.grid_rows)


def _projected_motion(homography: np.ndarray, width: int, height: int, sx: float, sy: float) -> dict:
    points = np.array([(x * width, y * height) for y in np.linspace(.1, .9, 5)
                       for x in np.linspace(.1, .9, 5)], np.float32)
    corners = np.array([[0, 0], [width, 0], [width, height], [0, height]], np.float32)
    homogeneous = np.c_[np.vstack([points, corners]), np.ones(len(points) + 4)] @ homography.T
    denominators = homogeneous[:, 2]
    if not np.isfinite(homogeneous).all() or np.min(np.abs(denominators)) < 1e-8 or not (
            np.all(denominators > 0) or np.all(denominators < 0)):
        raise ValueError("Homography has a pole or invalid projection within the image")
    projected = homogeneous[:, :2] / denominators[:, None]
    transformed_corners = projected[-4:].astype(np.float32)
    original_orientation = cv2.contourArea(corners, oriented=True)
    new_orientation = cv2.contourArea(transformed_corners, oriented=True)
    area_ratio = abs(new_orientation) / (width * height)
    if not cv2.isContourConvex(transformed_corners) or original_orientation * new_orientation <= 0 or not .25 <= area_ratio <= 4:
        raise ValueError("Homography folds, reflects or gives an implausible image area ratio")
    delta = (projected[:len(points)] - points) / np.array([sx, sy])
    magnitude = np.linalg.norm(delta, axis=1)
    return dict(medianDxPixels=float(np.median(delta[:, 0])), medianDyPixels=float(np.median(delta[:, 1])),
                medianMagnitudePixels=float(np.median(magnitude)), minimumMagnitudePixels=float(magnitude.min()),
                maximumMagnitudePixels=float(magnitude.max()), areaRatio=area_ratio,
                grid=[dict(x=float(point[0] / sx), y=float(point[1] / sy), dx=float(d[0]), dy=float(d[1]))
                      for point, d in zip(points, delta)])


def _photometric_residual(before: np.ndarray, after: np.ndarray, homography: np.ndarray, p: Parameters) -> dict:
    height, width = before.shape
    moved = cv2.warpPerspective(before, homography, (width, height))
    valid = cv2.warpPerspective(np.ones(before.shape, np.uint8), homography, (width, height),
                                flags=cv2.INTER_NEAREST).astype(bool)
    valid[:5] = valid[-5:] = False
    valid[:, :5] = valid[:, -5:] = False
    # Deterministic subsampling and trimmed affine illumination fit allow a
    # simple gain/offset change without treating brightness alone as motion.
    mask = valid & (moved > 5) & (moved < 250) & (after > 5) & (after < 250)
    x = moved[mask].astype(np.float64)[::8]
    y = after[mask].astype(np.float64)[::8]
    if len(x) < 100 or x.std() < 3:
        return dict(available=False, problem="Insufficient unsaturated intensity variation")
    keep = np.ones(len(x), bool)
    gain, bias = 1.0, 0.0
    for _ in range(3):
        gain, bias = np.linalg.lstsq(np.c_[x[keep], np.ones(keep.sum())], y[keep], rcond=None)[0]
        error = np.abs(y - (gain * x + bias))
        keep = error <= np.percentile(error, 75)
        if keep.sum() < 100:
            return dict(available=False, problem="Illumination fit lacks enough retained pixels")
    residual = np.abs(after.astype(np.float32) - (moved.astype(np.float32) * gain + bias))
    changed = (residual > p.residual_change_threshold) & valid
    values = residual[valid]
    tiles = []
    for row in range(p.grid_rows):
        for col in range(p.grid_columns):
            ys = slice(row * height // p.grid_rows, (row + 1) * height // p.grid_rows)
            xs = slice(col * width // p.grid_columns, (col + 1) * width // p.grid_columns)
            tile_mask = valid[ys, xs]
            tiles.append(dict(column=col, row=row, changedPixels=int(changed[ys, xs].sum()),
                              changedFraction=float(changed[ys, xs][tile_mask].mean()) if tile_mask.any() else None))
    total_changed = int(changed.sum())
    concentration = sum(sorted((tile['changedPixels'] for tile in tiles), reverse=True)[:3]) / total_changed if total_changed else 0.0
    return dict(available=True, gain=float(gain), bias=float(bias), validFraction=float(valid.mean()),
                medianAbsoluteResidual=float(np.median(values)), percentile90AbsoluteResidual=float(np.percentile(values, 90)),
                changedFraction=float(changed[valid].mean()), topThreeTilesChangedFraction=concentration, tiles=tiles)


def compare_images(before_image: np.ndarray, after_image: np.ndarray, parameters: Parameters | None = None) -> dict:
    p = parameters or Parameters()
    if not 160 <= p.max_dimension <= 2560:
        raise ValueError("Analysis max dimension must be between 160 and 2560")
    before, after = _gray(before_image), _gray(after_image)
    if before.shape != after.shape or min(before.shape) < 32:
        raise ValueError("Images must have identical decoded dimensions of at least 32 pixels")
    original_height, original_width = before.shape
    factor = min(1.0, p.max_dimension / max(before.shape))
    if factor < 1:
        size = (max(32, round(original_width * factor)), max(32, round(original_height * factor)))
        before, after = (cv2.resize(image, size, interpolation=cv2.INTER_AREA) for image in (before, after))
    height, width = before.shape
    sx, sy = width / original_width, height / original_height
    qualities = [_texture(image) for image in (before, after)]
    issues = []
    low_texture = any(q['std'] < p.minimum_texture_std or q['gradientMean'] < p.minimum_gradient_mean for q in qualities)
    if low_texture:
        issues.append("Low texture: a shift estimate alone is insufficient")
    saturation = any(q['saturatedFraction'] > p.maximum_saturated_fraction for q in qualities)
    if saturation:
        issues.append("Large dark/clipped area reduces usable image information")
    floats = [(image.astype(np.float32) - q['mean']) / max(q['std'], 1e-6) for image, q in zip((before, after), qualities)]
    shift, response = cv2.phaseCorrelate(*floats, cv2.createHanningWindow((width, height), cv2.CV_32F))
    phase = dict(dxPixels=float(shift[0] / sx), dyPixels=float(shift[1] / sy), response=float(response),
                 finite=bool(np.isfinite([*shift, response]).all()))
    # Mutual ratio matches prevent a repeated scene feature from being counted
    # many times. ORB/RANSAC use images only, without a pretrained detector.
    orb = cv2.ORB_create(nfeatures=p.max_features, fastThreshold=12)
    points1, desc1 = orb.detectAndCompute(before, None)
    points2, desc2 = orb.detectAndCompute(after, None)
    matches = []
    if desc1 is not None and desc2 is not None:
        matcher = cv2.BFMatcher(cv2.NORM_HAMMING)
        forward = [pair[0] for pair in matcher.knnMatch(desc1, desc2, k=2) if len(pair) == 2 and pair[0].distance < p.ratio_test * pair[1].distance]
        reverse = {(pair[0].trainIdx, pair[0].queryIdx) for pair in matcher.knnMatch(desc2, desc1, k=2)
                   if len(pair) == 2 and pair[0].distance < p.ratio_test * pair[1].distance}
        matches = [match for match in forward if (match.queryIdx, match.trainIdx) in reverse]
    homography = None
    geometric = dict(available=False, keypointsBefore=len(points1), keypointsAfter=len(points2), mutualRatioMatches=len(matches))
    residual = dict(available=False, problem="No valid spatial homography")
    if len(matches) >= 8:
        source = np.array([points1[m.queryIdx].pt for m in matches], np.float32)
        target = np.array([points2[m.trainIdx].pt for m in matches], np.float32)
        cv2.setRNGSeed(0)
        h, inliers = cv2.findHomography(source, target, cv2.RANSAC, p.ransac_pixels_at_analysis_scale,
                                       maxIters=4000, confidence=.995)
        if h is not None and inliers is not None:
            accepted = inliers.ravel().astype(bool)
            try:
                motion = _projected_motion(h, width, height, sx, sy)
                projected = cv2.perspectiveTransform(source.reshape(-1, 1, 2), h).reshape(-1, 2)
                errors = np.linalg.norm((projected - target) / np.array([sx, sy]), axis=1)
                before_coverage = _coverage(source[accepted], width, height, p)
                after_coverage = _coverage(target[accepted], width, height, p)
                count = int(accepted.sum())
                ratio = float(accepted.mean())
                broad = count >= p.minimum_inliers and ratio >= p.minimum_inlier_ratio and all(
                    c['hullFraction'] >= p.minimum_hull_fraction and c['occupiedCells'] >= p.minimum_grid_cells
                    for c in (before_coverage, after_coverage))
                scale = np.diag([sx, sy, 1.0])
                original_h = np.linalg.inv(scale) @ h @ scale
                original_h /= original_h[2, 2]
                geometric.update(available=True, broadSpatialSupport=broad, inliers=count, inlierRatio=ratio,
                                 homographyBeforeToAfter=original_h.tolist(), coverageBefore=before_coverage,
                                 coverageAfter=after_coverage, inlierRmsPixels=float(np.sqrt(np.mean(errors[accepted] ** 2))),
                                 inlierMedianPixels=float(np.median(errors[accepted])), inlierMaximumPixels=float(errors[accepted].max()),
                                 projectedGridMotion=motion,
                                 matches=[dict(beforeX=float(a[0] / sx), beforeY=float(a[1] / sy),
                                               afterX=float(b[0] / sx), afterY=float(b[1] / sy), hammingDistance=float(m.distance),
                                               inlier=bool(ok), residualPixels=float(error))
                                          for a, b, m, ok, error in zip(source, target, matches, accepted, errors)])
                homography = h
                residual = _photometric_residual(before, after, h, p)
            except ValueError as error:
                geometric['problem'] = str(error)
    classification = 'ambiguous'
    reason = 'Insufficient reliable, spatially distributed geometric support'
    jitter = max(p.jitter_pixels, np.hypot(original_width, original_height) * .00075)
    if geometric.get('broadSpatialSupport') and not low_texture and not saturation:
        motion = geometric['projectedGridMotion']
        median_motion = motion['medianMagnitudePixels']
        vector = np.array([motion['medianDxPixels'], motion['medianDyPixels']])
        phase_error = float(np.linalg.norm(vector - np.array([phase['dxPixels'], phase['dyPixels']]))) if phase['finite'] else None
        phase['homographyMedianVectorDifferencePixels'] = phase_error
        phase_agrees = phase_error is not None and response >= p.minimum_phase_response and phase_error <= max(3.0, median_motion * .35)
        changed = residual.get('changedFraction')
        exposure_large = residual.get('available') and (not .6 <= residual['gain'] <= 1.6 or abs(residual['bias']) > 40)
        if exposure_large:
            issues.append("Large fitted illumination change; repeat under settled exposure")
        if not residual.get('available') or exposure_large:
            reason = 'Photometric support is unavailable or large exposure change is unresolved'
        elif median_motion <= jitter:
            if p.minimum_local_changed_fraction <= changed <= p.maximum_local_changed_fraction and residual['topThreeTilesChangedFraction'] >= p.minimum_local_top_three_tile_fraction:
                classification, reason = 'local-motion-candidate', 'Background homography is within jitter, with localized residual image changes'
            elif changed < p.minimum_local_changed_fraction:
                classification, reason = 'static-or-jitter', 'Broad image features are stable after an affine illumination correction'
            else:
                reason = 'Large residual change remains despite a nearly static homography'
        elif phase_agrees and changed <= p.maximum_local_changed_fraction:
            classification, reason = 'global-motion-candidate', 'Broad geometric support and phase shift agree on image motion'
        else:
            reason = 'Phase and homography disagree, or unexplained residual change is too large'
    if classification == 'ambiguous':
        issues.append(reason)
    return dict(classification=classification, reason=reason, dimensions=dict(width=original_width, height=original_height),
                analysisDimensions=dict(width=width, height=height), textureBefore=qualities[0], textureAfter=qualities[1],
                phaseCorrelation=phase, orbHomography=geometric, photometricResidual=residual, jitterThresholdPixels=float(jitter),
                problems=issues, thresholds=asdict(p), thresholdsValidatedForInstallation=False,
                cameraMotorIdentified=False, motorCommandsIssued=False,
                limitations=["Global image motion is consistent with camera motion, but a moving scene filling the view can give the same evidence.",
                             "A single homography may fail for parallax, rolling shutter, occlusion or articulated motion; disagreement stays ambiguous.",
                             "Localized shadows, reflections or exposure changes can resemble local object motion.",
                             "Images alone do not establish camera pan/tilt angle, motor identity, joint calibration or safe movement."])


def analyze_sequence(before: np.ndarray, displaced: np.ndarray, after: np.ndarray | None = None, parameters: Parameters | None = None) -> dict:
    forward = compare_images(before, displaced, parameters)
    result = dict(beforeToDisplaced=forward, repeatControl=None, conclusion=forward['classification'],
                  cameraMotorIdentified=False, motorCommandsIssued=False)
    if after is None:
        return result
    backward, closure = compare_images(displaced, after, parameters), compare_images(before, after, parameters)
    consistent = False
    cosine = None
    if forward['classification'] == backward['classification'] == 'global-motion-candidate' and closure['classification'] == 'static-or-jitter':
        vectors = [np.array([pair['orbHomography']['projectedGridMotion']['medianDxPixels'],
                             pair['orbHomography']['projectedGridMotion']['medianDyPixels']]) for pair in (forward, backward)]
        product = float(np.linalg.norm(vectors[0]) * np.linalg.norm(vectors[1]))
        if product > 0:
            cosine = float(np.dot(*vectors) / product)
            consistent = cosine < -.8
    conclusion = 'reversible-global-image-motion-candidate' if consistent else 'ambiguous-repeat-control'
    if all(pair['classification'] == 'static-or-jitter' for pair in (forward, backward, closure)):
        conclusion = 'stable-through-sequence'
    elif forward['classification'] == backward['classification'] == 'local-motion-candidate' and closure['classification'] == 'static-or-jitter':
        conclusion = 'reversible-local-image-change-candidate'
    result.update(conclusion=conclusion, repeatControl=dict(displacedToAfter=backward, beforeToAfter=closure,
                  oppositeMedianMotionCosine=cosine, reversibleGlobalCandidate=consistent,
                  limitation="A return frame controls drift/exposure and reversibility; it still cannot establish which motor moved the camera."))
    return result


def load_image(path: Path) -> tuple[np.ndarray, dict]:
    if not path.is_file() or path.stat().st_size > 32 * 1024 * 1024:
        raise ValueError("Input must be a local image file of at most 32 MiB")
    data = path.read_bytes()
    image = cv2.imdecode(np.frombuffer(data, np.uint8), cv2.IMREAD_COLOR)
    if image is None or image.shape[0] * image.shape[1] > 16_000_000:
        raise ValueError("Image could not be decoded or exceeds 16 million pixels")
    return image, dict(path=str(path.resolve()), sha256=hashlib.sha256(data).hexdigest(),
                       width=image.shape[1], height=image.shape[0], bytes=len(data))


def input_duplicates(inputs: dict) -> list[list[str]]:
    groups = {}
    for name, metadata in inputs.items():
        if metadata is not None:
            groups.setdefault(metadata['sha256'], []).append(name)
    return [names for names in groups.values() if len(names) > 1]


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--before', required=True, type=Path)
    parser.add_argument('--displaced', required=True, type=Path)
    parser.add_argument('--after', type=Path)
    parser.add_argument('--output', type=Path)
    parser.add_argument('--max-dimension', type=int, default=1280)
    args = parser.parse_args()
    try:
        if args.output:
            for original in (args.before, args.displaced, args.after):
                if original is not None and (args.output.resolve() == original.resolve() or
                        args.output.exists() and original.exists() and args.output.samefile(original)):
                    raise ValueError("JSON output must not overwrite an original input image")
        before, meta_before = load_image(args.before)
        displaced, meta_displaced = load_image(args.displaced)
        after, meta_after = load_image(args.after) if args.after else (None, None)
        cv2.setNumThreads(1)
        result = analyze_sequence(before, displaced, after, Parameters(max_dimension=args.max_dimension))
        inputs = dict(before=meta_before, displaced=meta_displaced, after=meta_after)
        result.update(schemaVersion=1, observedAt=datetime.now(timezone.utc).isoformat(),
                      software=dict(opencv=cv2.__version__, numpy=np.__version__, sourceSha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest()),
                      inputs=inputs, duplicateInputContent=input_duplicates(inputs),
                      temporalBaselineEstablished=False,
                      temporalBaselineNote="Image files alone do not verify independent capture timestamps. Identical input content cannot establish jitter/exposure coverage.")
        text = json.dumps(result, indent=2, allow_nan=False) + '\n'
        if args.output:
            args.output.parent.mkdir(parents=True, exist_ok=True)
            args.output.write_text(text)
        else:
            print(text, end='')
        return 0
    except (ValueError, OSError, cv2.error) as error:
        print(json.dumps(dict(error=str(error), motorCommandsIssued=False)), file=sys.stderr)
        return 2


if __name__ == '__main__':
    raise SystemExit(main())
