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
        bool busy = false, denied = false, expired = false;
        QVector<Object> calls;
        QObject::connect(&broker, &QLocalServer::newConnection, [&] {
            auto socket = broker.nextPendingConnection(); auto bytes = std::make_shared<QByteArray>();
            QObject::connect(socket, &QLocalSocket::disconnected, socket, &QObject::deleteLater);
            QObject::connect(socket, &QLocalSocket::readyRead, socket, [&, socket, bytes] {
                bytes->append(socket->readAll()); if (bytes->size() < 4) return;
                auto size = qFromBigEndian<quint32>(bytes->constData()); if (bytes->size() < 4 + qint64(size)) return;
                const Object request = QJsonDocument::fromJson(bytes->mid(4)).object(); calls.append(request); bytes->clear();
                const auto action = request["action"].toString(); Object data;
                if (action == "state") data = Object{{"profiles", QJsonArray{loaded, other}}, {"active", loaded}, {"runtime", Object{{"instances", instances}}}, {"operation", busy ? QJsonValue(Object{{"stage", "Applying"}}) : QJsonValue()}};
                else if (action == "preview" || action == "preview-unload") {
                    const bool unload = action == "preview-unload"; Object target = unload ? loaded : other; if (unload) target["workloads"] = QJsonArray{};
                    QJsonArray steps;
                    steps.append(Object{{"kind", unload ? "Stop" : "Keep"}, {"workloadId", "assistant"}});
                    steps.append(Object{{"kind", "Stop"}, {"workloadId", "gaming"}});
                    if (!unload) steps.append(Object{{"kind", "Start"}, {"workloadId", "studio"}});
                    data = Object{{"id", unload ? "unload-plan" : "load-plan"}, {"digest", "fixture-digest"}, {"unload", unload}, {"target", target}, {"expires", QDateTime::currentDateTimeUtc().addSecs(expired ? -5 : 60).toString(Qt::ISODateWithMs)}, {"steps", steps}};
                } else if (action == "apply") data = Object{{"stage", "Applying"}};
                else throw std::runtime_error("Unexpected picker operation");
                auto body = QJsonDocument(Object{{"ok", !denied}, {"data", data}, {"error", "Workstation profile controls are disabled."}}).toJson(QJsonDocument::Compact);
                QByteArray frame(4, '\0'); qToBigEndian<quint32>(body.size(), frame.data()); socket->write(frame + body); socket->flush();
            });
        });
        auto applies = [&] { return std::count_if(calls.begin(), calls.end(), [](const auto &c) { return c["action"] == "apply"; }); };
        Switcher picker(geteuid()); picker.server = address; picker.showPicker("controller"); wait([&] { return picker.profiles->count() == 3; });
        check(picker.pages->count() == 2 && picker.pages->currentIndex() == 0, "Picker opens without a login page");
        check(picker.findChildren<QLineEdit *>().size() == 1, "No administrator credentials requested");
        check(picker.profiles->currentRow() == 1, "First selectable profile is selected");
        picker.input.action("accept"); wait([&] { return picker.apply->isEnabled(); });
        check(picker.pages->currentIndex() == 1 && picker.reviewTitle->text() == "Studio", "Controller A opens profile review");
        check(picker.warning->isVisible(), "Review warns before stopping a workstation");
        check(applies() == 0, "Review does not apply"); picker.input.action("back");
        check(picker.pages->currentIndex() == 0, "Controller B returns to picker");
        picker.input.action("down"); check(picker.profiles->currentRow() == 2, "D-pad reaches Unload all");
        picker.input.action("accept"); wait([&] { return picker.apply->isEnabled(); });
        check(picker.plan["unload"].toBool() && picker.apply->text() == "Unload all", "Controller A reviews Unload all");
        check(picker.warning->isVisible() && applies() == 0, "Unload warns and requires separate approval");
        picker.input.action("down"); check(picker.controllerConfirm, "D-pad selects unload confirmation even without desktop focus");
        picker.input.action("accept"); wait([&] { return applies() == 1 && picker.pages->currentIndex() == 0; });
        auto approved = *std::find_if(calls.begin(), calls.end(), [](const auto &c) { return c["action"] == "apply"; });
        check(approved["id"] == "unload-plan" && approved["digest"] == "fixture-digest", "Unload approves exact reviewed plan");
        check(approved["trigger"] == "controller", "Controller trigger is retained");
        picker.reject(); picker.shortcut.trigger(); wait([&] { return picker.profiles->count() == 3; });
        check(picker.trigger == "keyboard", "Keyboard shortcut records its trigger");
        picker.reject(); picker.showPicker(); wait([&] { return picker.profiles->count() == 3; });
        check(picker.trigger == "plasma-menu", "Launcher records its trigger");
        busy = true; picker.refresh(); wait([&] { return !picker.error->isHidden(); });
        const auto busyItems = picker.profiles->findItems("", Qt::MatchContains);
        check(std::all_of(busyItems.begin(), busyItems.end(), [](auto item) { return !(item->flags() & Qt::ItemIsEnabled); }), "Busy change disables actions");
        busy = false; expired = true; picker.refresh(); wait([&] { return picker.profiles->count() == 3; });
        picker.input.action("accept"); wait([&] { return picker.apply->isEnabled(); }); picker.input.action("down"); picker.input.action("accept");
        check(picker.error->text().contains("expired") && applies() == 1, "Expired review never applies"); picker.input.action("back");
        denied = true; picker.refresh(); wait([&] { return picker.error->text().contains("disabled"); });
        check(picker.controllerRetry, "Policy error selects controller retry");
        denied = false; expired = false; picker.input.action("accept"); wait([&] { return picker.profiles->count() == 3; });
        check(picker.pages->currentIndex() == 0 && picker.error->isHidden(), "Controller A retries without login");
        picker.input.action("back"); check(!picker.isVisible(), "Controller B closes picker");
        Switcher untrusted(geteuid() + 1); untrusted.server = address; untrusted.showPicker(); wait([&] { return !untrusted.error->isHidden(); });
        check(untrusted.error->text().contains("not trusted") && untrusted.profiles->count() == 0, "Kernel credentials reject an untrusted broker"); untrusted.reject();
        std::cout << "Native picker: " << checks << " checks passed\n";
    }
};
int main(int argc, char **argv) {
    QApplication app(argc, argv); app.setOrganizationName("XurFixture"); app.setApplicationName("profile-switcher-checks");
    try { SwitcherChecks::run(); return 0; } catch (const std::exception &error) { std::cerr << error.what() << '\n'; return 1; }
}
