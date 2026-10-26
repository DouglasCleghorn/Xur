"""Serve the rendered Razor page with synthetic setup states for browser checks.

Render first with Xur.Unit.Tests --control-panel-render .build/evidence/t3-ui.
No credentials, provider logins or model requests leave this loopback fixture.
"""
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import mimetypes
from pathlib import Path
from urllib.parse import urlsplit

ROOT = Path(__file__).resolve().parents[2]
state = {"installed": True, "enabled": True, "installStage": None, "running": True, "modelReady": True, "failConnect": False, "calls": [], "accounts": {}, "flows": {}, "storage": dict(allDrives=True, models=True, folders=[])}


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass

    def send(self, data, code=200):
        body = json.dumps(data).encode()
        self.send_response(code);self.send_header("Content-Type", "application/json");self.send_header("Cache-Control", "no-store");self.send_header("Content-Length", str(len(body)));self.end_headers();self.wfile.write(body)

    def do_GET(self):
        path = urlsplit(self.path).path
        if path == "/fixture":
            return self.send(state)
        if path == "/api/updates":
            return self.send(dict(automatic=False))
        if path == "/api/container-jobs":
            return self.send([dict(id="fixture-install", stage=state["installStage"])] if state["installStage"] else [])
        if path == "/api/t3-code/storage":
            return self.send(state["storage"])
        if path == "/api/t3-code/status":
            return self.send(dict(state="Running" if state["running"] else "Starting" if state["enabled"] else "Disabled", workloadId="t3", installed=state["installed"], enabled=state["enabled"]))
        if path == "/api/t3-code/models":
            return self.send(dict(models=[dict(workloadId="12", name="DeepSeek 1017 <test>", engine="vLLM", state="Ready" if state["modelReady"] else "Profile not loaded", profiles=["Coding"], modelIds=["deepseek-1017"] if state["modelReady"] else [], contextLimits={"deepseek-1017": 32768})], connections=[]))
        if path == "/t3-code" or path == "/":
            file = ROOT / ".build/evidence/t3-ui" / ("t3-code.html" if state["installed"] else "t3-install.html")
        elif path == "/endpoints":
            file = ROOT / ".build/evidence/t3-ui/endpoints.html"
        else:
            file = (ROOT / "src/Xur.Control/wwwroot" / path.lstrip("/")).resolve()
            if not file.is_relative_to(ROOT / "src/Xur.Control/wwwroot"):
                return self.send({}, 404)
        if not file.is_file():
            return self.send({}, 404)
        data = file.read_bytes();self.send_response(200);self.send_header("Content-Type", mimetypes.guess_type(str(file))[0] or "application/octet-stream");self.send_header("Content-Length", str(len(data)));self.end_headers();self.wfile.write(data)

    def do_POST(self):
        path = urlsplit(self.path).path
        data = json.loads(self.rfile.read(int(self.headers.get("Content-Length", 0))) or b"{}")
        if path == "/api/t3-code/storage":
            if any(not p.startswith("/") or ".." in p.split("/") for p in data.get("folders", [])):
                return self.send(dict(error="Use absolute folder paths."), 409)
            state["storage"] = data
            return self.send(data)
        if path == "/fixture":
            for key in ("running", "modelReady", "failConnect", "installed", "installStage", "enabled"):
                if key in data:
                    state[key] = data[key]
            return self.send(state)
        if self.headers.get("RequestVerificationToken") != "fixture-only":
            return self.send(dict(error="Missing antiforgery header"), 400)
        state["calls"].append(dict(path=path, action=data.get("action"), provider=data.get("provider"), codeSubmitted=bool(data.get("code"))))
        if path == "/api/t3-code/enabled":
            state["enabled"] = state["running"] = data["enabled"]
            return self.send(dict(enabled=state["enabled"]))
        if path == "/api/t3-code/prepare":
            state["installStage"] = "Building"
            return self.send(dict(id="fixture-install", stage="Building"))
        if path == "/api/t3-code/setup":
            provider, action = data["provider"], data["action"]
            if action == "install":
                state["accounts"][provider] = "Signed out"
                return self.send(dict(state="Succeeded", message="Harness installed.", provider=provider))
            if action in ("login", "login-browser"):
                flow = dict(state="Waiting", message="Complete sign-in.", flowId="fixture-" + provider, provider=provider, url="https://claude.com/oauth/authorize?state=fixture-only" if provider == "claude" else "https://auth.openai.com/codex/device", needsCode=provider == "claude", deviceCode="ABCD-EFGHJ" if provider == "codex" else None)
                if action == "login-browser":
                    flow.update(url="https://auth.openai.com/oauth/authorize?state=fixture-only", needsCallback=True, deviceCode=None)
                state["flows"][provider] = flow
                return self.send(flow)
            if action == "respond":
                if data.get("code") != ("http://localhost:1455/auth/callback?state=fixture-only&code=fixture-code" if provider == "codex" else "fixture-code"):
                    return self.send(dict(error="Invalid fixture code"), 409)
                state["accounts"][provider] = "Signed in";state["flows"].pop(provider, None)
                return self.send(dict(state="Succeeded", message="Subscription login saved.", provider=provider))
            if action == "cancel":
                state["flows"].pop(provider, None)
                return self.send(dict(state="Cancelled", message="Sign-in cancelled.", provider=provider))
            return self.send(state["flows"].get(provider) or dict(state=state["accounts"].get(provider, "Missing"), message="Fixture account state.", provider=provider))
        if path == "/api/t3-code/connect":
            if state["failConnect"]:
                return self.send(dict(error="The endpoint did not pass the streaming tool-call check."), 409)
            return self.send(dict(id="xur_" + "a" * 24, configured=True, harness=data["harness"], modelId=data["modelId"]))
        return self.send({}, 404)


if __name__ == "__main__":
    import argparse
    parser = argparse.ArgumentParser();parser.add_argument("--port", type=int, default=18973);args = parser.parse_args()
    ThreadingHTTPServer(("127.0.0.1", args.port), Handler).serve_forever()
