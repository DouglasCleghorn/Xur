/* Original Xur adapter: MIT. Linked AprilTag code retains its BSD-2-Clause notice.
 * Supplied undistorted pixel corners in; both upstream planar pose candidates out.
 * No camera, serial or motor APIs. Corner order is AprilTag's detection order.
 */
#include <errno.h>
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include "apriltag_pose.h"
#include "common/homography.h"

static int candidate(apriltag_pose_t *pose, double error, int comma)
{
    if (!pose->R || !pose->t || !isfinite(error)) return comma;
    for (int i = 0; i < 9; i++) if (!isfinite(pose->R->data[i])) return comma;
    for (int i = 0; i < 3; i++) if (!isfinite(pose->t->data[i])) return comma;
    printf("%s{\"rotation\":[", comma ? "," : "");
    for (int i = 0; i < 9; i++) printf("%s%.17g", i ? "," : "", pose->R->data[i]);
    printf("],\"translation\":[");
    for (int i = 0; i < 3; i++) printf("%s%.17g", i ? "," : "", pose->t->data[i]);
    printf("],\"objectSpaceError\":%.17g}", error);
    return 1;
}

int main(int argc, char **argv)
{
    if (argc != 14) { fprintf(stderr, "Expected edge-m fx fy cx cy and eight undistorted corner coordinates.\n"); return 2; }
    double values[13];
    for (int i = 0; i < 13; i++) {
        char *end; errno = 0; values[i] = strtod(argv[i + 1], &end);
        if (errno || *end || end == argv[i + 1] || !isfinite(values[i]) || fabs(values[i]) > 1e7) return 2;
    }
    if (values[0] <= 0 || values[0] > 2 || values[1] <= 0 || values[2] <= 0) return 2;
    apriltag_detection_t detection = {0};
    zarray_t *correspondences = zarray_create(sizeof(float[4]));
    for (int i = 0; i < 4; i++) {
        detection.p[i][0] = values[5 + i * 2]; detection.p[i][1] = values[6 + i * 2];
        float point[4] = {(i == 1 || i == 2) ? 1 : -1, i < 2 ? 1 : -1,
                          detection.p[i][0], detection.p[i][1]};
        zarray_add(correspondences, point);
    }
    detection.H = homography_compute(correspondences, HOMOGRAPHY_COMPUTE_FLAG_SVD);
    zarray_destroy(correspondences);
    if (!detection.H) return 3;
    apriltag_detection_info_t info = {&detection, values[0], values[1], values[2], values[3], values[4]};
    apriltag_pose_t first = {0}, second = {0}; double error1, error2;
    estimate_tag_pose_orthogonal_iteration(&info, &error1, &first, &error2, &second, 50);
    printf("{\"poses\":["); int comma = candidate(&first, error1, 0); candidate(&second, error2, comma); printf("]}\n");
    if (first.R) matd_destroy(first.R);
    if (first.t) matd_destroy(first.t);
    if (second.R) matd_destroy(second.R);
    if (second.t) matd_destroy(second.t);
    matd_destroy(detection.H);
    return 0;
}
