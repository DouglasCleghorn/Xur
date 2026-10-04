#pragma once
#include <array>
#include <chrono>
#include <linux/input-event-codes.h>

// Independent state per physical/virtual pad, using monotonic time. A held
// opening chord fires once and must become neutral before menu navigation.
struct PadState {
    std::array<bool, KEY_MAX + 1> keys{};
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
        for (bool down : keys) if (down) return;
        armed = true;
    }
};
