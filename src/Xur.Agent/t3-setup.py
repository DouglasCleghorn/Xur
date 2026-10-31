"""Xur's constrained setup helper, executed as node inside the T3 container.

stdin/stdout carry JSON, never CLI transcripts. Provider credentials remain in
their native stores in the persistent home. No provider token is returned to Xur.
"""
import codecs
import fcntl
import hashlib
import http.client
import json
import os
from pathlib import Path
import pty
import re
import secrets
import select
import shutil
import signal
import ssl
import subprocess
import sys
import termios
import time
import urllib.error
import urllib.parse
import urllib.request

PROVIDERS = {"codex", "claude", "opencode"}
LAUNCHER = "__XUR_T3_LAUNCHER__"
ANSI = re.compile(r"\x1b\[[0-?]*[ -/]*[@-~]|\x1b\][^\x07]*(?:\x07|\x1b\\)")


class SetupError(Exception):
    """A fixed, safe message that may be shown to the browser."""


def emit(state, message, **extra):
    print(json.dumps(dict(state=state, message=message, **extra)), flush=True)


def paths():
    home = Path.home()
    return home, home / ".local/bin", home / ".t3/userdata/settings.json"


def binary(provider):
    if provider not in PROVIDERS:
        raise SetupError("Choose Codex, Claude or OpenCode.")
    return str(paths()[1] / provider)


def atomic(path, content):
    path.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
    temporary = path.with_name(path.name + "." + secrets.token_hex(8) + ".tmp")
    try:
        fd = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
        with os.fdopen(fd, "w") as stream:
            stream.write(content)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def provider_entry(identifier, entry):
    settings = paths()[2]
    settings.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
    # Serialize our helpers; also detect concurrent edits made by T3 itself.
    with os.fdopen(os.open(settings.with_suffix(".xur-lock"), os.O_RDWR | os.O_CREAT, 0o600), "w") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        merge_provider(settings, identifier, entry)


def merge_provider(settings, identifier, entry):
    old = settings.read_text() if settings.exists() else "{}"
    data = json.loads(old)
    instances = data.setdefault("providerInstances", {})
    existing = instances.get(identifier)
    if existing and existing.get("driver") != entry["driver"]:
        raise SetupError("This provider entry was changed in T3. Remove that connection before reconnecting.")
    instances[identifier] = entry
    if (settings.read_text() if settings.exists() else "{}") != old:
        raise SetupError("T3 settings changed during setup. Retry the connection.")
    atomic(settings, json.dumps(data, indent=2) + "\n")


def codex_subscription_id(data):
    """Match native credential/runtime identity, never a display name or plan."""
    home = paths()[0]
    def resolved(value):
        return Path(str(value).replace("~/", str(home) + "/", 1)).resolve()
    def matches(entry):
        config = entry.get("config", {})
        if (entry.get("driver") != "codex" or entry.get("enabled") is False or
                config.get("enabled") is False or entry.get("environment") or
                config.get("setupMode") not in (None, "existing") or
                config.get("shadowHomePath", "").strip() or config.get("launchArgs", "").strip()):
            return False
        executable = config.get("binaryPath", "").strip() or "codex"
        if "/" not in executable:
            executable = shutil.which(executable)
        return (executable is not None and resolved(executable) == resolved(binary("codex")) and
                resolved(config.get("homePath", "").strip() or home / ".codex") == resolved(home / ".codex"))
    instances = data.get("providerInstances", {})
    # Prefer the normal T3 identity. Preserve any explicitly different account,
    # managed runtime, disabled entry, routing environment or launch arguments.
    if "codex" not in instances or matches(instances["codex"]):
        return "codex"
    for identifier, entry in instances.items():
        if matches(entry):
            return identifier
    raise SetupError("Codex in T3 uses another account or configuration. Sign in from that provider's T3 settings.")


def codex_subscription(register=False):
    settings = paths()[2]
    def select():
        old = settings.read_text() if settings.exists() else "{}"
        data = json.loads(old)
        identifier = codex_subscription_id(data)
        if register and identifier not in data.get("providerInstances", {}):
            data.setdefault("providerInstances", {})[identifier] = dict(driver="codex", enabled=True,
                config=dict(setupMode="existing", binaryPath=binary("codex"), homePath=str(paths()[0] / ".codex")))
            if (settings.read_text() if settings.exists() else "{}") != old:
                raise SetupError("T3 settings changed during setup. Retry the connection.")
            atomic(settings, json.dumps(data, indent=2) + "\n")
        return identifier
    if not register:
        return select()
    settings.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
    with os.fdopen(os.open(settings.with_suffix(".xur-lock"), os.O_RDWR | os.O_CREAT, 0o600), "w") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        return select()


def subscription_environment():
    env = dict(os.environ, BROWSER="/bin/true", TERM="dumb", NO_COLOR="1")
    # This login always targets the native subscription, never a router/API key.
    for key in list(env):
        if key.startswith(("ANTHROPIC_", "CLAUDE_CODE_USE_")) or key in ("CLAUDE_CODE_OAUTH_TOKEN", "OPENAI_API_KEY", "OPENAI_BASE_URL", "CODEX_HOME", "CLAUDE_CONFIG_DIR"):
            env.pop(key)
    return env


def account(provider):
    exe = binary(provider)
    if not Path(exe).is_file():
        return "Missing", "Automatic setup has not finished. Retry setup if it remains unavailable."
    if provider == "opencode":
        return "Installed", "OpenCode is installed. Connect an Xur model below."
    args = [exe, "login", "status"] if provider == "codex" else [exe, "auth", "status"]
    result = subprocess.run(args, capture_output=True, timeout=20, env=subscription_environment())
    if result.returncode:
        return "Signed out", "Sign in with your subscription."
    if provider == "claude":
        try:
            data = json.loads(result.stdout)
            if data.get("authMethod") != "claude.ai":
                return "Other credentials", "Claude is using another authentication method. Sign in with your Claude subscription."
        except (ValueError, TypeError):
            return "Unknown", "Could not verify Claude's account mode. Check Providers in T3."
    elif "chatgpt" not in (result.stdout + result.stderr).decode(errors="replace").lower():
        return "Other credentials", "Codex is using another authentication method. Sign in with ChatGPT."
    return "Signed in", "Subscription login saved in T3's persistent home."


def stop_process(process):
    if process.poll() is None:
        os.killpg(process.pid, signal.SIGTERM)
        try:
            process.wait(timeout=3)
        except subprocess.TimeoutExpired:
            os.killpg(process.pid, signal.SIGKILL)
            process.wait()


def install(provider):
    binary(provider)
    emit("Installing", "Installing the latest harness in T3's persistent home…")
    process = subprocess.Popen(["sh", "-c", LAUNCHER, "t3-launcher", "xur-install-harness", provider], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, start_new_session=True)
    try:
        deadline = time.monotonic() + 600
        while process.poll() is None and time.monotonic() < deadline:
            if select.select([sys.stdin.fileno()], [], [], 0.25)[0]:
                raise SetupError("Installation interrupted. Retry installation.")
        if process.poll() != 0 or not Path(binary(provider)).is_file():
            raise SetupError("Installation failed or timed out. Check internet access and free space, then retry.")
    finally:
        stop_process(process)
    emit("Succeeded", "Harness installed in persistent storage.")


def auth_interaction(provider, output):
    clean = ANSI.sub("", output).replace("\r", "")
    allowed = {"auth.openai.com"} if provider == "codex" else {"claude.com", "claude.ai", "platform.claude.com", "console.anthropic.com"}
    url = None
    for candidate in re.findall(r'https://[^\s<>"\x1b]+', clean):
        parsed = urllib.parse.urlsplit(candidate)
        if parsed.hostname in allowed and not parsed.username and not parsed.password and parsed.port in (None, 443):
            url = candidate
    code = re.search(r"\b[A-Z0-9]{4,5}-[A-Z0-9]{4,5}\b", clean) if provider == "codex" else None
    return dict(url=url, deviceCode=code.group() if code else None, needsCode=provider == "claude" and bool(url))


def callback_target(callback, authorization):
    """Validate against the CLI's pending OAuth attempt before making any request."""
    try:
        if not isinstance(callback, str) or not 0 < len(callback) <= 8192 or any(ord(c) <= 32 or ord(c) >= 127 for c in callback):
            raise ValueError()
        auth = urllib.parse.urlsplit(authorization)
        if auth.scheme != "https" or auth.hostname != "auth.openai.com" or auth.username or auth.password or auth.port not in (None, 443):
            raise ValueError()
        params = urllib.parse.parse_qs(auth.query, keep_blank_values=True, max_num_fields=32)
        if len(params.get("redirect_uri", [])) != 1 or len(params.get("state", [])) != 1 or not params["state"][0]:
            raise ValueError()
        expected = urllib.parse.urlsplit(params["redirect_uri"][0])
        actual = urllib.parse.urlsplit(callback)
        for url in (expected, actual):
            if url.scheme != "http" or url.hostname not in ("localhost", "127.0.0.1") or not url.port or url.username or url.password or url.fragment or url.path != "/auth/callback":
                raise ValueError()
        if expected.query or (actual.hostname, actual.port) != (expected.hostname, expected.port):
            raise ValueError()
        values = urllib.parse.parse_qs(actual.query, keep_blank_values=True, max_num_fields=32)
        if any(len(value) != 1 for value in values.values()) or not values.get("code", [""])[0] or "error" in values:
            raise ValueError()
        if values.get("state") != params["state"]:
            raise ValueError()
        return actual.port, "/auth/callback?" + actual.query
    except (ValueError, TypeError):
        raise SetupError("Paste the complete localhost URL from this sign-in attempt.") from None


def relay_callback(port, target):
    # Never execute the pasted text, resolve its hostname, use a proxy, or follow
    # arbitrary redirects. The only network destination is this container's CLI.
    def get(path):
        connection = http.client.HTTPConnection("127.0.0.1", port, timeout=30)
        try:
            connection.request("GET", path, headers={"Connection": "close"})
            response = connection.getresponse()
            return response.status, response.getheader("Location")
        finally:
            connection.close()
    try:
        status, location = get(target)
        if not 200 <= status < 400:
            raise SetupError("Codex could not complete this browser sign-in. Start again.")
        if location:
            success = urllib.parse.urlsplit(location)
            if (success.scheme == "http" and success.hostname in ("localhost", "127.0.0.1") and success.port == port
                    and success.path == "/success" and not success.username and not success.password and not success.fragment):
                # Older Codex releases exit only after /success. Its query may
                # contain credentials; discard it and never return it to Xur.
                status, _ = get("/success")
                if not 200 <= status < 300:
                    raise SetupError("Codex could not finish sign-in. Check its account status.")
            # Newer CLI versions exit after redirecting to a hosted success page.
            # Leave that redirect unfollowed; the CLI's exit/status verifies login.
    except (OSError, ValueError, http.client.HTTPException):
        raise SetupError("Could not reach the waiting Codex login. Check account status or start again.") from None


def authenticate(provider, browser=False):
    if provider not in ("codex", "claude"):
        raise SetupError("Subscription sign-in is available for Codex and Claude.")
    exe = binary(provider)
    if not Path(exe).is_file():
        raise SetupError("Install this harness first.")
    if browser and provider != "codex":
        raise SetupError("Browser callback sign-in is available for Codex only.")
    if provider == "codex":
        codex_subscription()  # Refuse incompatible setups before changing native credentials.
    args = [exe, "login"] + ([] if browser else ["--device-auth"]) if provider == "codex" else [exe, "auth", "login", "--claudeai"]
    master, slave = pty.openpty()
    attributes = termios.tcgetattr(slave)
    attributes[3] &= ~termios.ECHO
    termios.tcsetattr(slave, termios.TCSANOW, attributes)
    process = subprocess.Popen(args, stdin=slave, stdout=slave, stderr=slave, start_new_session=True, env=subscription_environment())
    os.close(slave)
    decoder = codecs.getincrementaldecoder("utf-8")("replace")
    output, last, pending, callback_sent = "", None, b"", False
    deadline = time.monotonic() + 900
    try:
        while process.poll() is None and time.monotonic() < deadline:
            ready, _, _ = select.select([master, sys.stdin.fileno()], [], [], 0.25)
            if sys.stdin.fileno() in ready:
                chunk = os.read(sys.stdin.fileno(), 4096)
                if not chunk:
                    raise SetupError("Sign-in interrupted. Start a new sign-in.")
                pending += chunk
                if len(pending) > 16384:
                    raise SetupError("Invalid sign-in response.")
                while b"\n" in pending:
                    line, pending = pending.split(b"\n", 1)
                    response = json.loads(line)
                    if response.get("action") == "cancel":
                        emit("Cancelled", "Sign-in cancelled.")
                        return
                    code = response.get("code", "")
                    if browser:
                        if callback_sent:
                            raise SetupError("This callback was already submitted. Check account status or start again.")
                        try:
                            if last is None:
                                raise SetupError("Wait for Codex's browser sign-in link.")
                            port, target = callback_target(code, last["url"])
                        except SetupError as error:
                            emit("Waiting", str(error), **(last or {}))
                            continue
                        callback_sent = True
                        relay_callback(port, target)
                        # A callback can be submitted only once. Keep its query
                        # out of subsequent events, errors and provider settings.
                        pending = b""
                        break
                    if provider != "claude" or not re.fullmatch(r"[A-Za-z0-9_#.-]{1,2048}", code):
                        raise SetupError("Paste the one-time code from Claude's sign-in page.")
                    os.write(master, code.encode() + b"\n")
            if master in ready:
                try:
                    chunk = os.read(master, 8192)
                except OSError:
                    break
                output = (output + decoder.decode(chunk))[-32768:]
                interaction = auth_interaction(provider, output)
                if browser:
                    interaction.update(deviceCode=None, needsCallback=bool(interaction["url"]))
                if interaction != last and interaction["url"]:
                    emit("Waiting", "Finish signing in on the provider's site.", **interaction)
                    last = interaction
        try:
            result = process.wait(timeout=3)
        except subprocess.TimeoutExpired:
            raise SetupError("Sign-in expired. Start again.")
        if result:
            raise SetupError("Sign-in did not complete. Retry, or use the provider's login in T3's terminal.")
        state, message = account(provider)
        if state != "Signed in":
            raise SetupError(message)
        if provider == "codex":
            identifier = codex_subscription(register=True)
        else:
            identifier = "xur_claude_subscription"
            config = dict(binaryPath=exe, homePath=str(paths()[0] / ".claude"))
            provider_entry(identifier, dict(driver="claudeAgent", displayName="Claude subscription", enabled=True, config=config))
        emit("Succeeded", message, providerInstanceId=identifier)
    finally:
        stop_process(process)
        os.close(master)


def request(model, path, body=None):
    context = ssl.create_default_context()
    if model.get("certificate"):
        context.load_verify_locations(cadata=model["certificate"])
    headers = {"Authorization": "Bearer " + model["apiKey"], "Content-Type": "application/json"}
    # No redirects: never forward the credential to a different origin.
    class NoRedirect(urllib.request.HTTPRedirectHandler):
        def redirect_request(self, req, fp, code, msg, hdrs, newurl):
            return None
    opener = urllib.request.build_opener(urllib.request.HTTPSHandler(context=context), NoRedirect())
    req = urllib.request.Request(model["baseUrl"] + path, headers=headers, data=json.dumps(body).encode() if body is not None else None)
    return opener.open(req, timeout=90)


def events(response):
    if response.headers.get_content_type() != "text/event-stream":
        raise SetupError("The endpoint did not return an event stream.")
    total = 0
    deadline = time.monotonic() + 120
    for raw in response:
        total += len(raw)
        if total > 1024 * 1024 or time.monotonic() > deadline:
            raise SetupError("The model compatibility check exceeded its limit.")
        if raw.startswith(b"data:") and raw[5:].strip() != b"[DONE]":
            yield json.loads(raw[5:])


def probe(model, provider):
    nonce = secrets.token_hex(6)
    prompt = "Call xur_check with value '" + nonce + "'. After the tool result, repeat its value."
    function = dict(name="xur_check", description="Return the supplied value.", parameters={"type": "object", "properties": {"value": {"type": "string"}}, "required": ["value"], "additionalProperties": False})
    if provider == "opencode":
        messages = [{"role": "user", "content": prompt}]
        body = dict(model=model["modelId"], messages=messages, tools=[dict(type="function", function=function)], tool_choice={"type": "function", "function": {"name": "xur_check"}}, stream=True, max_tokens=512)
        call_id, name, arguments, finished = "", "", "", False
        with request(model, "/chat/completions", body) as response:
            for event in events(response):
                for choice in event.get("choices", []):
                    finished = finished or choice.get("finish_reason") == "tool_calls"
                    for call in choice.get("delta", {}).get("tool_calls", []):
                        if call.get("index", 0) != 0:
                            raise SetupError("The model returned unexpected parallel tool calls.")
                        call_id += call.get("id", "")
                        name += call.get("function", {}).get("name", "")
                        arguments += call.get("function", {}).get("arguments", "")
        if not finished or not call_id or name != "xur_check" or json.loads(arguments).get("value") != nonce:
            raise SetupError("The model did not complete the tool-call check. Check its serving tool parser and chat template.")
        messages.extend([dict(role="assistant", content=None, tool_calls=[dict(id=call_id, type="function", function=dict(name=name, arguments=arguments))]), dict(role="tool", tool_call_id=call_id, content=nonce)])
        body.pop("tool_choice")
        text, finished = "", False
        with request(model, "/chat/completions", body) as response:
            for event in events(response):
                for choice in event.get("choices", []):
                    finished = finished or choice.get("finish_reason") == "stop"
                    text += choice.get("delta", {}).get("content") or ""
    else:
        body = dict(model=model["modelId"], input=[dict(role="user", content=prompt)], tools=[dict(type="function", **function)], tool_choice=dict(type="function", name="xur_check"), stream=True, store=False)
        call, finished = None, False
        with request(model, "/responses", body) as response:
            for event in events(response):
                finished = finished or event.get("type") == "response.completed"
                if event.get("type") == "response.output_item.done" and event.get("item", {}).get("type") == "function_call":
                    call = event["item"]
        if not finished or not call or not call.get("call_id") or call.get("name") != "xur_check" or json.loads(call.get("arguments", "{}")).get("value") != nonce:
            raise SetupError("Codex requires streaming Responses API tool calls. This endpoint did not pass that check.")
        body["input"].extend([call, dict(type="function_call_output", call_id=call["call_id"], output=nonce)])
        body.pop("tool_choice")
        text, finished = "", False
        with request(model, "/responses", body) as response:
            for event in events(response):
                finished = finished or event.get("type") == "response.completed"
                if event.get("type") == "response.output_text.delta":
                    text += event.get("delta", "")
    if not finished or nonce not in text:
        raise SetupError("The model did not use the tool result. The connection was not enabled.")


def connection_directory(model, provider):
    if provider not in ("codex", "opencode") or not re.fullmatch(r"xur_[a-f0-9]{24}", model["id"]):
        raise SetupError("Invalid model connection.")
    return paths()[0] / ".config/xur-t3" / model["id"]


def connection_metadata(model):
    return {key: value for key, value in model.items() if key != "apiKey"}


def connection_status(model, provider):
    directory = connection_directory(model, provider)
    try:
        manifest = json.loads((directory / "connection.json").read_text())
        entry = json.loads(paths()[2].read_text())["providerInstances"][model["id"]]
        return (Path(binary(provider)).is_file() and manifest["model"] == connection_metadata(model) and entry == manifest["entry"]
                and all(hashlib.sha256((directory / name).read_bytes()).hexdigest() == digest for name, digest in manifest["files"].items()))
    except (OSError, ValueError, KeyError, TypeError):
        return False


def configure(model, provider):
    directory = connection_directory(model, provider)
    directory.mkdir(mode=0o700, parents=True, exist_ok=True)
    cert = directory / "xur.crt"
    atomic(cert, model["certificate"])
    bundle = directory / "ca-bundle.crt"
    roots = ssl.get_default_verify_paths().cafile
    atomic(bundle, (Path(roots).read_text() if roots else "") + "\n" + model["certificate"])
    key = directory / "api-key"
    atomic(key, model["apiKey"])
    environment = [{"name": "NODE_EXTRA_CA_CERTS", "value": str(cert)}, {"name": "SSL_CERT_FILE", "value": str(bundle)}]
    custom = dict(slug=model["modelId"], name=model["name"], capabilities=dict(optionDescriptors=[]))
    config = dict(binaryPath=binary(provider), customModels=[custom])
    if provider == "opencode":
        native = directory / "opencode.json"
        atomic(native, json.dumps({"providers": {model["id"]: {"name": "Xur", "package": "@opencode/ai/providers/openai-compatible", "settings": {"baseURL": model["baseUrl"], "apiKey": "{file:" + str(key) + "}"}, "models": {model["modelId"]: {"name": model["name"], "capabilities": {"tools": True, "input": ["text"], "output": ["text"]}, "limit": {"context": model["contextTokens"], "output": min(8192, model["contextTokens"] // 4)}}}}}}))
        environment.append({"name": "OPENCODE_CONFIG_DIR", "value": str(directory)})
        custom["slug"] = model["id"] + "/" + model["modelId"]
    else:
        native = directory / "codex"
        # JSON string quoting is also valid for TOML basic string values here.
        atomic(native / "config.toml", "model_provider = \"xur\"\nmodel = " + json.dumps(model["modelId"], ensure_ascii=False) + "\nmodel_context_window = " + str(model["contextTokens"]) + "\n[model_providers.xur]\nname = \"Xur\"\nbase_url = " + json.dumps(model["baseUrl"]) + "\nwire_api = \"responses\"\nexperimental_bearer_token = " + json.dumps(model["apiKey"]) + "\nrequires_openai_auth = false\n")
        config.update(setupMode="existing", homePath=str(native))
        environment.append({"name": "CODEX_CA_CERTIFICATE", "value": str(cert)})
    entry = dict(driver=provider, displayName=model["name"] + " · Xur", enabled=True, environment=environment, config=config)
    provider_entry(model["id"], entry)
    native_file = native if provider == "opencode" else native / "config.toml"
    hashes = {str(file.relative_to(directory)): hashlib.sha256(file.read_bytes()).hexdigest() for file in (key, cert, bundle, native_file)}
    atomic(directory / "connection.json", json.dumps(dict(model=connection_metadata(model), entry=entry, files=hashes)))


def main():
    payload = json.loads(sys.stdin.buffer.readline(65537))
    action, provider = payload["action"], payload.get("provider", "")
    if action == "status":
        state, message = account(provider)
        identifier = None
        if provider == "codex":
            try:
                identifier = codex_subscription()
            except SetupError as error:
                message += " " + str(error)
        emit(state, message, providerInstanceId=identifier)
    elif action == "install":
        install(provider)
    elif action in ("login", "login-browser"):
        authenticate(provider, browser=action == "login-browser")
    elif action == "connection-status":
        configured = connection_status(payload["model"], provider)
        emit("Configured" if configured else "Missing", "Connection saved." if configured else "Reconnect this model to repair its configuration.")
    elif action == "connect":
        if not Path(binary(provider)).is_file():
            raise SetupError("Install the selected harness first.")
        # Finish before the agent/controller deadline; a timed-out probe must
        # never later install a credential that the controller has revoked.
        def expired(*_):
            raise SetupError("The model compatibility check timed out. Check the workload and retry.")
        signal.signal(signal.SIGALRM, expired)
        signal.alarm(240)
        try:
            emit("Checking", "Checking streaming and a tool-call round trip from T3…")
            probe(payload["model"], provider)
            if select.select([sys.stdin.fileno()], [], [], 0)[0]:
                raise SetupError("Model setup interrupted. Reconnect the model.")
            configure(payload["model"], provider)
        finally:
            signal.alarm(0)
        emit("Succeeded", "Model connected. Refresh provider status in T3 to load the model.")
    else:
        raise SetupError("Unsupported setup action.")


if __name__ == "__main__":
    try:
        main()
    except SetupError as error:
        # Only our own fixed validation messages may reach the browser.
        emit("Failed", str(error))
    except Exception:
        emit("Failed", "Setup could not complete. Check the workload, server address, certificate and network access, then retry.")
