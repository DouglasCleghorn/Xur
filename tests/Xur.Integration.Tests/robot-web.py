#!/usr/bin/env python3
"""Test the actual Native AOT app/container using an isolated Unix-socket agent.

No robot devices, network privileges or container-engine socket enter the app.
"""
import argparse
import base64
from datetime import datetime, timezone
import http.client
from http.server import BaseHTTPRequestHandler
import json
import os
from pathlib import Path
import socket
import socketserver
import subprocess
import tempfile
import threading
import time

ROOT = Path(__file__).resolve().parents[2]
JOB = 'a' * 32
JPEG = base64.b64decode('/9j/2Q==')


class UnixHTTP(http.client.HTTPConnection):
    def __init__(self, path):
        super().__init__('localhost', timeout=5)
        self.path = str(path)

    def connect(self):
        self.sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        self.sock.settimeout(self.timeout)
        self.sock.connect(self.path)


def request(path, route, method='GET', body=None, headers=None):
    connection = UnixHTTP(path)
    try:
        connection.request(method, route, body=body, headers=headers or {})
        response = connection.getresponse()
        return response.status, dict(response.getheaders()), response.read()
    finally:
        connection.close()


class Agent(socketserver.ThreadingMixIn, socketserver.UnixStreamServer):
    daemon_threads = True


def fixture():
    now = datetime.now(timezone.utc).isoformat()
    def camera(name):
        return {'name': name, 'frames': [dict(capturedAt=now, width=100, height=100,
                ambiguousDuplicateIds=[], detections=[dict(id=0, hamming=0, decisionMargin=100,
                center=[50, 50], corners=[[20, 20], [80, 20], [80, 80], [20, 80]])]) for _ in range(3)],
                'markers': [dict(id=0, detectedFrames=3)]}
    return {'family': 'tagStandard41h12', 'observedAt': now, 'detectorSha256': 'b' * 64,
            'cameras': [camera('head'), camera('hand')], 'sharedIds': [0],
            'jointCalibrationApproved': False, 'motorCommandsIssued': False, 'metricPoseAvailable': False}


def main():
    parser = argparse.ArgumentParser()
    target = parser.add_mutually_exclusive_group(required=True)
    target.add_argument('--binary', type=Path)
    target.add_argument('--container')
    parser.add_argument('--engine', choices=['docker', 'podman'], default='docker')
    options = parser.parse_args()
    if options.container and os.geteuid() != 0:
        raise SystemExit('Run container validation with sudo: production uses root-owned private sockets and a root app with all capabilities dropped.')
    evidence = ROOT / '.build/evidence/robot-web'
    evidence.mkdir(parents=True, exist_ok=True)
    calls = []
    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *args): pass
        def do_GET(self): self.reply()
        def do_POST(self): self.reply()
        def reply(self):
            data = self.rfile.read(int(self.headers.get('Content-Length', 0)))
            calls.append((self.command, self.path, dict(self.headers), data))
            status = 200
            content_type = 'application/json'
            result = {'mode': 'disarmed', 'workloadId': 'isolated-test', 'stopLatched': True,
                      'problems': ['Test fixture — no robot connected.']}
            if self.command == 'POST':
                parsed = json.loads(data)
                if 'motors' in parsed: status, result = 400, {'error': 'Invalid high-level request.'}
                elif self.path == '/robotics/start-controller': status, result = 409, {'error': 'Calibration required.'}
                else: status, result = 202, {'id': JOB, 'kind': 'inspect-markers', 'state': 'running'}
            elif self.path.endswith('/captures/head-markers.jpg'):
                content_type, result = 'image/jpeg', JPEG
            elif self.path.endswith('/markers'): result = fixture()
            elif self.path == '/robotics/jobs': result = []
            payload = result if isinstance(result, bytes) else json.dumps(result).encode()
            self.send_response(status)
            self.send_header('Content-Type', content_type)
            self.send_header('Content-Length', len(payload))
            self.end_headers()
            self.wfile.write(payload)
    passed = []
    def check(condition, label):
        if not condition: raise AssertionError(label)
        passed.append(label)
    with tempfile.TemporaryDirectory(prefix='robot-', dir=ROOT / '.build') as temporary:
        directory = Path(temporary)
        backend_socket, app_socket = directory / 'agent.sock', directory / 'app.sock'
        server = Agent(str(backend_socket), Handler)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        name = 'xur-robot-test-' + str(os.getpid())
        log = (evidence / 'app.log').open('w')
        process = None
        try:
            if options.binary:
                binary = options.binary.resolve()
                check(binary.read_bytes()[:4] == b'\x7fELF', 'Published app is a native ELF executable')
                check(not (binary.parent / 'libcoreclr.so').exists(), 'Published app contains no CoreCLR/JIT')
                environment = dict(os.environ, XUR_AGENT_SOCKET=str(backend_socket), XUR_ROBOT_SOCKET=str(app_socket))
                environment.pop('XUR_ROBOT_DEV_URL', None)
                process = subprocess.Popen([str(binary)], cwd=binary.parent, env=environment, stdout=log, stderr=subprocess.STDOUT)
            else:
                subprocess.run([options.engine, 'run', '--detach', '--name', name, '--pull=never',
                    '--network=none', '--cap-drop=ALL', '--security-opt=no-new-privileges', '--security-opt=label=disable', '--read-only',
                    '--memory=256m', '--cpus=1', '--pids-limit=128', '--tmpfs=/tmp:rw,nosuid,nodev,size=16m',
                    '--volume', str(directory) + ':/test:rw', '--env', 'XUR_AGENT_SOCKET=/test/agent.sock',
                    '--env', 'XUR_ROBOT_SOCKET=/test/app.sock', options.container], check=True, stdout=log, stderr=subprocess.STDOUT)
            for _ in range(100):
                try:
                    if request(app_socket, '/robot/health')[0] == 200: break
                except (OSError, http.client.HTTPException): pass
                time.sleep(.1)
            else: raise AssertionError('Native app did not become ready; inspect ' + str(evidence / 'app.log'))
            status, headers, data = request(app_socket, '/robot/health')
            check(json.loads(data)['compilation'] == 'native-aot', 'Native AOT health endpoint runs')
            for page, text in [('/robot/', b'Every motor'), ('/robot/tags', b'Raw observations'), ('/robot/controller', b'Button map')]:
                status, headers, data = request(app_socket, page)
                check(status == 200 and text in data, 'Serves ' + page)
                check(b'id="estop"' in data and b'id="reset-estop"' in data, 'E-stop and reset are available on ' + page)
                check('blob: data:' in headers['Content-Security-Policy'], 'Camera CSP survives the proxy response')
            for asset in ['robot.js', 'robot.css']:
                check(request(app_socket, '/robot/' + asset)[0] == 200, 'Serves local ' + asset)
            check(request(app_socket, '/robot/Program.cs')[0] == 404, 'Does not expose source files')
            status, _, data = request(app_socket, '/robot/api/status', headers={'Cookie': 'secret', 'Authorization': 'Bearer secret', 'Tailscale-User-Login': 'owner'})
            check(status == 200 and json.loads(data)['mode'] == 'disarmed', 'High-level agent status relays over Unix socket')
            check(not any(key.lower() in ['cookie', 'authorization', 'tailscale-user-login'] for key in calls[-1][2]), 'Browser credentials and identity headers never reach the agent')
            before = len(calls)
            for route in ['/robot/api/poweroff', '/robot/api/jobs/not-a-job/markers', '/robot/api/jobs/' + JOB + '/captures/config.json', '/robot/api/../diagnostics/ssh']:
                check(request(app_socket, route)[0] == 404, 'Rejects non-allowlisted route ' + route)
            check(len(calls) == before, 'Rejected paths never contact the privileged agent')
            status, _, data = request(app_socket, '/robot/api/start-controller', 'POST', b'{"seconds":60}', {'Content-Type': 'application/json'})
            check(status == 409 and json.loads(data)['error'] == 'Calibration required.', 'Preserves upstream calibration rejection')
            for operation in ['estop', 'reset-estop']:
                check(request(app_socket, '/robot/api/' + operation, 'POST', b'{}', {'Content-Type': 'application/json'})[0] == 202,
                      'Relays the high-level ' + operation + ' operation')
            check(request(app_socket, '/robot/api/tasks', 'POST', b'{"motors":{"goal":180}}', {'Content-Type': 'application/json'})[0] == 400, 'Preserves upstream rejection of motor targets')
            before = len(calls)
            check(request(app_socket, '/robot/api/tasks', 'POST', b'x' * 16385, {'Content-Type': 'application/json'})[0] == 413, 'Bounds large request bodies')
            check(request(app_socket, '/robot/api/tasks', 'POST', b'{}', {'Content-Type': 'text/plain'})[0] == 415, 'Requires JSON for operations')
            check(len(calls) == before, 'Rejected request bodies never reach the agent')
            status, headers, data = request(app_socket, '/robot/api/jobs/' + JOB + '/captures/head-markers.jpg')
            check(status == 200 and data == JPEG and headers['Content-Type'] == 'image/jpeg', 'Relays the exact matched survey frame')
            status, _, data = request(app_socket, '/robot/api/jobs/' + JOB + '/markers')
            check(status == 200 and json.loads(data)['jointCalibrationApproved'] is False, 'Raw tag data retains unapproved calibration state')
            server.shutdown(); server.server_close(); backend_socket.unlink(missing_ok=True)
            check(request(app_socket, '/robot/api/status')[0] == 503, 'Unavailable agent returns a clear 503 under Native AOT')
        finally:
            server.shutdown(); server.server_close()
            if process:
                process.terminate()
                try: process.wait(timeout=5)
                except subprocess.TimeoutExpired: process.kill(); process.wait()
            elif options.container:
                subprocess.run([options.engine, 'logs', name], stdout=log, stderr=subprocess.STDOUT)
                subprocess.run([options.engine, 'rm', '--force', name], stdout=log, stderr=subprocess.STDOUT)
            log.close()
            if not passed or not any('Unavailable agent' in label for label in passed):
                print((evidence / 'app.log').read_text())
    receipt = {'suite': 'RobotNativeAot', 'passed': passed, 'hardware': 'No robot devices passed to app'}
    (evidence / 'validation.json').write_text(json.dumps(receipt, indent=2) + '\n')
    print(json.dumps(receipt))


if __name__ == '__main__': main()
