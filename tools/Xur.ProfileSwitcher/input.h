#pragma once
#include <array>
#include <chrono>
#include <linux/input-event-codes.h>

enum class PadAction { None, Up, Down, Left, Right, Accept, Back };

// Independent state per physical/virtual pad, using monotonic time. A held
// opening chord fires once and must become neutral before menu navigation.
struct PadState {
    std::array<bool, KEY_MAX + 1> keys{};
    int hatX = 0, hatY = 0;
    bool latched = false, armed = false;
    std::chrono::steady_clock::time_point held{};
    bool chord(bool shoulders = false) const {
        return shoulders ? keys[BTN_TL] && keys[BTN_TR] && keys[BTN_START]
                         : keys[BTN_SELECT] && keys[BTN_START];
    }
    bool tick(std::chrono::steady_clock::time_point now, int milliseconds, bool shoulders = false) {
        if (!chord(shoulders)) {
            held = {};
            if (!keys[BTN_SELECT] && !keys[BTN_START] && !keys[BTN_TL] && !keys[BTN_TR]) latched = false;
            return false;
        }
        if (held == std::chrono::steady_clock::time_point{}) held = now;
        if (latched || now - held < std::chrono::milliseconds(milliseconds)) return false;
        latched = true; armed = false;
        return true;
    }
    void neutral() {
        // Touch/contact indicators on HID pads are not controller buttons.
        // A D-pad represented by hat axes must also be released before arming.
        for (int code = BTN_GAMEPAD; code <= BTN_THUMBR; ++code) if (keys[code]) return;
        for (int code = BTN_DPAD_UP; code <= BTN_DPAD_RIGHT; ++code) if (keys[code]) return;
        if (hatX || hatY) return;
        armed = true;
    }
    int direction() const {
        return hatY ? hatY : int(keys[BTN_DPAD_DOWN]) - int(keys[BTN_DPAD_UP]);
    }
    int horizontal() const {
        return hatX ? hatX : int(keys[BTN_DPAD_RIGHT]) - int(keys[BTN_DPAD_LEFT]);
    }
    PadAction read(unsigned short type, unsigned short code, int value) {
        const int before = direction();
        const int beforeX = horizontal();
        PadAction command = PadAction::None;
        if (type == EV_KEY && code <= KEY_MAX && (value == 0 || value == 1)) {
            const bool edge = value == 1 && !keys[code];
            keys[code] = value == 1;
            if (edge && code == BTN_SOUTH) command = PadAction::Accept;
            if (edge && code == BTN_EAST) command = PadAction::Back;
        } else if (type == EV_ABS && (code == ABS_HAT0X || code == ABS_HAT0Y)) {
            (code == ABS_HAT0X ? hatX : hatY) = (value > 0) - (value < 0);
        } else if (type == EV_SYN && code == SYN_REPORT) {
            // Arm at the release packet, even if a new press is already queued
            // in the same timer read. A press in that packet remains suppressed.
            neutral();
        }
        const int after = direction();
        if (after && after != before) command = after < 0 ? PadAction::Up : PadAction::Down;
        const int afterX = horizontal();
        if (afterX && afterX != beforeX) command = afterX < 0 ? PadAction::Left : PadAction::Right;
        return armed ? command : PadAction::None;
    }
};
