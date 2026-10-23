"""No real accounts: exercise the constrained container helper with isolated CLIs."""
import importlib.util
import io
import json
from email.message import Message
import os
from pathlib import Path
import select
import subprocess
import sys
import tempfile
import time
import unittest
from unittest.mock import patch

SOURCE = Path("src/Xur.Agent/t3-setup.py").resolve()
spec = importlib.util.spec_from_file_location("t3_setup", SOURCE)
helper = importlib.util.module_from_spec(spec)
spec.loader.exec_module(helper)
ROOT = Path(".build/evidence/t3-helper").resolve()
ROOT.mkdir(parents=True, exist_ok=True)


def stream(events):
    response = io.BytesIO(b''.join(b'data: ' + json.dumps(e).encode() + b'\n\n' for e in events))
    response.headers = Message()
    response.headers['Content-Type'] = 'text/event-stream'
    return response


class SetupTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(dir=ROOT)
        self.home = Path(self.temp.name)
        self.bin = self.home / ".local/bin"
        self.bin.mkdir(parents=True)
        self.settings = self.home / ".t3/userdata/settings.json"
        self.path_patch = patch.object(helper, "paths", return_value=(self.home, self.bin, self.settings))
        self.path_patch.start()

    def tearDown(self):
        self.path_patch.stop()
        self.temp.cleanup()

    def test_config_preserves_subscription_and_has_no_inline_opencode_secret(self):
        helper.atomic(self.settings, json.dumps({"providerInstances": {"codex": {"driver": "codex", "config": {"setupMode": "managed"}}}, "unrelated": "keep"}))
        model = dict(id="xur_" + "a" * 24, name="DeepSeek", modelId="owner/model", baseUrl="https://xur.test/api/v1", apiKey="fixture-secret", certificate="fixture-cert", contextTokens=16384)
        helper.configure(model, "opencode")
        data = json.loads(self.settings.read_text())
        self.assertEqual(data["providerInstances"]["codex"]["config"]["setupMode"], "managed")
        self.assertEqual(data["unrelated"], "keep")
        entry = data["providerInstances"][model["id"]]
        self.assertEqual(entry["config"]["customModels"][0]["slug"], model["id"] + "/owner/model")
        directory = Path(next(e["value"] for e in entry["environment"] if e["name"] == "OPENCODE_CONFIG_DIR"))
        config = json.loads((directory / "opencode.json").read_text())
        roots = helper.ssl.get_default_verify_paths().cafile
        if roots:
            self.assertIn(Path(roots).read_text(), (directory / "ca-bundle.crt").read_text())
        self.assertIn(model["certificate"], (directory / "ca-bundle.crt").read_text())
        self.assertEqual(config["providers"][model["id"]]["models"]["owner/model"]["limit"]["context"], 16384)
        self.assertNotIn("fixture-secret", self.settings.read_text() + (directory / "opencode.json").read_text())
        self.assertEqual((directory / "api-key").stat().st_mode & 0o777, 0o600)
        self.assertEqual(self.settings.stat().st_mode & 0o777, 0o600)
        (self.bin / "opencode").touch()
        self.assertTrue(helper.connection_status(model, "opencode"))
        self.assertFalse(helper.connection_status(dict(model, contextTokens=8192), "opencode"))
        self.assertFalse(helper.connection_status(dict(model, baseUrl="https://other.test/v1"), "opencode"))
        (directory / "api-key").unlink()
        self.assertFalse(helper.connection_status(model, "opencode"))

    def test_codex_configuration_is_separate_from_subscription(self):
        model = dict(id="xur_" + "b" * 24, name="DeepSeek", modelId="model", baseUrl="https://xur.test/api/v1", apiKey="fixture-key", certificate="fixture-cert", contextTokens=8192)
        helper.configure(model, "codex")
        entry = json.loads(self.settings.read_text())["providerInstances"][model["id"]]
        import tomllib
        config = tomllib.loads((Path(entry["config"]["homePath"]) / "config.toml").read_text())
        self.assertEqual(config["model_providers"]["xur"]["wire_api"], "responses")
        self.assertFalse(config["model_providers"]["xur"]["requires_openai_auth"])
        self.assertFalse((self.home / ".codex").exists())

    def test_official_auth_links_only(self):
        self.assertIsNone(helper.auth_interaction("claude", "https://claude.ai.evil.test/code")['url'])
        self.assertIsNone(helper.auth_interaction("codex", "https://user:secret@auth.openai.com/device")['url'])
        value = helper.auth_interaction("codex", "\x1b[32mhttps://auth.openai.com/codex/device\x1b[0m\nABCD-EFGHJ\n")
        self.assertEqual(value['deviceCode'], 'ABCD-EFGHJ')
        self.assertEqual(value['url'], 'https://auth.openai.com/codex/device')

    def fake_claude(self):
        binary = self.bin / "claude"
        binary.write_text('''#!/usr/bin/env python3
import json,sys
if sys.argv[1:]==['auth','status']:
 print(json.dumps({'authMethod':'claude.ai','accessToken':'NEVER-EXPOSE'}));sys.exit(0)
assert sys.argv[1:]==['auth','login','--claudeai']
print('private diagnostic NEVER-EXPOSE',flush=True)
print('https://claude.ai/oauth/authorize?state=fixture-only',flush=True)
value=input('Paste code here if prompted: ')
sys.exit(0 if value=='fixture-code#state' else 1)
''')
        binary.chmod(0o700)

    def start(self):
        env = dict(os.environ)
        env["HOME"] = str(self.home)
        env["PATH"] = str(self.bin) + os.pathsep + env["PATH"]
        return subprocess.Popen([sys.executable, "-u", str(SOURCE)], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, env=env)

    def read(self, process):
        self.assertTrue(select.select([process.stdout], [], [], 10)[0], "helper response timed out")
        line = process.stdout.readline()
        self.assertNotIn("NEVER-EXPOSE", line)
        return json.loads(line)

    def test_login_pty_code_return_and_subscription_registration(self):
        self.fake_claude()
        with self.start() as process:
            process.stdin.write(json.dumps(dict(action="login", provider="claude")) + "\n");process.stdin.flush()
            state = self.read(process)
            self.assertEqual(state["state"], "Waiting");self.assertTrue(state["needsCode"])
            process.stdin.write(json.dumps(dict(code="fixture-code#state")) + "\n");process.stdin.flush()
            self.assertEqual(self.read(process)["state"], "Succeeded")
            process.wait(timeout=5)
            self.assertEqual(process.stderr.read(), "")
        entry = json.loads(self.settings.read_text())["providerInstances"]["xur_claude_subscription"]
        self.assertEqual(entry["driver"], "claudeAgent")
        self.assertEqual(entry["config"]["homePath"], str(self.home / ".claude"))

    def test_login_cancel_and_eof_stop_cli(self):
        for cancel in (True, False):
            self.fake_claude()
            with self.start() as process:
                process.stdin.write('{"action":"login","provider":"claude"}\n');process.stdin.flush();self.read(process)
                if cancel:
                    process.stdin.write('{"action":"cancel"}\n');process.stdin.flush()
                else:
                    process.stdin.close()
                state = self.read(process)
                self.assertEqual(state["state"], "Cancelled" if cancel else "Failed")
                process.wait(timeout=5)
            self.assertFalse(self.settings.exists())

    def test_codex_device_login_registers_native_subscription(self):
        binary = self.bin / "codex"
        binary.write_text('''#!/usr/bin/env python3
from pathlib import Path
import sys,time
if sys.argv[1:]==['login','status']:
 print('Logged in using ChatGPT',file=sys.stderr);sys.exit(0)
assert sys.argv[1:]==['login','--device-auth']
print('https://auth.openai.com/codex/device\\nABCD-EFGHJ',flush=True)
while not (Path.home()/'authorized').exists(): time.sleep(.02)
''')
        binary.chmod(0o700)
        with self.start() as process:
            process.stdin.write('{"action":"login","provider":"codex"}\n');process.stdin.flush()
            state = self.read(process)
            self.assertEqual(state["deviceCode"], "ABCD-EFGHJ");self.assertFalse(state["needsCode"])
            (self.home / "authorized").touch()
            self.assertEqual(self.read(process)["state"], "Succeeded")
            process.wait(timeout=5)
        config = json.loads(self.settings.read_text())["providerInstances"]["xur_codex_subscription"]["config"]
        self.assertEqual(config["homePath"], str(self.home / ".codex"))
        self.assertEqual(config["setupMode"], "existing")

    def test_install_stops_if_controller_disconnects(self):
        binary = self.bin / "npm"
        binary.write_text('''#!/usr/bin/env python3
from pathlib import Path
import os,time
(Path.home()/'install-pid').write_text(str(os.getpid()))
time.sleep(600)
''')
        binary.chmod(0o700)
        with self.start() as process:
            process.stdin.write('{"action":"install","provider":"codex"}\n');process.stdin.flush()
            self.assertEqual(self.read(process)["state"], "Installing")
            for _ in range(200):
                if (self.home / "install-pid").exists(): break
                time.sleep(.01)
            pid = int((self.home / "install-pid").read_text())
            process.stdin.close()
            self.assertEqual(self.read(process)["state"], "Failed")
            process.wait(timeout=5)
            with self.assertRaises(ProcessLookupError):
                os.kill(pid, 0)

    def test_subscription_environment_excludes_api_routing(self):
        forbidden = {"ANTHROPIC_AUTH_TOKEN": "fixture", "ANTHROPIC_BASE_URL": "https://fixture.test", "CLAUDE_CODE_USE_BEDROCK": "1", "OPENAI_API_KEY": "fixture", "OPENAI_BASE_URL": "https://fixture.test", "CODEX_HOME": "/fixture", "CLAUDE_CONFIG_DIR": "/fixture"}
        with patch.dict(os.environ, forbidden):
            self.assertFalse(set(forbidden) & set(helper.subscription_environment()))

    def test_chat_stream_tool_round_trip(self):
        calls = []
        def request(model, path, body):
            calls.append(path)
            if len(calls) == 1:
                nonce = body["messages"][0]["content"].split("'")[1]
                arguments = json.dumps(dict(value=nonce))
                events = [{"choices": [{"delta": {"tool_calls": [{"index": 0, "id": "call-1", "function": {"name": "xur_check", "arguments": arguments[:5]}}]}}]}, {"choices": [{"delta": {"tool_calls": [{"index": 0, "function": {"arguments": arguments[5:]}}]}, "finish_reason": "tool_calls"}]}]
            else:
                self.assertEqual(body["messages"][-1]["tool_call_id"], "call-1")
                events = [{"choices": [{"delta": {"content": body["messages"][-1]["content"]}, "finish_reason": "stop"}]}]
            return stream(events)
        with patch.object(helper, "request", side_effect=request):
            helper.probe(dict(modelId="deepseek-1017"), "opencode")
        self.assertEqual(calls, ["/chat/completions", "/chat/completions"])

    def test_responses_tool_round_trip_and_incomplete_streams(self):
        for complete in (True, False):
            calls = []
            def request(model, path, body):
                calls.append(path)
                nonce = body["input"][0]["content"].split("'")[1]
                if len(calls) == 1:
                    events = [dict(type="response.output_item.done", item=dict(type="function_call", name="xur_check", call_id="call-1", arguments=json.dumps(dict(value=nonce))))]
                else:
                    self.assertEqual(body["input"][-1], dict(type="function_call_output", call_id="call-1", output=nonce))
                    events = [dict(type="response.output_text.delta", delta=nonce)]
                if complete:
                    events.append(dict(type="response.completed"))
                return stream(events)
            with patch.object(helper, "request", side_effect=request):
                if complete:
                    helper.probe(dict(modelId="deepseek-1017"), "codex")
                    self.assertEqual(calls, ["/responses", "/responses"])
                else:
                    with self.assertRaises(helper.SetupError):
                        helper.probe(dict(modelId="deepseek-1017"), "codex")
                    self.assertEqual(len(calls), 1)

    def test_rejects_non_streaming_success_and_foreign_tool_result(self):
        response = stream([])
        response.headers.replace_header("Content-Type", "application/json")
        with self.assertRaises(helper.SetupError):
            list(helper.events(response))
        events = [dict(choices=[dict(delta=dict(tool_calls=[dict(id="call-1", function=dict(name="xur_check", arguments='{"value":"wrong"}'))]), finish_reason="tool_calls")])]
        with patch.object(helper, "request", return_value=stream(events)):
            with self.assertRaises(helper.SetupError):
                helper.probe(dict(modelId="model"), "opencode")


if __name__ == "__main__":
    unittest.main()
