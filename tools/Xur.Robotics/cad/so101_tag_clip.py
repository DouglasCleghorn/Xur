#!/usr/bin/env python3
"""MIT-licensed parametric prototype: SO-101 positive-Z upper-arm rail tag clip.

Python CAD source; exports a watertight STL, editable OpenSCAD, validation JSON,
and private review images. Units are mm. No tag pattern is generated.
See so101-tag-clip.md before printing or fitting.
"""
from pathlib import Path
import argparse
import json
import numpy as np
import trimesh
from shapely.geometry import Polygon

# Print coordinates: flat label face on Z=0, collar grows upward.
FACE_W = 35.0
FACE_H = 43.0
PLATE_T = 2.4
CLIP_W = 6.0                 # along the link; upstream X=43..49
RAIL_W = 24.0
SIDE_CLEARANCE = 0.25
WALL = 1.8
SEAT_Z = 3.0                 # upstream rail top contacts this plane
HOOK_HALF_OPENING = 11.5     # 23 mm mouth vs 24 mm rail maximum
HOOK_Z = 16.0
TIP_Z = 17.2
LINK_X = 46.0
LINK_Y = 12.0
LINK_TOP_Z = 31.7
UPSTREAM_COMMIT = 'a758567c3978dfeefe282ede0500085a48fe8f78'


def cross_section():
    """One solid open C-profile with 45-degree entry/retention ramps.

    Coordinates are (print X, print Z); extrude along print Y. The rounded
    upstream rail itself is NOT copied or included in the exported clip.
    """
    inner = RAIL_W / 2 + SIDE_CLEARANCE
    outer = inner + WALL
    root = outer + 1.6
    left = [(-root, PLATE_T - 0.2), (-outer, PLATE_T + 1.4),
            (-outer, TIP_Z), (-(HOOK_HALF_OPENING + TIP_Z - HOOK_Z), TIP_Z),
            (-HOOK_HALF_OPENING, HOOK_Z), (-inner, HOOK_Z - (inner - HOOK_HALF_OPENING)),
            (-inner, SEAT_Z)]
    return left + [(-x, z) for x, z in reversed(left)]


def build():
    plate = trimesh.creation.box([FACE_W, FACE_H, PLATE_T])
    plate.apply_translation([0, 0, PLATE_T / 2])
    clip = trimesh.creation.extrude_polygon(Polygon(cross_section()), CLIP_W)
    # Polygon XY -> print XZ, extrusion Z -> print -Y, keeping determinant +1.
    clip.apply_transform([[1, 0, 0, 0], [0, 0, -1, CLIP_W / 2],
                          [0, 1, 0, 0], [0, 0, 0, 1]])
    return trimesh.boolean.union([plate, clip], engine='manifold')


def install_transform():
    # Column vectors. Upstream STL frame only: not a URDF/robot calibration.
    return np.array([[0., 1., 0., LINK_X], [1., 0., 0., LINK_Y],
                     [0., 0., -1., LINK_TOP_Z + SEAT_Z], [0., 0., 0., 1.]])


def openscad():
    # Standalone editable CAD companion generated from exactly the same profile.
    profile = json.dumps(cross_section())
    return f'''// Original Xur prototype, MIT. Units mm. See source design notes.
// Flat white label face: print Z=0. No marker artwork in this model.
face_width = {FACE_W}; face_height = {FACE_H}; plate_thickness = {PLATE_T};
clip_width = {CLIP_W};
profile = {profile}; // [X,Z]; retention/entry slopes are 45 degrees.
union() {{
  translate([-face_width/2,-face_height/2,0])
    cube([face_width,face_height,plate_thickness]);
  translate([0,clip_width/2,0]) rotate([90,0,0])
    linear_extrude(height=clip_width) polygon(profile);
}}
'''


def rasterize(items, view, size=900):
    """Orthographic CAD preview with a depth buffer (no display/GPU required)."""
    n = np.array(view, dtype=float); n /= np.linalg.norm(n)
    right = np.cross([0, 0, 1], n); right /= np.linalg.norm(right)
    up = np.cross(n, right)
    basis = np.array([right, up, n]).T
    all_points = np.vstack([m.vertices for m, _ in items]) @ basis
    low, high = all_points[:, :2].min(0), all_points[:, :2].max(0)
    scale = (size - 60) / max(high - low)
    center = (low + high) / 2
    pixels = np.full((size, size, 3), 250, dtype=np.uint8)
    depth = np.full((size, size), -np.inf)
    light = np.array([-.3, -.6, 1.]); light /= np.linalg.norm(light)
    for mesh, color in items:
        vertices = mesh.vertices @ basis
        vertices[:, :2] = (vertices[:, :2] - center) * scale + size / 2
        vertices[:, 1] = size - vertices[:, 1]
        for face, normal in zip(mesh.faces, mesh.face_normals):
            tri = vertices[face]
            xmin, ymin = np.maximum(np.floor(tri[:, :2].min(0)).astype(int), 0)
            xmax, ymax = np.minimum(np.ceil(tri[:, :2].max(0)).astype(int), size - 1)
            a, b, c = tri
            den = (b[1]-c[1])*(a[0]-c[0]) + (c[0]-b[0])*(a[1]-c[1])
            if abs(den) < 1e-10:
                continue
            yy, xx = np.mgrid[ymin:ymax+1, xmin:xmax+1]
            u = ((b[1]-c[1])*(xx+.5-c[0]) + (c[0]-b[0])*(yy+.5-c[1]))/den
            v = ((c[1]-a[1])*(xx+.5-c[0]) + (a[0]-c[0])*(yy+.5-c[1]))/den
            w = 1-u-v
            z = u*a[2] + v*b[2] + w*c[2]
            patch = depth[ymin:ymax+1, xmin:xmax+1]
            mask = (u >= -1e-8) & (v >= -1e-8) & (w >= -1e-8) & (z > patch)
            shade = .48 + .52 * max(0, np.dot(normal, light))
            pixels[ymin:ymax+1, xmin:xmax+1][mask] = np.array(color) * shade
            patch[mask] = z[mask]
    return pixels


def render(mesh, link, out):
    import matplotlib
    matplotlib.use('Agg')
    import matplotlib.pyplot as plt
    fig = plt.figure(figsize=(14, 7))
    ax = fig.add_subplot(1, 2, 1)
    ax.imshow(rasterize([(mesh, [225, 233, 238])], [1, -1.6, 1.1]))
    ax.axis('off')
    ax.set_title('Print orientation: label face on bed\n35 × 43 × 17.2 mm; open collar upward')
    ax = fig.add_subplot(1, 2, 2)
    p = np.array(cross_section() + [cross_section()[0]])
    ax.fill(p[:, 0], p[:, 1], color='#b7cdd5', label='Clip section')
    ax.fill([-FACE_W/2, FACE_W/2, FACE_W/2, -FACE_W/2],
            [0, 0, PLATE_T, PLATE_T], color='#b7cdd5')
    if link is not None:
        section = link.section([1, 0, 0], [LINK_X, 0, 0])
        for loop in section.discrete:
            if loop[:, 2].mean() > 0:
                ax.plot(loop[:, 1] - LINK_Y, LINK_TOP_Z + SEAT_Z - loop[:, 2],
                        color='#e59138', linewidth=2, label='Upstream rail at X=46')
    ax.axhline(0, color='#555', linestyle='--', linewidth=.7)
    ax.set_aspect('equal'); ax.set_xlabel('Print X / across rail (mm)')
    ax.set_ylabel('Print Z / from label face (mm)'); ax.grid(alpha=.25)
    ax.set_title('Rail fit section — CAD only\n0.25 mm side clearance; 23 mm clip mouth')
    ax.legend(loc='upper right', fontsize=8)
    fig.tight_layout(); fig.savefig(out / 'clip-review.png', dpi=160, bbox_inches='tight'); plt.close(fig)
    if link is not None:
        installed = mesh.copy(); installed.apply_transform(install_transform())
        fig = plt.figure(figsize=(10, 8)); ax = fig.add_subplot()
        ax.imshow(rasterize([(link, [125, 175, 195]), (installed, [241, 238, 228])],
                            [1, -1.8, 1.0]))
        ax.axis('off')
        ax.set_title('SO-101 upper-arm rail: prototype placement\nLink only; motors, cables and motion envelope NOT checked')
        fig.tight_layout(); fig.savefig(out / 'clip-on-link.png', dpi=160, bbox_inches='tight'); plt.close(fig)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, default=Path('.build/robotics/so101-tag-mount'))
    parser.add_argument('--evidence', type=Path, default=Path('.build/evidence/so101-tag-mount'))
    parser.add_argument('--upstream-stl', type=Path)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True); args.evidence.mkdir(parents=True, exist_ok=True)
    mesh = build()
    assert mesh.is_watertight and mesh.is_winding_consistent and mesh.volume > 0
    assert len(mesh.split()) == 1
    mesh.export(args.output / 'so101-upper-arm-tag-clip.stl')
    (args.output / 'so101-upper-arm-tag-clip.scad').write_text(openscad())
    # Validate the serialized STL, not just the pre-export mesh.
    exported = trimesh.load(args.output / 'so101-upper-arm-tag-clip.stl')
    assert exported.is_watertight and exported.is_winding_consistent
    assert len(exported.split()) == 1 and exported.volume > 0
    assert np.allclose(exported.extents, [FACE_W, FACE_H, TIP_Z])
    downward = (exported.face_normals[:, 2] < -1e-6) & (exported.triangles_center[:, 2] > 1e-5)
    overhang = np.degrees(np.arcsin(-exported.face_normals[downward, 2]))
    assert np.max(overhang, initial=0) <= 45.001
    report = {'design': 'SO101 upper-arm rail adhesive tag clip prototype v1',
              'units': 'mm', 'upstream_commit': UPSTREAM_COMMIT,
              'watertight': bool(exported.is_watertight),
              'winding_consistent': bool(exported.is_winding_consistent),
              'connected_solids': len(exported.split()), 'triangles': len(exported.faces),
              'bounds_mm': exported.bounds.tolist(), 'dimensions_mm': exported.extents.tolist(),
              'volume_mm3': float(exported.volume),
              'maximum_downward_surface_overhang_from_vertical_degrees': float(np.max(overhang, initial=0)),
              'print_to_upstream_stl_transform': install_transform().tolist(),
              'label_reference_center_print_mm': [0, 3.81, 0],
              'physical_fit': 'UNVALIDATED', 'motion_clearance': 'UNVALIDATED',
              'marker_visibility': 'UNVALIDATED'}
    link = None
    if args.upstream_stl:
        link = trimesh.load(args.upstream_stl)
        placed = exported.copy(); placed.apply_transform(install_transform())
        # The empty intersection has no mass center; suppress its 0/0 warning.
        with np.errstate(invalid='ignore'):
            overlap = trimesh.boolean.intersection([link, placed], engine='manifold')
            overlap_volume = 0.0 if len(overlap.faces) == 0 else float(overlap.volume)
        report['link_only_intersection_volume_mm3'] = overlap_volume
        assert abs(overlap_volume) < 0.001, 'Clip interferes with upstream arm link'
    (args.evidence / 'mesh-validation.json').write_text(json.dumps(report, indent=2) + '\n')
    render(exported, link, args.evidence)
    print(json.dumps(report, indent=2))


if __name__ == '__main__':
    main()
