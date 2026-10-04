#include "input.h"
#include <QtWidgets>
#include <QtNetwork>
#include <QtDBus>
#include <KGlobalAccel>
#include <libevdev/libevdev.h>
#include <libudev.h>
#include <fcntl.h>
#include <sys/ioctl.h>
#include <sys/stat.h>
#include <unistd.h>
#include <functional>
#include <memory>

using Object = QJsonObject;
static const char *service = "dev.xur.ProfileSwitcher";

class Inputs {
    struct Pad {
        QString path;
        int fd;
        libevdev *device;
        PadState state;
        int hat = 0;
        bool grabbed = false;
        ~Pad() { libevdev_free(device); ::close(fd); }
    };
    std::vector<std::unique_ptr<Pad>> pads;
    QTimer scanTimer, readTimer;
    udev *context = udev_new();
    bool visible = false;
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
                QString command;
                if (event.type == EV_KEY && event.code <= KEY_MAX && event.value != 2) {
                    const bool edge = event.value == 1 && !pad.state.keys[event.code];
                    pad.state.keys[event.code] = event.value == 1;
                    if (edge) {
                        if (event.code == BTN_SOUTH) command = "accept";
                        if (event.code == BTN_EAST) command = "back";
                        if (event.code == BTN_DPAD_UP) command = "up";
                        if (event.code == BTN_DPAD_DOWN) command = "down";
                    }
                }
                if (event.type == EV_ABS && event.code == ABS_HAT0Y) {
                    if (event.value != pad.hat && event.value != 0) command = event.value < 0 ? "up" : "down";
                    pad.hat = event.value;
                }
                if (!syncing && visible && pad.grabbed && pad.state.armed && !command.isEmpty() && action) action(command);
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
    QNetworkAccessManager network;
    QSettings preferences;
    Inputs input;
    QAction shortcut{this};
    QStackedWidget *pages = new QStackedWidget;
    QLabel *current = new QLabel, *error = new QLabel;
    QLineEdit *search = new QLineEdit, *username = new QLineEdit, *password = new QLineEdit;
    QListWidget *profiles = new QListWidget;
    QVBoxLayout *changes = new QVBoxLayout;
    QPushButton *apply = new QPushButton("Load profile"), *back = new QPushButton("Back"), *retry = new QPushButton("Try again");
    QLabel *reviewTitle = new QLabel, *warning = new QLabel;
    QUrl server;
    QSslCertificate certificate;
    QByteArray token;
    Object state, plan, selected;
    bool applying = false;
    int revision = 0;
    static QLabel *label(const QString &text, const char *name = nullptr) {
        auto result = new QLabel(text); result->setTextFormat(Qt::PlainText); result->setWordWrap(true);
        if (name) result->setObjectName(name);
        return result;
    }
    void message(const QString &text) { error->setText(text); error->setVisible(!text.isEmpty()); }
    void request(const QString &path, const Object *body, std::function<void(Object)> completed) {
        if (server.scheme() != "https" || server.host() != "localhost" || certificate.isNull()) {
            message("The local manager connection is unavailable. Reload this workstation after updating Xur."); return;
        }
        QNetworkRequest request(server.resolved(QUrl(path)));
        auto tls = QSslConfiguration::defaultConfiguration(); tls.setCaCertificates({certificate}); request.setSslConfiguration(tls);
        request.setTransferTimeout(20000);
        request.setAttribute(QNetworkRequest::RedirectPolicyAttribute, QNetworkRequest::ManualRedirectPolicy);
        request.setRawHeader("Content-Type", "application/json");
        if (!token.isEmpty()) request.setRawHeader("Authorization", "Bearer " + token);
        auto reply = body ? network.post(request, QJsonDocument(*body).toJson(QJsonDocument::Compact)) : network.get(request);
        const int version = revision;
        connect(reply, &QNetworkReply::sslErrors, this, [this, reply](const QList<QSslError> &errors) {
            // Trust only the exact public certificate provisioned by the agent.
            if (reply->sslConfiguration().peerCertificate() != certificate) return;
            for (const auto &error : errors) if (error.error() != QSslError::SelfSignedCertificate && error.error() != QSslError::CertificateUntrusted) return;
            reply->ignoreSslErrors(errors);
        });
        connect(reply, &QNetworkReply::finished, this, [this, reply, completed, version] {
            const auto data = QJsonDocument::fromJson(reply->readAll()).object();
            const int status = reply->attribute(QNetworkRequest::HttpStatusCodeAttribute).toInt();
            if (version == revision && isVisible()) {
                if (status == 401 && !token.isEmpty()) { applying = false; back->setEnabled(true); apply->setText("Load profile"); plan = {}; token.clear(); pages->setCurrentIndex(0); message("Your session expired. Sign in again."); username->setFocus(); }
                else if (reply->error() != QNetworkReply::NoError || status < 200 || status >= 300) {
                    if (applying) { applying = false; back->setEnabled(true); apply->setText("Load profile"); plan = {}; }
                    message(data["error"].toString("Could not contact the local manager. Try again.")); apply->setEnabled(false);
                } else completed(data);
            }
            reply->deleteLater();
        });
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
    void render() {
        profiles->clear(); const QString filter = search->text();
        auto operation = state["operation"].toObject()["stage"].toString();
        const bool busy = operation == "Applying" || operation == "Cancelling" || operation == "Failed";
        auto saved = state["profiles"].toArray();
        std::vector<Object> sorted; for (auto value : saved) sorted.push_back(value.toObject());
        const auto activeId = state["active"].toObject()["id"];
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
            if (busy || isCurrent(profile)) item->setFlags(item->flags() & ~Qt::ItemIsEnabled);
        }
        if (!profiles->count()) message(saved.isEmpty() ? "No profiles yet. Create one in the Xur web manager." : "No profiles match your search.");
        else if (busy) message(operation == "Failed" ? "A profile change needs attention. Resume or cancel it in the Xur web manager." : "A profile change is in progress. Wait for it to finish.");
        for (int n = 0; n < profiles->count(); ++n) if (profiles->item(n)->flags() & Qt::ItemIsEnabled) { profiles->setCurrentRow(n); break; }
    }
    void refresh() {
        ++revision; message(""); plan = {}; pages->setCurrentIndex(1); current->setText("Loading profiles…"); profiles->clear(); search->clear();
        request("/api/profiles", nullptr, [this](Object data) { state = data; auto active = state["active"].toObject();
            current->setText(active.isEmpty() ? "No complete profile loaded" : "Loaded: " + active["name"].toString()); render(); profiles->setFocus(); });
    }
    void choose(QListWidgetItem *item) {
        if (!item || !(item->flags() & Qt::ItemIsEnabled) || applying) return;
        selected = item->data(Qt::UserRole).toJsonObject(); ++revision; message(""); plan = {}; apply->setEnabled(false);
        pages->setCurrentIndex(2); reviewTitle->setText(selected["name"].toString()); warning->hide();
        while (auto child = changes->takeAt(0)) { delete child->widget(); delete child; }
        back->setFocus(); Object body;
        request("/api/profiles/" + selected["id"].toString() + "/preview", &body, [this](Object data) {
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
            apply->setEnabled(true);
        });
    }
    void approve() {
        if (plan.isEmpty() || applying) return;
        if (QDateTime::fromString(plan["expires"].toString(), Qt::ISODateWithMs) <= QDateTime::currentDateTimeUtc()) {
            message("This review expired. Go back and select the profile again."); apply->setEnabled(false); return;
        }
        applying = true; apply->setEnabled(false); back->setEnabled(false); apply->setText("Loading…");
        Object approval{{"id", plan["id"]}, {"digest", plan["digest"]}};
        request("/api/profiles/apply", &approval, [this](Object) { applying = false; back->setEnabled(true); apply->setText("Load profile"); refresh(); });
    }
    void goBack() {
        if (applying) return;
        if (pages->currentIndex() == 2) { ++revision; plan = {}; pages->setCurrentIndex(1); message(""); profiles->setFocus(); }
        else reject();
    }
    void controls() {
        QDialog settings(this); settings.setWindowTitle("Profile switcher shortcuts"); auto layout = new QFormLayout(&settings);
        QKeySequenceEdit key(shortcut.property("shortcut").value<QKeySequence>());
        QComboBox chord; chord.addItems({"View + Menu", "LB + RB + Menu"}); chord.setCurrentIndex(input.shoulders ? 1 : 0);
        QSpinBox hold; hold.setRange(500, 3000); hold.setSingleStep(100); hold.setSuffix(" ms"); hold.setValue(input.hold);
        layout->addRow("Keyboard", &key); layout->addRow("Controller", &chord); layout->addRow("Hold duration", &hold);
        QDialogButtonBox buttons(QDialogButtonBox::Save | QDialogButtonBox::Cancel); layout->addRow(&buttons);
        connect(&buttons, &QDialogButtonBox::accepted, &settings, &QDialog::accept); connect(&buttons, &QDialogButtonBox::rejected, &settings, &QDialog::reject);
        if (settings.exec() != QDialog::Accepted) return;
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
    Switcher() {
        for (auto text : {current, error, reviewTitle, warning}) text->setTextFormat(Qt::PlainText);
        setWindowTitle("Switch profile · Xur"); setWindowFlag(Qt::WindowStaysOnTopHint); resize(640, 590); setMinimumSize(420, 420);
        auto layout = new QVBoxLayout(this); layout->setContentsMargins(24, 24, 24, 20); layout->setSpacing(14);
        auto heading = new QHBoxLayout; heading->addWidget(label("Switch profile", "title")); heading->addStretch();
        auto settings = new QPushButton("Shortcuts"); auto close = new QPushButton("Close"); heading->addWidget(settings); heading->addWidget(close); layout->addLayout(heading); layout->addWidget(current);
        error->setObjectName("error"); error->setWordWrap(true); error->hide(); layout->addWidget(error);
        auto login = new QWidget; auto form = new QFormLayout(login); form->setContentsMargins(0, 0, 0, 0);
        form->addRow(label("Sign in with your Xur administrator account. Your password is not saved.")); form->addRow("Username or email", username); password->setEchoMode(QLineEdit::Password); form->addRow("Password", password);
        auto signIn = new QPushButton("Sign in"); form->addRow(signIn); pages->addWidget(login);
        auto picker = new QWidget; auto pickLayout = new QVBoxLayout(picker); pickLayout->setContentsMargins(0, 0, 0, 0); search->setPlaceholderText("Search profiles"); search->setAccessibleName("Search profiles"); profiles->setAccessibleName("Saved profiles"); pickLayout->addWidget(search); pickLayout->addWidget(profiles); pickLayout->addWidget(retry); pages->addWidget(picker);
        auto review = new QWidget; auto reviewLayout = new QVBoxLayout(review); reviewLayout->setContentsMargins(0, 0, 0, 0); reviewTitle->setObjectName("reviewTitle"); reviewLayout->addWidget(reviewTitle); reviewLayout->addWidget(label("Review what stays running, stops and starts."));
        auto scroll = new QScrollArea; scroll->setWidgetResizable(true); auto rows = new QWidget; rows->setLayout(changes); scroll->setWidget(rows); reviewLayout->addWidget(scroll);
        warning->setText("Your current desktop may stop. Save your work before loading."); warning->setWordWrap(true); warning->setObjectName("warning"); reviewLayout->addWidget(warning);
        auto actions = new QHBoxLayout; actions->addStretch(); actions->addWidget(back); actions->addWidget(apply); apply->setObjectName("primary"); reviewLayout->addLayout(actions); pages->addWidget(review); layout->addWidget(pages, 1);
        layout->addWidget(label("↑ ↓ / D-pad: Move     Enter / A: Select     Esc / B: Back", "hint"));
        QFile connection(QDir::homePath() + "/.config/xur-profile-switcher/connection.json"); if (connection.open(QIODevice::ReadOnly)) {
            auto data = QJsonDocument::fromJson(connection.readAll()).object(); server = QUrl(data["url"].toString()); certificate = QSslCertificate(data["certificate"].toString().toUtf8());
        }
        input.shoulders = preferences.value("controllerShoulders", false).toBool(); input.hold = qBound(500, preferences.value("holdMilliseconds", 1000).toInt(), 3000);
        input.open = [this] { showPicker(); }; input.failure = [this](QString text) { message(text); };
        input.action = [this](QString action) {
            if (QApplication::activeModalWidget() && QApplication::activeModalWidget() != this) return;
            if (action == "back") { goBack(); return; }
            if (pages->currentIndex() == 1) {
                if (action == "accept") choose(profiles->currentItem());
                else if (action == "up" || action == "down") {
                    const int count = profiles->count(); if (!count) return; int index = profiles->currentRow();
                    for (int n = 0; n < count; ++n) { index = (index + (action == "up" ? -1 : 1) + count) % count; if (profiles->item(index)->flags() & Qt::ItemIsEnabled) { profiles->setCurrentRow(index); break; } }
                }
            } else if (pages->currentIndex() == 2) {
                if (action == "accept") { if (apply->hasFocus() && apply->isEnabled()) approve(); else if (back->hasFocus()) goBack(); }
                else if (action == "up" || action == "down") { if (apply->hasFocus() || !apply->isEnabled()) back->setFocus(); else apply->setFocus(); }
            }
        };
        connect(search, &QLineEdit::textChanged, this, [this] { if (!state.isEmpty()) { message(""); render(); } });
        connect(profiles, &QListWidget::itemActivated, this, [this](QListWidgetItem *item) { choose(item); });
        connect(profiles, &QListWidget::itemClicked, this, [this](QListWidgetItem *item) { choose(item); });
        connect(close, &QPushButton::clicked, this, &Switcher::reject); connect(back, &QPushButton::clicked, this, [this] { goBack(); }); connect(retry, &QPushButton::clicked, this, [this] { refresh(); });
        connect(settings, &QPushButton::clicked, this, [this] { controls(); }); connect(apply, &QPushButton::clicked, this, [this] { approve(); });
        auto authenticate = [this, signIn] { if (!signIn->isEnabled()) return; Object account{{"username", username->text()}, {"password", password->text()}}; password->clear(); message("");
            request("/api/auth/login", &account, [this](Object data) { token = data["accessToken"].toString().toUtf8(); refresh(); }); };
        connect(signIn, &QPushButton::clicked, this, authenticate); connect(password, &QLineEdit::returnPressed, this, authenticate);
        shortcut.setObjectName("open-profile-switcher"); shortcut.setText("Switch profile");
        shortcut.setProperty("componentName", "xur-profile-switcher"); shortcut.setProperty("componentDisplayName", "Xur profile switcher");
        KGlobalAccel::self()->setDefaultShortcut(&shortcut, {QKeySequence("Ctrl+Alt+P")});
        if (!KGlobalAccel::self()->setShortcut(&shortcut, {QKeySequence("Ctrl+Alt+P")})) qWarning("Xur profile switcher: global keyboard shortcut unavailable");
        auto keys = KGlobalAccel::self()->shortcut(&shortcut); shortcut.setProperty("shortcut", QVariant::fromValue(keys.isEmpty() ? QKeySequence("Ctrl+Alt+P") : keys.first()));
        connect(&shortcut, &QAction::triggered, this, [this] { showPicker(); });
        input.scan();
    }
    void showPicker() {
        if (isVisible()) { raise(); activateWindow(); return; }
        show(); raise(); activateWindow(); input.capture(true);
        if (token.isEmpty()) { pages->setCurrentIndex(0); current->setText("Sign in to switch profiles"); username->setFocus(); }
        else refresh();
    }
};

int main(int argc, char **argv) {
    QApplication app(argc, argv); app.setQuitOnLastWindowClosed(false);
    app.setOrganizationName("Xur"); app.setApplicationName("xur-profile-switcher"); app.setDesktopFileName("dev.xur.ProfileSwitcher");
    const bool background = app.arguments().contains("--background");
    auto bus = QDBusConnection::sessionBus();
    if (!bus.registerService(service)) { if (!background) { QDBusMessage call = QDBusMessage::createMethodCall(service, "/Switcher", "dev.xur.ProfileSwitcher", "Show"); bus.call(call); } return 0; }
    QFile fontFile(QCoreApplication::applicationDirPath() + "/fonts/IBMPlexSans.ttf");
    if (fontFile.exists()) { const int id = QFontDatabase::addApplicationFont(fontFile.fileName()); auto families = QFontDatabase::applicationFontFamilies(id); if (!families.isEmpty()) app.setFont(QFont(families.first(), 12)); }
    app.setStyleSheet("QWidget{background:#20252d;color:#edf1f7;font-size:15px;} QLabel#title{font-size:26px;font-weight:600;} QLabel#reviewTitle{font-size:22px;} QLabel#hint{color:#b0bac8;font-size:13px;} QLabel#error{color:#e7a6a1;border:1px solid #7c494b;padding:12px;} QLabel#warning{color:#e6c387;background:#393022;padding:12px;} QLabel#change{border-bottom:1px solid #373f4b;padding:12px;} QPushButton,QLineEdit{min-height:36px;padding:4px 12px;border:1px solid #373f4b;border-radius:5px;} QPushButton:focus,QLineEdit:focus{border:2px solid #99c5ff;} QPushButton#primary{background:#99c5ff;color:#17283d;} QPushButton:disabled{color:#8190a3;} QLineEdit,QListWidget{background:#15181d;} QListWidget{border:0;} QListWidget::item{padding:12px;border:1px solid #373f4b;border-radius:6px;margin-bottom:8px;font-size:19px;} QListWidget::item:selected{background:#293649;border:2px solid #99c5ff;} QScrollArea{border:0;} ");
    Switcher window;
    // The only exported session-bus action opens an authenticated UI.
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
