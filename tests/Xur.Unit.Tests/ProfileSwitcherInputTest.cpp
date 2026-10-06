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

    PadState queued;
    queued.keys[BTN_SELECT] = queued.keys[BTN_START] = true;
    assert(queued.read(EV_KEY, BTN_DPAD_DOWN, 1) == PadAction::None);
    queued.read(EV_KEY, BTN_DPAD_DOWN, 0);
    queued.read(EV_KEY, BTN_SELECT, 0); queued.read(EV_KEY, BTN_START, 0);
    assert(!queued.armed);
    queued.read(EV_SYN, SYN_REPORT, 0);
    assert(queued.armed && queued.read(EV_KEY, BTN_DPAD_DOWN, 1) == PadAction::Down);
    assert(queued.read(EV_KEY, BTN_DPAD_DOWN, 1) == PadAction::None);
    assert(queued.read(EV_KEY, BTN_DPAD_DOWN, 2) == PadAction::None);
    assert(queued.read(EV_KEY, BTN_DPAD_DOWN, 0) == PadAction::None);
    assert(queued.read(EV_KEY, BTN_DPAD_UP, 1) == PadAction::Up);
    queued.read(EV_KEY, BTN_DPAD_UP, 0);
    assert(queued.read(EV_KEY, BTN_SOUTH, 1) == PadAction::Accept);
    assert(queued.read(EV_KEY, BTN_SOUTH, 1) == PadAction::None);
    assert(queued.read(EV_KEY, BTN_SOUTH, 2) == PadAction::None);
    queued.read(EV_KEY, BTN_SOUTH, 0);
    assert(queued.read(EV_KEY, BTN_EAST, 1) == PadAction::Back);

    PadState hat;
    hat.hatY = -1; hat.neutral(); assert(!hat.armed);
    hat.read(EV_ABS, ABS_HAT0Y, 0); hat.read(EV_SYN, SYN_REPORT, 0);
    assert(hat.armed && hat.read(EV_ABS, ABS_HAT0Y, 1) == PadAction::Down);
    assert(hat.read(EV_KEY, BTN_DPAD_DOWN, 1) == PadAction::None);
    assert(hat.read(EV_ABS, ABS_HAT0Y, 0) == PadAction::None);
    assert(hat.read(EV_KEY, BTN_DPAD_DOWN, 0) == PadAction::None);
    assert(hat.read(EV_KEY, BTN_DPAD_UP, 1) == PadAction::Up);
    assert(hat.read(EV_ABS, ABS_HAT0Y, -1) == PadAction::None);
    hat.read(EV_KEY, BTN_DPAD_UP, 0);
    assert(hat.read(EV_ABS, ABS_HAT0Y, 0) == PadAction::None);
    hat.armed = false; hat.hatX = 1; hat.neutral(); assert(!hat.armed);
    hat.read(EV_ABS, ABS_HAT0X, 0); hat.read(EV_SYN, SYN_REPORT, 0); assert(hat.armed);

    PadState contact;
    contact.keys[BTN_TOUCH] = true; contact.neutral(); assert(contact.armed);
    assert(contact.read(EV_ABS, ABS_HAT0Y, -1) == PadAction::Up);
    assert(contact.read(EV_KEY, BTN_DPAD_LEFT, 1) == PadAction::Left);
    assert(contact.read(EV_ABS, ABS_HAT0X, -1) == PadAction::None);
    contact.read(EV_KEY, BTN_DPAD_LEFT, 0); contact.read(EV_ABS, ABS_HAT0X, 0);
    assert(contact.read(EV_ABS, ABS_HAT0X, 1) == PadAction::Right);
    assert(contact.read(EV_KEY, BTN_DPAD_RIGHT, 1) == PadAction::None);
    std::cout << "Controller chords and input packets: release gate, digital/hat D-pad, duplicate mappings, fresh A/B and HID contact indicators passed\n";
}
