#include "../../tools/Xur.ProfileSwitcher/input.h"
#include <cassert>
#include <iostream>
int main() {
    using namespace std::chrono;
    const auto start = steady_clock::time_point(seconds(10));
    PadState first, second;
    first.keys[BTN_SELECT] = true; second.keys[BTN_START] = true;
    assert(!first.tick(start, 1000) && !second.tick(start, 1000));
    first.keys[BTN_START] = true;
    assert(!first.tick(start, 1000));
    assert(!first.tick(start + milliseconds(999), 1000));
    assert(first.tick(start + seconds(1), 1000));
    assert(!first.armed && !first.tick(start + seconds(3), 1000));
    first.keys[BTN_START] = false;
    assert(!first.tick(start + seconds(4), 1000));
    first.keys[BTN_START] = true;
    assert(!first.tick(start + seconds(5), 1000));
    assert(!first.tick(start + seconds(7), 1000));
    first.keys.fill(false); first.neutral();
    assert(first.armed); assert(!first.tick(start + seconds(8), 1000));
    first.keys[BTN_SELECT] = first.keys[BTN_START] = true;
    assert(!first.tick(start + seconds(9), 1000));
    assert(first.tick(start + seconds(10), 1000));
    PadState shoulders; shoulders.keys[BTN_TL] = shoulders.keys[BTN_TR] = shoulders.keys[BTN_START] = true;
    assert(!shoulders.tick(start, 700, true));
    assert(shoulders.tick(start + milliseconds(700), 700, true));
    PadState interrupted; interrupted.keys[BTN_SELECT] = interrupted.keys[BTN_START] = true;
    assert(!interrupted.tick(start, 1000)); interrupted.keys[BTN_START] = false;
    assert(!interrupted.tick(start + milliseconds(500), 1000)); interrupted.keys[BTN_START] = true;
    assert(!interrupted.tick(start + milliseconds(600), 1000));
    assert(!interrupted.tick(start + milliseconds(1500), 1000));
    assert(interrupted.tick(start + milliseconds(1600), 1000));
    std::cout << "Controller chords: threshold, release latch, independent pads, interruption and alternative binding passed\n";
}
