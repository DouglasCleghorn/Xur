#include "input.h"
#include "style.h"
#include <QtWidgets>
#include <QtNetwork>
#include <QtDBus>
#include <QtEndian>
#include <KGlobalAccel>
#include <libevdev/libevdev.h>
#include <libudev.h>
#include <fcntl.h>
#include <sys/ioctl.h>
#include <sys/stat.h>
#include <sys/socket.h>
#include <unistd.h>
#include <functional>
#include <memory>

using Object = QJsonObject;
#ifndef XUR_SWITCHER_UI_TEST
static const char *service = "dev.xur.ProfileSwitcher";
#endif

class Inputs {
    friend struct SwitcherChecks;
    struct Pad {
        QString path;
        int fd = -1;
        libevdev *device = nullptr;
        PadState state;
        bool grabbed = false;
        ~Pad() { if (device) libevdev_free(device); if (fd >= 0) ::close(fd); }
    };
    std::vector<std::unique_ptr<Pad>> pads;
    QTimer scanTimer, readTimer;
    udev *context = udev_new();
    bool visible = false;
    void event(Pad &pad, const input_event &event, bool syncing) {
        QString command;
        switch (pad.state.read(event.type, event.code, event.value)) {
            case PadAction::Up: command = "up"; break;
            case PadAction::Down: command = "down"; break;
            case PadAction::Left: command = "left"; break;
            case PadAction::Right: command = "right"; break;
            case PadAction::Accept: command = "accept"; break;
            case PadAction::Back: command = "back"; break;
            case PadAction::None: break;
        }
        if (!syncing && visible && pad.grabbed && !command.isEmpty() && action) action(command);
    }
public:
    std::function<void()> open;
    std::function<void(QString)> action;
    std::function<void(QString)> failure;
    bool shoulders = false;
    int hold = 1000;
    Inputs() {
        QObject::connect(&scanTimer, &QTimer::timeout, [&] { scan(); });
        QObject::connect(&readTimer, &QTimer::timeout, [&] { read(); });
        scanTimer.start(2000); readTimer.start(10);
    }
    ~Inputs() { pads.clear(); if (context) udev_unref(context); }
    void capture(bool active) {
        visible = active;
        for (auto &pad : pads) {
            pad->state.armed = false;
            if (active && !pad->grabbed) {
                pad->grabbed = ioctl(pad->fd, EVIOCGRAB, 1) == 0;
                if (!pad->grabbed && failure) failure("Controller input could not be captured. Use the keyboard to avoid also controlling the game.");
            } else if (!active && pad->grabbed) {
                ioctl(pad->fd, EVIOCGRAB, 0); pad->grabbed = false;
            }
        }
    }
    void scan() {
        if (!context) return;
        const QString seat = qEnvironmentVariable("XDG_SEAT");
        if (!seat.startsWith("seat-xur-")) return;
        for (const QString &name : QDir("/dev/input").entryList({"event*"}, QDir::System | QDir::Files)) {
            const QString path = "/dev/input/" + name;
            if (std::any_of(pads.begin(), pads.end(), [&](const auto &pad) { return pad->path == path; })) continue;
            struct stat info{};
            if (::stat(path.toUtf8(), &info) != 0) continue;
            auto node = udev_device_new_from_devnum(context, 'c', info.st_rdev);
            if (!node) continue;
            const auto assigned = udev_device_get_property_value(node, "ID_SEAT");
            const bool ours = assigned && seat == QString::fromUtf8(assigned);
            udev_device_unref(node);
            if (!ours) continue;
            const int fd = ::open(path.toUtf8(), O_RDONLY | O_NONBLOCK | O_CLOEXEC);
            if (fd < 0) continue;
            libevdev *device = nullptr;
            if (libevdev_new_from_fd(fd, &device) < 0) { ::close(fd); continue; }
            if (!libevdev_has_event_code(device, EV_KEY, BTN_SOUTH) || !libevdev_has_event_code(device, EV_KEY, BTN_START)) {
                libevdev_free(device); ::close(fd); continue;
            }
            auto pad = std::make_unique<Pad>(); pad->path = path; pad->fd = fd; pad->device = device;
            for (int code = 0; code <= KEY_MAX; ++code) pad->state.keys[code] = libevdev_get_event_value(device, EV_KEY, code) == 1;
            pad->state.hatX = libevdev_get_event_value(device, EV_ABS, ABS_HAT0X);
            pad->state.hatY = libevdev_get_event_value(device, EV_ABS, ABS_HAT0Y);
            if (visible) pad->grabbed = ioctl(fd, EVIOCGRAB, 1) == 0;
            pads.push_back(std::move(pad));
        }
    }
    void read() {
        for (auto it = pads.begin(); it != pads.end();) {
            auto &pad = **it; input_event event{}; int result;
            bool syncing = false;
            while ((result = libevdev_next_event(pad.device, syncing ? LIBEVDEV_READ_FLAG_SYNC : LIBEVDEV_READ_FLAG_NORMAL, &event)) >= 0) {
                if (result == LIBEVDEV_READ_STATUS_SYNC) { syncing = true; pad.state.armed = false; }
                this->event(pad, event, syncing);
            }
            if (result == -ENODEV || result == -EBADF) { it = pads.erase(it); continue; }
            if (syncing) { pad.state.held = {}; pad.state.latched = pad.state.chord(shoulders); }
            pad.state.neutral();
            if (pad.state.tick(std::chrono::steady_clock::now(), hold, shoulders) && !visible && open) open();
            ++it;
        }
    }
};

class Switcher : public QDialog {
    friend struct SwitcherChecks;
    QSettings preferences;
    Inputs input;
    QAction shortcut{this};
    QStackedWidget *pages = new QStackedWidget;
    QLabel *current = new QLabel, *error = new QLabel;
    QLineEdit *search = new QLineEdit;
    QListWidget *profiles = new QListWidget;
    QVBoxLayout *changes = new QVBoxLayout;
    QPushButton *apply = new QPushButton("Load profile"), *back = new QPushButton("Back to profiles"), *retry = new QPushButton("Try again");
    QPushButton *settings = new QPushButton("Shortcuts"), *close = new QPushButton("Close");
    QPushButton *unload = new QPushButton("Stop all workloads");
    QLabel *reviewTitle = new QLabel, *warning = new QLabel;
    QLabel *navigation = new QLabel;
    QString server = "/run/xur-profile-switcher/switcher.sock", trigger = "plasma-menu";
    uint trustedServerUid;
    Object state, plan, selected;
    enum class ControllerTarget { Profiles, StopAll, Retry, Shortcuts, Close, Back, Apply };
    bool applying = false;
    ControllerTarget controllerTarget = ControllerTarget::Profiles;
    std::function<void(QString)> modalAction;
    int revision = 0;
    static QLabel *label(const QString &text, const char *name = nullptr) {
        auto result = new QLabel(text); result->setTextFormat(Qt::PlainText); result->setWordWrap(true);
        if (name) result->setObjectName(name);
        return result;
    }
    void highlightSelection() {
        for (auto [target, widget] : std::initializer_list<std::pair<ControllerTarget, QWidget *>>{
                {ControllerTarget::Profiles, profiles}, {ControllerTarget::StopAll, unload}, {ControllerTarget::Retry, retry},
                {ControllerTarget::Shortcuts, settings}, {ControllerTarget::Close, close}, {ControllerTarget::Back, back}, {ControllerTarget::Apply, apply}}) {
            widget->setProperty("controllerSelected", controllerTarget == target);
            widget->style()->unpolish(widget); widget->style()->polish(widget); widget->update();
        }
    }
    void focusControl(ControllerTarget target) {
        controllerTarget = target;
        switch (target) {
            case ControllerTarget::Profiles: profiles->setFocus(); break;
            case ControllerTarget::StopAll: unload->setFocus(); break;
            case ControllerTarget::Retry: retry->setFocus(); break;
            case ControllerTarget::Shortcuts: settings->setFocus(); break;
            case ControllerTarget::Close: close->setFocus(); break;
            case ControllerTarget::Back: back->setFocus(); break;
            case ControllerTarget::Apply: apply->setFocus(); break;
        }
        highlightSelection();
    }
    void moveControl(int direction) {
        std::vector<std::pair<ControllerTarget, int>> choices;
        if (settings->isEnabled()) choices.push_back({ControllerTarget::Shortcuts, -1});
        if (close->isEnabled()) choices.push_back({ControllerTarget::Close, -1});
        if (pages->currentIndex() == 0) {
            for (int row = 0; row < profiles->count(); ++row)
                if (profiles->item(row)->flags() & Qt::ItemIsEnabled) choices.push_back({ControllerTarget::Profiles, row});
            if (unload->isEnabled()) choices.push_back({ControllerTarget::StopAll, -1});
            if (retry->isEnabled()) choices.push_back({ControllerTarget::Retry, -1});
        } else {
            if (back->isEnabled()) choices.push_back({ControllerTarget::Back, -1});
            if (apply->isEnabled()) choices.push_back({ControllerTarget::Apply, -1});
        }
        if (choices.empty()) return;
        const auto selected = std::make_pair(controllerTarget, controllerTarget == ControllerTarget::Profiles ? profiles->currentRow() : -1);
        const auto found = std::find(choices.begin(), choices.end(), selected);
        const int count = choices.size(), index = found == choices.end() ? (direction > 0 ? -1 : 0) : std::distance(choices.begin(), found);
        const auto next = choices[(index + direction + count) % count];
        if (next.first == ControllerTarget::Profiles) profiles->setCurrentRow(next.second);
        focusControl(next.first);
    }
    void message(const QString &text) { error->setText(text); error->setVisible(!text.isEmpty()); }
    void request(const QString &path, const Object *body, std::function<void(Object)> completed) {
        Object payload = body ? *body : Object{};
        payload["action"] = path == "/api/profiles" ? "state" : path == "/api/profiles/apply" ? "apply" : path == "/api/profiles/cancel" ? "cancel" : path == "/api/profiles/unload/preview" ? "preview-unload" : "preview";
        if (payload["action"] == "preview") payload["id"] = path.section('/', 3, 3);
        if (payload["action"] == "apply" || payload["action"] == "cancel") payload["trigger"] = trigger;
        const QByteArray data = QJsonDocument(payload).toJson(QJsonDocument::Compact);
        auto reply = new QLocalSocket(this); auto timer = new QTimer(reply); timer->setSingleShot(true);
        auto received = std::make_shared<QByteArray>(); auto finished = std::make_shared<bool>(false);
        const int version = revision;
        auto finish = [this, reply, timer, completed, version, finished](Object envelope) {
            if (*finished) return;
            *finished = true; timer->stop();
            if (version == revision && isVisible()) {
                if (!envelope["ok"].toBool()) {
                    if (applying) { applying = false; back->setEnabled(true); apply->setText(plan["unload"].toBool() ? "Stop all workloads" : "Load profile"); plan = {}; }
                    message(envelope["error"].toString("Could not contact the local manager. Try again.")); apply->setEnabled(false);
                    if (pages->currentIndex() == 0) focusControl(ControllerTarget::Retry);
                } else completed(envelope["data"].toObject());
            }
            reply->abort(); reply->deleteLater();
        };
        connect(reply, &QLocalSocket::connected, this, [this, reply, data, finish] {
            ucred peer{}; socklen_t size = sizeof(peer);
            if (getsockopt(reply->socketDescriptor(), SOL_SOCKET, SO_PEERCRED, &peer, &size) != 0 || peer.uid != trustedServerUid) {
                finish(Object{{"error", "The local profile service is not trusted. Reload the workstation."}}); return;
            }
            QByteArray frame(4, '\0'); qToBigEndian<quint32>(data.size(), frame.data()); reply->write(frame + data);
        });
        connect(reply, &QLocalSocket::readyRead, this, [reply, received, finished, finish] {
            if (*finished) return;
            received->append(reply->readAll());
            if (received->size() < 4) return;
            const auto size = qFromBigEndian<quint32>(received->constData());
            if (!size || size > 4 * 1024 * 1024 || received->size() > 4 + qint64(size)) { finish(Object{}); return; }
            if (received->size() == 4 + qint64(size)) finish(QJsonDocument::fromJson(received->mid(4)).object());
        });
        connect(reply, &QLocalSocket::errorOccurred, this, [finish](QLocalSocket::LocalSocketError) { finish(Object{}); });
        connect(timer, &QTimer::timeout, this, [finish] { finish(Object{}); });
        timer->start(20000); reply->connectToServer(server);
    }
    bool isCurrent(const Object &profile) const {
        auto active = state["active"].toObject();
        if (active["id"] != profile["id"] || active["revision"] != profile["revision"]) return false;
        auto instances = state["runtime"].toObject()["instances"].toArray(), workloads = profile["workloads"].toArray();
        if (instances.size() != workloads.size()) return false;
        for (auto value : workloads) { auto w = value.toObject(); bool found = false;
            for (auto instance : instances) { auto i = instance.toObject(); if (i["id"] == w["id"] && i["fingerprint"] == w["fingerprint"] && i["state"] == "running") found = true; }
            if (!found) return false;
        }
        return true;
    }
    static bool changing(const Object &data) {
        const auto stage = data["operation"].toObject()["stage"].toString();
        return data["busy"].toBool() || stage == "Applying" || stage == "Cancelling" || stage == "Failed";
    }
    void render() {
        profiles->clear(); const QString filter = search->text();
        const bool busy = changing(state);
        auto saved = state["profiles"].toArray();
        std::vector<Object> sorted; for (auto value : saved) sorted.push_back(value.toObject());
        const QJsonValue activeId = state["active"].toObject()["id"];
        std::stable_sort(sorted.begin(), sorted.end(), [&](const auto &a, const auto &b) {
            if ((a["id"] == activeId) != (b["id"] == activeId)) return a["id"] == activeId;
            return QString::localeAwareCompare(a["name"].toString(), b["name"].toString()) < 0;
        });
        for (const auto &profile : sorted) {
            QStringList workloads; for (auto value : profile["workloads"].toArray()) workloads << value.toObject()["name"].toString();
            QString text = profile["name"].toString();
            if (!(text + workloads.join(' ')).contains(filter, Qt::CaseInsensitive)) continue;
            if (profile["id"] == activeId) text += isCurrent(profile) ? "    Loaded" : "    Saved changes";
            auto item = new QListWidgetItem(text + "\n" + (workloads.isEmpty() ? "No workloads" : workloads.join(", ")), profiles);
            item->setData(Qt::UserRole, profile); item->setSizeHint(QSize(0, 82));
            if (!busy && isCurrent(profile)) item->setFlags(item->flags() & ~Qt::ItemIsEnabled);
        }
        unload->setEnabled(busy || !state["runtime"].toObject()["instances"].toArray().isEmpty());
        if (saved.isEmpty()) message("No profiles yet. Create one in the Xur web manager.");
        else if (!profiles->count()) message("No profiles match your search.");
        else if (busy) message("A profile change is in progress. Choose another profile to interrupt it.");
        for (int n = 0; n < profiles->count(); ++n) if (profiles->item(n)->flags() & Qt::ItemIsEnabled) { profiles->setCurrentRow(n); break; }
    }
    void refresh(bool restoreSelection = false) {
        const auto previousSearch = search->text();
        ++revision; message(""); plan = {}; controllerTarget = ControllerTarget::Profiles; pages->setCurrentIndex(0); current->setText("Loading profiles…"); profiles->clear(); search->clear();
        navigation->setText("D-pad / ↑ ↓: Move    Enter / A: Select    Esc / B: Close");
        request("/api/profiles", nullptr, [this, restoreSelection, previousSearch](Object data) { state = data; auto active = state["active"].toObject();
            if (restoreSelection) { const QSignalBlocker blocked(search); search->setText(previousSearch); }
            current->setText(active.isEmpty() ? "No complete profile loaded" : "Loaded: " + active["name"].toString()); render();
            if (restoreSelection && selected["unload"].toBool() && unload->isEnabled()) { focusControl(ControllerTarget::StopAll); return; }
            if (restoreSelection) for (int row = 0; row < profiles->count(); ++row)
                if (profiles->item(row)->data(Qt::UserRole).toJsonObject()["id"] == selected["id"] && profiles->item(row)->flags() & Qt::ItemIsEnabled) { profiles->setCurrentRow(row); break; }
            focusControl(profiles->currentRow() >= 0 ? ControllerTarget::Profiles : ControllerTarget::Retry); });
    }
    void choose(QListWidgetItem *item) {
        if (!item || !(item->flags() & Qt::ItemIsEnabled) || applying) return;
        chooseTarget(item->data(Qt::UserRole).toJsonObject());
    }
    void chooseTarget(Object target) {
        if (applying) return;
        selected = target; ++revision; message(""); plan = {}; apply->setEnabled(false);
        pages->setCurrentIndex(1); reviewTitle->setText(selected["name"].toString()); apply->setText(selected["unload"].toBool() ? "Stop all workloads" : "Load profile"); warning->hide();
        navigation->setText("D-pad / ← →: Choose action    Enter / A: Select    Esc / B: Back to profiles");
        while (auto child = changes->takeAt(0)) { delete child->widget(); delete child; }
        focusControl(ControllerTarget::Back);
        request("/api/profiles", nullptr, [this](Object data) {
            state = data;
            if (!changing(state)) { previewSelection(); return; }
            const auto operation = state["operation"].toObject()["id"].toString();
            if (operation.isEmpty()) { message("Refresh the picker before interrupting this change."); return; }
            message("Stopping the current profile change…"); Object body{{"id", operation}};
            request("/api/profiles/cancel", &body, [this, operation](Object) { waitForReview(operation); });
        });
    }
    void waitForReview(const QString &operation) {
        request("/api/profiles", nullptr, [this, operation](Object data) {
            state = data;
            if (!changing(state)) { message(""); previewSelection(); return; }
            const auto current = state["operation"].toObject();
            if (current["id"].toString() != operation) { message("Another profile change started. Go back and select again."); return; }
            message("Stopping the current profile change. Waiting for its running action to finish: " + current["currentAction"].toString());
            const int version = revision;
            QTimer::singleShot(500, this, [this, operation, version] { if (version == revision && isVisible() && pages->currentIndex() == 1) waitForReview(operation); });
        });
    }
    void previewSelection() {
        Object body;
        request(selected["unload"].toBool() ? "/api/profiles/unload/preview" : "/api/profiles/" + selected["id"].toString() + "/preview", &body, [this](Object data) {
            plan = data; QMap<QString, Object> definitions;
            for (auto p : state["profiles"].toArray()) for (auto w : p.toObject()["workloads"].toArray()) definitions[w.toObject()["id"].toString()] = w.toObject();
            for (auto w : state["active"].toObject()["workloads"].toArray()) definitions[w.toObject()["id"].toString()] = w.toObject();
            for (auto w : plan["target"].toObject()["workloads"].toArray()) definitions[w.toObject()["id"].toString()] = w.toObject();
            for (auto step : plan["steps"].toArray()) { auto s = step.toObject(); const auto kind = s["kind"].toString();
                if (kind != "Keep" && kind != "Stop" && kind != "Start") continue;
                auto w = definitions.value(s["workloadId"].toString());
                changes->addWidget(label((kind == "Keep" ? "Keep running" : kind) + "    " + w["name"].toString() + "\n" + w["recipe"].toObject()["name"].toString(), "change"));
                if (kind == "Stop" && w["recipe"].toObject()["kind"] == "Workstation") warning->show();
            }
            if (!changes->count()) changes->addWidget(label("No workloads need to start or stop."));
            apply->setEnabled(true); focusControl(ControllerTarget::Back);
        });
    }
    void approve() {
        if (plan.isEmpty() || applying) return;
        if (QDateTime::fromString(plan["expires"].toString(), Qt::ISODateWithMs) <= QDateTime::currentDateTimeUtc()) {
            message("This review expired. Go back and select the profile again."); apply->setEnabled(false); return;
        }
        applying = true; apply->setEnabled(false); back->setEnabled(false); apply->setText(plan["unload"].toBool() ? "Unloading…" : "Loading…");
        Object approval{{"id", plan["id"]}, {"digest", plan["digest"]}};
        request("/api/profiles/apply", &approval, [this](Object) { applying = false; back->setEnabled(true); apply->setText("Load profile"); refresh(); });
    }
    void goBack() {
        if (applying) return;
        if (pages->currentIndex() == 1) { refresh(true); }
        else reject();
    }
    void controls() {
        QDialog settings(this); settings.setWindowTitle("Profile switcher shortcuts"); auto layout = new QFormLayout(&settings);
        QKeySequenceEdit key(shortcut.property("shortcut").value<QKeySequence>());
        QComboBox chord; chord.addItems({"View + Menu", "LB + RB + Menu"}); chord.setCurrentIndex(input.shoulders ? 1 : 0);
        QSpinBox hold; hold.setRange(500, 3000); hold.setSingleStep(100); hold.setSuffix(" ms"); hold.setValue(input.hold);
        layout->addRow("Keyboard", &key); layout->addRow("Controller", &chord); layout->addRow("Hold duration", &hold);
        QDialogButtonBox buttons(QDialogButtonBox::Save | QDialogButtonBox::Cancel); layout->addRow(&buttons);
        layout->addRow(label("D-pad ↑ ↓: Move    ← →: Adjust    A: Activate    B: Cancel"));
        connect(&buttons, &QDialogButtonBox::accepted, &settings, &QDialog::accept); connect(&buttons, &QDialogButtonBox::rejected, &settings, &QDialog::reject);
        std::array<QWidget *, 5> fields{&key, &chord, &hold, buttons.button(QDialogButtonBox::Save), buttons.button(QDialogButtonBox::Cancel)};
        int selectedField = 1;
        auto focusField = [&] {
            for (int index = 0; index < int(fields.size()); ++index) {
                auto widget = fields[index]; widget->setProperty("controllerSelected", index == selectedField);
                widget->style()->unpolish(widget); widget->style()->polish(widget); widget->update();
            }
            fields[selectedField]->setFocus();
        };
        modalAction = [&](QString action) {
            if (action == "back") { settings.reject(); return; }
            if (action == "up" || action == "down") { selectedField = (selectedField + (action == "up" ? -1 : 1) + int(fields.size())) % fields.size(); focusField(); }
            else if (action == "left" || action == "right") {
                const int direction = action == "left" ? -1 : 1;
                if (selectedField == 1) chord.setCurrentIndex((chord.currentIndex() + direction + chord.count()) % chord.count());
                if (selectedField == 2) hold.setValue(hold.value() + direction * hold.singleStep());
            } else if (action == "accept" && selectedField >= 3) qobject_cast<QPushButton *>(fields[selectedField])->click();
        };
        QTimer::singleShot(0, &settings, focusField);
        const int result = settings.exec(); modalAction = {};
        highlightSelection();
        if (result != QDialog::Accepted) return;
        if (key.keySequence().isEmpty() || key.keySequence().count() != 1) { message("Choose a single keyboard combination."); return; }
        if (!KGlobalAccel::self()->setShortcut(&shortcut, {key.keySequence()}, KGlobalAccel::NoAutoloading)) { message("That keyboard shortcut is unavailable. Choose another."); return; }
        shortcut.setProperty("shortcut", QVariant::fromValue(key.keySequence()));
        input.shoulders = chord.currentIndex() == 1; input.hold = hold.value();
        preferences.setValue("controllerShoulders", input.shoulders); preferences.setValue("holdMilliseconds", input.hold);
    }
protected:
    void reject() override { if (applying) return; ++revision; input.capture(false); hide(); }
    void closeEvent(QCloseEvent *event) override { event->ignore(); reject(); }
    void keyPressEvent(QKeyEvent *event) override {
        if (event->key() == Qt::Key_Escape) { goBack(); return; }
        QDialog::keyPressEvent(event);
    }
public:
    explicit Switcher(uint serverUid = 0) : trustedServerUid(serverUid) {
        for (auto text : {current, error, reviewTitle, warning}) text->setTextFormat(Qt::PlainText);
        setWindowTitle("Switch profile · Xur"); setWindowFlag(Qt::WindowStaysOnTopHint); resize(640, 590); setMinimumSize(420, 420);
        auto layout = new QVBoxLayout(this); layout->setContentsMargins(24, 24, 24, 20); layout->setSpacing(14);
        auto heading = new QHBoxLayout; heading->addWidget(label("Switch profile", "title")); heading->addStretch();
        heading->addWidget(settings); heading->addWidget(close); layout->addLayout(heading); layout->addWidget(current);
        error->setObjectName("error"); error->setWordWrap(true); error->hide(); layout->addWidget(error);
        auto picker = new QWidget; auto pickLayout = new QVBoxLayout(picker); pickLayout->setContentsMargins(0, 0, 0, 0); search->setPlaceholderText("Search profiles"); search->setAccessibleName("Search profiles"); profiles->setAccessibleName("Saved profiles"); pickLayout->addWidget(search); pickLayout->addWidget(profiles);
        pickLayout->addWidget(unload); pickLayout->addWidget(label("Stops workstations and AI services. Saved profiles are kept.", "hint")); pickLayout->addWidget(retry); pages->addWidget(picker);
        auto review = new QWidget; auto reviewLayout = new QVBoxLayout(review); reviewLayout->setContentsMargins(0, 0, 0, 0); reviewTitle->setObjectName("reviewTitle"); reviewLayout->addWidget(reviewTitle); reviewLayout->addWidget(label("Review what stays running, stops and starts."));
        auto scroll = new QScrollArea; scroll->setWidgetResizable(true); auto rows = new QWidget; rows->setLayout(changes); scroll->setWidget(rows); reviewLayout->addWidget(scroll);
        warning->setText("Your current desktop may stop. Save your work before loading."); warning->setWordWrap(true); warning->setObjectName("warning"); reviewLayout->addWidget(warning);
        auto actions = new QHBoxLayout; actions->addStretch(); actions->addWidget(back); actions->addWidget(apply); apply->setObjectName("primary"); reviewLayout->addLayout(actions); pages->addWidget(review); layout->addWidget(pages, 1);
        navigation->setObjectName("hint"); navigation->setWordWrap(true); navigation->setTextFormat(Qt::PlainText); layout->addWidget(navigation);
        QFile connection(QDir::homePath() + "/.config/xur-profile-switcher/connection.json"); if (connection.open(QIODevice::ReadOnly)) {
            auto data = QJsonDocument::fromJson(connection.readAll()).object(); if (data["socket"].isString()) server = data["socket"].toString();
        }
        input.shoulders = preferences.value("controllerShoulders", false).toBool(); input.hold = qBound(500, preferences.value("holdMilliseconds", 1000).toInt(), 3000);
        input.open = [this] { showPicker("controller"); }; input.failure = [this](QString text) { message(text); };
        // Controller selection survives fullscreen apps retaining desktop focus;
        // ordinary Tab/mouse focus changes still select the visible action.
        connect(qApp, &QApplication::focusChanged, this, [this](QWidget *, QWidget *now) {
            if (now == apply) controllerTarget = ControllerTarget::Apply;
            else if (now == back) controllerTarget = ControllerTarget::Back;
            else if (now == retry) controllerTarget = ControllerTarget::Retry;
            else if (now == unload) controllerTarget = ControllerTarget::StopAll;
            else if (now == settings) controllerTarget = ControllerTarget::Shortcuts;
            else if (now == close) controllerTarget = ControllerTarget::Close;
            else if (now == profiles || now == search) controllerTarget = ControllerTarget::Profiles;
            else return;
            highlightSelection();
        });
        input.action = [this](QString action) {
            if (modalAction) { modalAction(action); return; }
            if (QApplication::activeModalWidget() && QApplication::activeModalWidget() != this) return;
            if (action == "back") { goBack(); return; }
            if (action == "up" || action == "down") { moveControl(action == "up" ? -1 : 1); return; }
            if (pages->currentIndex() == 1 && (action == "left" || action == "right")) {
                focusControl(action == "right" && apply->isEnabled() ? ControllerTarget::Apply : ControllerTarget::Back); return;
            }
            if (action != "accept") return;
            switch (controllerTarget) {
                case ControllerTarget::Profiles: choose(profiles->currentItem()); break;
                case ControllerTarget::StopAll: if (unload->isEnabled()) chooseTarget(Object{{"unload", true}, {"name", "Stop all workloads"}}); break;
                case ControllerTarget::Retry: refresh(); break;
                case ControllerTarget::Shortcuts: controls(); break;
                case ControllerTarget::Close: reject(); break;
                case ControllerTarget::Back: goBack(); break;
                case ControllerTarget::Apply: if (apply->isEnabled()) approve(); break;
            }
        };
        connect(search, &QLineEdit::textChanged, this, [this] { if (!state.isEmpty()) { message(""); render(); } });
        connect(profiles, &QListWidget::itemActivated, this, [this](QListWidgetItem *item) { choose(item); });
        connect(profiles, &QListWidget::itemClicked, this, [this](QListWidgetItem *item) { choose(item); });
        connect(close, &QPushButton::clicked, this, &Switcher::reject); connect(back, &QPushButton::clicked, this, [this] { goBack(); }); connect(retry, &QPushButton::clicked, this, [this] { refresh(); });
        connect(unload, &QPushButton::clicked, this, [this] { chooseTarget(Object{{"unload", true}, {"name", "Stop all workloads"}}); });
        connect(settings, &QPushButton::clicked, this, [this] { controls(); }); connect(apply, &QPushButton::clicked, this, [this] { approve(); });
        shortcut.setObjectName("open-profile-switcher"); shortcut.setText("Switch profile");
        shortcut.setProperty("componentName", "xur-profile-switcher"); shortcut.setProperty("componentDisplayName", "Xur profile switcher");
        KGlobalAccel::self()->setDefaultShortcut(&shortcut, {QKeySequence("Ctrl+Alt+P")});
        if (!KGlobalAccel::self()->setShortcut(&shortcut, {QKeySequence("Ctrl+Alt+P")})) qWarning("Xur profile switcher: global keyboard shortcut unavailable");
        auto keys = KGlobalAccel::self()->shortcut(&shortcut); shortcut.setProperty("shortcut", QVariant::fromValue(keys.isEmpty() ? QKeySequence("Ctrl+Alt+P") : keys.first()));
        connect(&shortcut, &QAction::triggered, this, [this] { showPicker("keyboard"); });
        input.scan();
    }
    void showPicker(const QString &source = "plasma-menu") {
        if (isVisible()) { raise(); activateWindow(); return; }
        show(); raise(); activateWindow(); input.capture(true);
        trigger = source; refresh();
    }
};

#ifndef XUR_SWITCHER_UI_TEST
int main(int argc, char **argv) {
    QApplication app(argc, argv); app.setQuitOnLastWindowClosed(false);
    app.setOrganizationName("Xur"); app.setApplicationName("xur-profile-switcher"); app.setDesktopFileName("dev.xur.ProfileSwitcher");
    const bool background = app.arguments().contains("--background");
    auto bus = QDBusConnection::sessionBus();
    if (!bus.registerService(service)) { if (!background) { QDBusMessage call = QDBusMessage::createMethodCall(service, "/Switcher", "dev.xur.ProfileSwitcher", "Show"); bus.call(call); } return 0; }
    QFile fontFile(QCoreApplication::applicationDirPath() + "/fonts/IBMPlexSans.ttf");
    if (fontFile.exists()) { const int id = QFontDatabase::addApplicationFont(fontFile.fileName()); auto families = QFontDatabase::applicationFontFamilies(id); if (!families.isEmpty()) app.setFont(QFont(families.first(), 12)); }
    app.setStyleSheet(SwitcherStyle());
    Switcher window;
    // The only exported session-bus action opens the local picker.
    class Bridge : public QDBusVirtualObject {
        Switcher &window;
    public:
        explicit Bridge(Switcher &w) : window(w) {}
        QString introspect(const QString &) const override { return "<interface name='dev.xur.ProfileSwitcher'><method name='Show'/></interface>"; }
        bool handleMessage(const QDBusMessage &message, const QDBusConnection &connection) override {
            if (message.interface() != "dev.xur.ProfileSwitcher" || message.member() != "Show" || !message.arguments().isEmpty()) return false;
            window.showPicker(); connection.send(message.createReply()); return true;
        }
    } bridge(window);
    bus.registerVirtualObject("/Switcher", &bridge);
    if (!background) window.showPicker();
    return app.exec();
}
#endif
