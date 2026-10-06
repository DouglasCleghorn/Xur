#define XUR_SWITCHER_UI_TEST
#include "main.cpp"
#include <iostream>
#include <stdexcept>

struct SwitcherChecks {
    static void run() {
        QDir().mkpath(".build"); QTemporaryDir files(QDir::currentPath() + "/.build/switcher-ui-XXXXXX");
        QLocalServer broker; const QString address = files.path() + "/broker.sock";
        if (!broker.listen(address)) throw std::runtime_error("Fixture broker failed");
        int checks = 0; auto check = [&](bool value, const char *name) { if (!value) throw std::runtime_error(name); ++checks; };
        auto wait = [](const std::function<bool()> &predicate) {
            QElapsedTimer timer; timer.start();
            while (!predicate()) { if (timer.elapsed() > 5000) throw std::runtime_error("Native picker timed out"); QCoreApplication::processEvents(); QThread::msleep(5); }
            QCoreApplication::processEvents();
        };
        auto workload = [](QString id, QString name, QString kind) { return Object{{"id", id}, {"name", name}, {"fingerprint", id + "-v1"}, {"recipe", Object{{"name", name}, {"kind", kind}}}}; };
        const auto assistant = workload("assistant", "Assistant", "Model"), gaming = workload("gaming", "Gaming desk", "Workstation"), studio = workload("studio", "Studio desk", "Workstation");
        const Object loaded{{"id", "1"}, {"name", "Gaming"}, {"revision", 1}, {"workloads", QJsonArray{gaming, assistant}}};
        const Object other{{"id", "2"}, {"name", "Studio"}, {"revision", 1}, {"workloads", QJsonArray{studio, assistant}}};
        const QJsonArray instances{Object{{"id", "gaming"}, {"fingerprint", "gaming-v1"}, {"state", "running"}}, Object{{"id", "assistant"}, {"fingerprint", "assistant-v1"}, {"state", "running"}}};
        bool busy = false, cancelling = false, denied = false, expired = false;
        QVector<Object> calls;
        QObject::connect(&broker, &QLocalServer::newConnection, [&] {
            auto socket = broker.nextPendingConnection(); auto bytes = std::make_shared<QByteArray>();
            QObject::connect(socket, &QLocalSocket::disconnected, socket, &QObject::deleteLater);
            QObject::connect(socket, &QLocalSocket::readyRead, socket, [&, socket, bytes] {
                bytes->append(socket->readAll()); if (bytes->size() < 4) return;
                auto size = qFromBigEndian<quint32>(bytes->constData()); if (bytes->size() < 4 + qint64(size)) return;
                const Object request = QJsonDocument::fromJson(bytes->mid(4)).object(); calls.append(request); bytes->clear();
                const auto action = request["action"].toString(); Object data;
                if (action == "state") data = Object{{"profiles", QJsonArray{loaded, other}}, {"active", loaded}, {"runtime", Object{{"instances", instances}}}, {"busy", busy}, {"operation", busy ? QJsonValue(Object{{"id", "running-plan"}, {"stage", cancelling ? "Cancelling" : "Applying"}, {"currentAction", "Starting model"}}) : QJsonValue()}};
                else if (action == "preview" || action == "preview-unload") {
                    const bool unload = action == "preview-unload"; Object target = unload ? loaded : other; if (unload) target["workloads"] = QJsonArray{};
                    QJsonArray steps;
                    steps.append(Object{{"kind", unload ? "Stop" : "Keep"}, {"workloadId", "assistant"}});
                    steps.append(Object{{"kind", "Stop"}, {"workloadId", "gaming"}});
                    if (!unload) steps.append(Object{{"kind", "Start"}, {"workloadId", "studio"}});
                    data = Object{{"id", unload ? "unload-plan" : "load-plan"}, {"digest", "fixture-digest"}, {"unload", unload}, {"target", target}, {"expires", QDateTime::currentDateTimeUtc().addSecs(expired ? -5 : 60).toString(Qt::ISODateWithMs)}, {"steps", steps}};
                } else if (action == "apply") data = Object{{"stage", "Applying"}};
                else if (action == "cancel") { if (request["id"] != "running-plan") throw std::runtime_error("Cancellation must bind the active operation"); cancelling = true; }
                else throw std::runtime_error("Unexpected picker operation");
                auto body = QJsonDocument(Object{{"ok", !denied}, {"data", data}, {"error", "Workstation profile controls are disabled."}}).toJson(QJsonDocument::Compact);
                QByteArray frame(4, '\0'); qToBigEndian<quint32>(body.size(), frame.data()); socket->write(frame + body); socket->flush();
            });
        });
        auto applies = [&] { return std::count_if(calls.begin(), calls.end(), [](const auto &c) { return c["action"] == "apply"; }); };
        auto cancels = [&] { return std::count_if(calls.begin(), calls.end(), [](const auto &c) { return c["action"] == "cancel"; }); };
        Switcher picker(geteuid()); picker.server = address; picker.showPicker("controller"); wait([&] { return picker.profiles->count() == 2; });
        using Target = Switcher::ControllerTarget;
        check(picker.pages->count() == 2 && picker.pages->currentIndex() == 0, "Picker opens without a login page");
        check(picker.findChildren<QLineEdit *>().size() == 1, "No administrator credentials requested");
        check(picker.profiles->currentRow() == 1, "First selectable profile is selected");
        Inputs::Pad pad; pad.grabbed = true;
        pad.state.keys[BTN_SELECT] = pad.state.keys[BTN_START] = true;
        auto event = [&](unsigned short type, unsigned short code, int value, bool syncing = false) {
            input_event sample{}; sample.type = type; sample.code = code; sample.value = value;
            picker.input.event(pad, sample, syncing);
        };
        auto button = [&](unsigned short code) {
            event(EV_KEY, code, 1); event(EV_SYN, SYN_REPORT, 0);
            event(EV_KEY, code, 0); event(EV_SYN, SYN_REPORT, 0);
        };
        auto hat = [&](int value, bool syncing = false) {
            event(EV_ABS, ABS_HAT0Y, value, syncing); event(EV_SYN, SYN_REPORT, 0, syncing);
            event(EV_ABS, ABS_HAT0Y, 0, syncing); event(EV_SYN, SYN_REPORT, 0, syncing);
        };
        button(BTN_DPAD_DOWN); button(BTN_SOUTH);
        check(picker.profiles->currentRow() == 1 && picker.pages->currentIndex() == 0, "Held opening chord suppresses navigation and acceptance");
        // All packets can be queued in one timer read. Releasing the opening
        // chord must arm the next D-pad press before that queue is drained.
        event(EV_KEY, BTN_SELECT, 0); event(EV_KEY, BTN_START, 0); event(EV_SYN, SYN_REPORT, 0);
        event(EV_KEY, BTN_DPAD_DOWN, 1); event(EV_SYN, SYN_REPORT, 0);
        check(picker.controllerTarget == Target::StopAll, "Queued chord release arms the next digital D-pad packet");
        event(EV_KEY, BTN_DPAD_DOWN, 0); event(EV_SYN, SYN_REPORT, 0);
        event(EV_ABS, ABS_HAT0Y, -1); event(EV_SYN, SYN_REPORT, 0);
        check(picker.profiles->currentRow() == 1, "Hat D-pad reaches profiles through the captured input handler");
        event(EV_ABS, ABS_HAT0Y, 0); event(EV_SYN, SYN_REPORT, 0);
        event(EV_ABS, ABS_HAT0Y, 1); event(EV_KEY, BTN_DPAD_DOWN, 1); event(EV_SYN, SYN_REPORT, 0);
        check(picker.controllerTarget == Target::StopAll, "Combined hat and digital D-pad reports move only once");
        event(EV_ABS, ABS_HAT0Y, 0); event(EV_KEY, BTN_DPAD_DOWN, 0); event(EV_SYN, SYN_REPORT, 0);
        hat(-1);
        pad.grabbed = false; hat(1);
        check(picker.controllerTarget == Target::Profiles, "Failed controller capture cannot navigate the overlay");
        pad.grabbed = true; hat(1, true);
        check(picker.controllerTarget == Target::Profiles, "Overrun recovery events cannot navigate the overlay");
        pad.state.armed = false; event(EV_KEY, BTN_TOUCH, 1); event(EV_SYN, SYN_REPORT, 0); hat(1);
        check(picker.controllerTarget == Target::StopAll, "HID contact indicators do not block D-pad navigation");
        hat(-1);
        button(BTN_SOUTH); wait([&] { return picker.apply->isEnabled(); });
        check(picker.pages->currentIndex() == 1 && picker.reviewTitle->text() == "Studio", "Controller A opens profile review");
        check(picker.warning->isVisible(), "Review warns before stopping a workstation");
        check(applies() == 0, "Review does not apply"); button(BTN_EAST);
        check(picker.pages->currentIndex() == 0, "Controller B returns to picker");
        wait([&] { return picker.profiles->count() == 2; });
        button(BTN_DPAD_DOWN); check(picker.controllerTarget == Target::StopAll, "D-pad reaches the separate Stop all workloads action");
        button(BTN_SOUTH); wait([&] { return picker.apply->isEnabled(); });
        check(picker.plan["unload"].toBool() && picker.apply->text() == "Stop all workloads", "Controller A reviews stopping all workloads");
        check(picker.warning->isVisible() && applies() == 0, "Unload warns and requires separate approval");
        check(picker.controllerTarget == Target::Back && picker.back->text() == "Back to profiles", "Review starts on an explicit Back to profiles action");
        button(BTN_DPAD_RIGHT); check(picker.controllerTarget == Target::Apply && picker.apply->property("controllerSelected").toBool(), "Right selects and visibly highlights confirmation without desktop focus");
        button(BTN_DPAD_LEFT); check(picker.controllerTarget == Target::Back, "Left selects Back without applying");
        button(BTN_DPAD_RIGHT);
        button(BTN_SOUTH); wait([&] { return applies() == 1 && picker.pages->currentIndex() == 0; });
        auto approved = *std::find_if(calls.begin(), calls.end(), [](const auto &c) { return c["action"] == "apply"; });
        check(approved["id"] == "unload-plan" && approved["digest"] == "fixture-digest", "Unload approves exact reviewed plan");
        check(approved["trigger"] == "controller", "Controller trigger is retained");
        picker.reject(); picker.shortcut.trigger(); wait([&] { return picker.profiles->count() == 2; });
        check(picker.trigger == "keyboard", "Keyboard shortcut records its trigger");
        picker.reject(); picker.showPicker(); wait([&] { return picker.profiles->count() == 2; });
        check(picker.trigger == "plasma-menu", "Launcher records its trigger");
        hat(1); hat(1); check(picker.controllerTarget == Target::Retry && picker.retry->property("controllerSelected").toBool(), "D-pad reaches and highlights Try again");
        button(BTN_SOUTH); wait([&] { return picker.profiles->count() == 2; });
        check(picker.controllerTarget == Target::Profiles, "Controller A on Try again refreshes profiles");
        hat(-1); hat(-1); check(picker.controllerTarget == Target::Shortcuts, "D-pad reaches Shortcuts");
        const int previousHold = picker.input.hold;
        const bool previousShoulders = picker.input.shoulders;
        QTimer::singleShot(0, [&] {
            event(EV_KEY, BTN_SOUTH, 0); event(EV_SYN, SYN_REPORT, 0);
            check(bool(picker.modalAction), "Controller A opens the Shortcuts dialog");
            button(BTN_DPAD_RIGHT); button(BTN_DPAD_DOWN); button(BTN_DPAD_RIGHT); button(BTN_DPAD_DOWN); button(BTN_SOUTH);
        });
        button(BTN_SOUTH);
        check(picker.input.shoulders != previousShoulders && picker.input.hold == previousHold + 100, "D-pad adjusts the chord and hold duration and A saves shortcuts");
        QTimer::singleShot(0, [&] { event(EV_KEY, BTN_SOUTH, 0); event(EV_SYN, SYN_REPORT, 0); button(BTN_EAST); });
        button(BTN_SOUTH); check(!picker.modalAction, "Controller B cancels Shortcuts and restores picker navigation");
        busy = true; picker.refresh(); wait([&] { return !picker.error->isHidden(); });
        const auto busyItems = picker.profiles->findItems("", Qt::MatchContains);
        check(std::all_of(busyItems.begin(), busyItems.end(), [](auto item) { return item->flags() & Qt::ItemIsEnabled; }) && picker.unload->isEnabled(), "Profiles and Stop all remain selectable during a load");
        hat(1); button(BTN_SOUTH); wait([&] { return cancels() == 1 && picker.error->text().contains("running action"); });
        check(picker.pages->currentIndex() == 1 && !picker.apply->isEnabled(), "Choosing a replacement cancels the active change and waits before review");
        button(BTN_EAST); wait([&] { return picker.profiles->count() == 2; });
        check(picker.controllerTarget == Target::Profiles && picker.profiles->currentRow() == 1, "Back remains available during cancellation and restores the selected profile");
        hat(1); button(BTN_SOUTH); wait([&] { return cancels() == 2; });
        busy = false; wait([&] { return picker.apply->isEnabled(); });
        check(picker.plan["unload"].toBool() && picker.reviewTitle->text() == "Stop all workloads", "The latest selection is reviewed after cancellation settles");
        check(applies() == 1, "Interrupting and navigating never bypasses replacement confirmation");
        button(BTN_EAST); wait([&] { return picker.profiles->count() == 2; });
        expired = true; picker.refresh(); wait([&] { return picker.profiles->count() == 2; });
        button(BTN_SOUTH); wait([&] { return picker.apply->isEnabled(); }); hat(1); button(BTN_SOUTH);
        check(picker.error->text().contains("expired") && applies() == 1, "Expired review never applies"); button(BTN_EAST);
        denied = true; picker.refresh(); wait([&] { return picker.error->text().contains("disabled"); });
        check(picker.controllerTarget == Target::Retry, "Policy error selects controller retry");
        denied = false; expired = false; button(BTN_SOUTH); wait([&] { return picker.profiles->count() == 2; });
        check(picker.pages->currentIndex() == 0 && picker.error->isHidden(), "Controller A retries without login");
        button(BTN_EAST); check(!picker.isVisible(), "Controller B closes picker");
        hat(1); button(BTN_SOUTH);
        check(applies() == 1 && !picker.isVisible(), "Closed overlay ignores queued controller input");
        Switcher untrusted(geteuid() + 1); untrusted.server = address; untrusted.showPicker(); wait([&] { return !untrusted.error->isHidden(); });
        check(untrusted.error->text().contains("not trusted") && untrusted.profiles->count() == 0, "Kernel credentials reject an untrusted broker"); untrusted.reject();
        std::cout << "Native picker: " << checks << " checks passed\n";
    }
};
int main(int argc, char **argv) {
    QDir().mkpath(".build/evidence"); QTemporaryDir configuration(QDir::currentPath() + "/.build/evidence/switcher-config-XXXXXX");
    qputenv("XDG_CONFIG_HOME", configuration.path().toUtf8());
    QApplication app(argc, argv); app.setOrganizationName("XurFixture"); app.setApplicationName("profile-switcher-checks");
    app.setStyleSheet(SwitcherStyle());
    try { SwitcherChecks::run(); return 0; } catch (const std::exception &error) { std::cerr << error.what() << '\n'; return 1; }
}
