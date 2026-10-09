/* Original Xur adapter: MIT. AprilTag itself retains its BSD-2-Clause license.
 * One grayscale PGM frame in; upstream detections as JSON out. No robot APIs.
 */
#include <math.h>
#include <stdio.h>
#include "apriltag.h"
#include "tagStandard41h12.h"
#include "common/image_u8.h"

int main(int argc, char **argv)
{
    if (argc != 2) {
        fprintf(stderr, "Usage: detect-markers frame.pgm\n");
        return 2;
    }
    image_u8_t *image = image_u8_create_from_pnm(argv[1]);
    if (!image) return 2;
    apriltag_family_t *family = tagStandard41h12_create();
    apriltag_detector_t *detector = apriltag_detector_create();
    apriltag_detector_add_family_bits(detector, family, 0);
    detector->quad_decimate = 1.0;
    detector->nthreads = 1;
    zarray_t *detections = apriltag_detector_detect(detector, image);
    printf("{\"family\":\"tagStandard41h12\",\"width\":%d,\"height\":%d,\"detections\":[",
           image->width, image->height);
    for (int i = 0; i < zarray_size(detections); i++) {
        apriltag_detection_t *tag;
        zarray_get(detections, i, &tag);
        if (!isfinite(tag->decision_margin) || !isfinite(tag->c[0]) || !isfinite(tag->c[1]))
            return 3;
        printf("%s{\"id\":%d,\"hamming\":%d,\"decisionMargin\":%.9f,\"center\":[%.9f,%.9f],\"corners\":[",
               i ? "," : "", tag->id, tag->hamming, tag->decision_margin, tag->c[0], tag->c[1]);
        for (int p = 0; p < 4; p++) {
            if (!isfinite(tag->p[p][0]) || !isfinite(tag->p[p][1])) return 3;
            printf("%s[%.9f,%.9f]", p ? "," : "", tag->p[p][0], tag->p[p][1]);
        }
        printf("]}");
    }
    printf("]}\n");
    apriltag_detections_destroy(detections);
    apriltag_detector_destroy(detector);
    tagStandard41h12_destroy(family);
    image_u8_destroy(image);
    return 0;
}
