#!/usr/bin/env python3
"""Exercise the independent Native AOT robotics app with local fake tools.

The app owns requests and persistent settings. No agent socket, physical devices,
container engine socket, model weights or robot motion are used by this suite.
"""
import argparse
import base64
import http.client
import json
import os
from pathlib import Path
import socket
import subprocess
import sys
import tempfile
import time

ROOT = Path(__file__).resolve().parents[2]
JPEG = base64.b64decode('/9j/2Q==')


class UnixHTTP(http.client.HTTPConnection):
    def __init__(self, path):
        super().__init__('localhost', timeout=10)
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


# Generate this adapter under ignored .build paths. It exercises the real
# subprocess boundary without importing upstream hardware libraries.
FAKE_BRIDGE = '''import json, os, sys
from datetime import datetime, timedelta, timezone
from pathlib import Path
if len(sys.argv)>1:
    if sys.argv[1] not in ["--idle", "--stop"]: raise SystemExit(2)
    raise SystemExit(0)
request=json.loads(sys.stdin.readline())
with open(os.environ["XUR_ROBOT_TEST_CALLS"],"a") as calls:
    calls.write(json.dumps(request)+"\\n")
operation=request["operation"]
now=datetime.now(timezone.utc)
if operation=="camera": result={"jpeg":"/9j/2Q=="}
elif operation=="dashboard":
    result={"observedAt":now.isoformat(),"buses":[
        {"role":role,"motors":[{"id":i,"registers":{"Torque_Enable":0,"Moving":0,"Status":0}} for i in range(1,count+1)]}
        for role,count in [("left/head",8),("right/wheels",9)]],"cameras":[]}
elif operation=="inspect-markers":
    def camera(name):
        return {"name":name,"jpeg":"/9j/2Q==","frames":[{
            "capturedAt":(now+timedelta(milliseconds=i)).isoformat(),"width":100,"height":100,
            "detections":[{"id":0,"hamming":0,"decisionMargin":100,"center":[50,50],
                "corners":[[20,20],[80,20],[80,80],[20,80]]}]} for i in range(3)]}
    result={"family":"tagStandard41h12","detectorSha256":"b"*64,"cameras":[camera("head"),camera("hand")]}
else: raise SystemExit("Unexpected hardware operation: "+operation)
print(json.dumps(result))
'''


def main():
    parser = argparse.ArgumentParser()
    target = parser.add_mutually_exclusive_group(required=True)
    target.add_argument('--binary', type=Path)
    target.add_argument('--container')
    parser.add_argument('--engine', choices=['docker', 'podman'], default='docker')
    options = parser.parse_args()
    if options.container and os.geteuid() != 0:
        raise SystemExit('Use sudo for the production root-owned private-socket container test.')
    evidence = ROOT / '.build/evidence/robot-web'
    evidence.mkdir(parents=True, exist_ok=True)
    passed = []

    def check(condition, label):
        if not condition:
            raise AssertionError(label)
        passed.append(label)

    with tempfile.TemporaryDirectory(prefix='robot-', dir=ROOT / '.build') as temporary:
        directory = Path(temporary)
        app_socket = directory / 'app.sock'
        state = directory / 'state'
        tools = directory / 'tools'
        tools.mkdir()
        state.mkdir()
        (tools / 'bridge.py').write_text(FAKE_BRIDGE)
        calls_path = directory / 'calls.jsonl'
        calls_path.touch()
        name = 'xur-robot-test-' + str(os.getpid())
        log = (evidence / 'app.log').open('w')
        process = None
        active_container = False

        def start():
            nonlocal process, active_container
            if options.binary:
                binary = options.binary.resolve()
                environment = dict(os.environ, XUR_ROBOT_SOCKET=str(app_socket), XUR_ROBOT_STATE=str(state),
                                   XUR_ROBOT_TOOLS=str(tools), XUR_ROBOT_PYTHON=sys.executable,
                                   XUR_ROBOT_WORKLOAD_ID='isolated-test', XUR_ROBOT_TEST_CALLS=str(calls_path),
                                   PYTHONPYCACHEPREFIX=str(directory / 'pycache'))
                environment.pop('XUR_ROBOT_DEV_URL', None)
                environment.pop('XUR_AGENT_SOCKET', None)
                process = subprocess.Popen([str(binary)], cwd=binary.parent, env=environment,
                                           stdout=log, stderr=subprocess.STDOUT)
            else:
                subprocess.run([options.engine, 'run', '--detach', '--name', name, '--pull=never',
                    '--network=none', '--cap-drop=ALL', '--security-opt=no-new-privileges', '--security-opt=label=disable',
                    '--read-only', '--memory=512m', '--cpus=1', '--pids-limit=128',
                    '--tmpfs=/tmp:rw,nosuid,nodev,size=32m', '--volume', str(directory) + ':/test:rw',
                    '--env', 'XUR_ROBOT_SOCKET=/test/app.sock', '--env', 'XUR_ROBOT_STATE=/test/state',
                    '--env', 'XUR_ROBOT_TOOLS=/test/tools', '--env', 'XUR_ROBOT_PYTHON=python3',
                    '--env', 'XUR_ROBOT_WORKLOAD_ID=isolated-test', '--env', 'XUR_ROBOT_TEST_CALLS=/test/calls.jsonl',
                    '--env', 'PYTHONPYCACHEPREFIX=/test/pycache', options.container], check=True,
                    stdout=log, stderr=subprocess.STDOUT)
                active_container = True
            for _ in range(100):
                try:
                    if request(app_socket, '/robot/health')[0] == 200:
                        return
                except (OSError, http.client.HTTPException):
                    pass
                time.sleep(.1)
            raise AssertionError('Native app did not become ready; inspect ' + str(evidence / 'app.log'))

        def stop():
            nonlocal process, active_container
            if process:
                process.terminate()
                try:
                    process.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait()
                process = None
            if active_container:
                subprocess.run([options.engine, 'logs', name], stdout=log, stderr=subprocess.STDOUT)
                subprocess.run([options.engine, 'stop', '--time', '10', name], check=True,
                               stdout=log, stderr=subprocess.STDOUT)
                subprocess.run([options.engine, 'rm', name], check=True, stdout=log, stderr=subprocess.STDOUT)
                active_container = False
            app_socket.unlink(missing_ok=True)

        def post(route, body):
            return request(app_socket, '/robot/api/' + route, 'POST', json.dumps(body).encode(),
                           {'Content-Type': 'application/json'})

        def api(route):
            status, _, data = request(app_socket, '/robot/api/' + route)
            if status != 200:
                raise AssertionError((route, status, data.decode()))
            return json.loads(data)

        def finished(job):
            for _ in range(100):
                result = api('jobs/' + job)
                if result['state'] != 'running':
                    return result
                time.sleep(.05)
            raise AssertionError('Job did not finish: ' + job)

        try:
            if options.binary:
                binary = options.binary.resolve()
                check(binary.read_bytes()[:4] == b'\x7fELF', 'Published app is a native ELF executable')
                check(not (binary.parent / 'libcoreclr.so').exists(), 'Published app contains no CoreCLR/JIT')
            start()
            check(json.loads(request(app_socket, '/robot/health')[2])['compilation'] == 'native-aot',
                  'Native AOT health endpoint runs')
            for page, text in [('/robot/', b'Every motor'), ('/robot/tags', b'Raw observations'), ('/robot/controller', b'Button map'), ('/robot/setup', b'Hardware, demonstrations and skills.')]:
                status, headers, data = request(app_socket, page)
                check(status == 200 and text in data, 'Serves ' + page)
                check(b'id="estop"' in data and b'id="reset-estop"' in data, 'E-stop and reset are available on ' + page)
                check('blob: data:' in headers['Content-Security-Policy'], 'Camera CSP survives the proxy response')
            for asset in ['robot.js', 'robot.css', 'setup.js', 'setup.css']:
                check(request(app_socket, '/robot/' + asset)[0] == 200, 'Serves local ' + asset)
            check(request(app_socket, '/robot/Program.cs')[0] == 404, 'Does not expose source files')
            initial = api('status')
            check(initial['workloadId'] == 'isolated-test' and initial['mode'] == 'disarmed' and not initial['configured'],
                  'Container owns unconfigured status without any agent socket')
            configuration = dict(robotId='fixture', leftPort='/dev/serial/by-id/left', rightPort='/dev/serial/by-id/right',
                controllerDevice='', headCamera='/dev/v4l/by-id/head-video-index0',
                handCamera='/dev/v4l/by-id/hand-video-index0', skills=[], motionEnabled=False,
                motorLimits=dict(maxLoadRaw=100, maxCurrentRaw=100, maxFollowingErrorDegrees=2))
            status, _, data = post('configure', configuration)
            check(status == 200 and json.loads(data)['configured'] and (state / 'config.json').exists(),
                  'Container configuration persists exclusively in its state volume')
            check(api('configuration') == configuration, 'Source-generated Native AOT configuration round trips')
            for route, body in [('configure', dict(configuration, motors={'goal': 180})),
                                ('tasks', dict(kind='inspect-table', motors={'goal': 180})),
                                ('auto-calibrate', dict(force=True)),
                                ('start-controller', dict(seconds=60, motors={'goal': 180})),
                                ('reset-estop', dict(force=True)),
                                ('prepare', dict(motors={'goal': 180})),
                                ('detect-buses', dict(motors={'goal': 180})),
                                ('stop', dict(force=True)),
                                ('probe', dict(motors={'goal': 180}))]:
                check(post(route, body)[0] == 400, 'Rejects unknown high-level request fields in ' + route)
            check(not calls_path.read_text(), 'Invalid requests do not invoke local hardware tools')
            for route in ['/robot/api/poweroff', '/robot/api/jobs/not-a-job/markers',
                          '/robot/api/jobs/' + 'a' * 32 + '/captures/config.json', '/robot/api/../diagnostics/ssh']:
                check(request(app_socket, route)[0] == 404, 'Rejects unsupported route ' + route)
            check(request(app_socket, '/robot/api/tasks', 'POST', b'x' * 16385,
                          {'Content-Type': 'application/json'})[0] == 413, 'Bounds large request bodies')
            check(request(app_socket, '/robot/api/tasks', 'POST', b'{}', {'Content-Type': 'text/plain'})[0] == 415,
                  'Requires JSON for operations')
            check(post('start-controller', dict(seconds=60))[0] == 409 and api('status')['stopLatched'],
                  'Controller start cannot bypass missing calibration')
            status, _, data = post('tasks', dict(kind='inspect-markers'))
            check(status == 202, 'Accepts marker inspection using local tools')
            job = json.loads(data)['id']
            check(finished(job)['state'] == 'completed', 'Container validates and persists real subprocess marker observations')
            report = api('jobs/' + job + '/markers')
            check(report['sharedIds'] == [0] and not report['jointCalibrationApproved'] and not report['motorCommandsIssued']
                  and not report['metricPoseAvailable'], 'Raw marker data retains unapproved pose and calibration state')
            status, headers, data = request(app_socket, '/robot/api/jobs/' + job + '/captures/head-markers.jpg')
            check(status == 200 and data == JPEG and headers['Content-Type'] == 'image/jpeg',
                  'Serves exact survey camera frame from container-owned evidence')
            status, _, data = request(app_socket, '/robot/api/cameras/head')
            check(status == 200 and data == JPEG, 'Live camera endpoint invokes the local bridge without a privileged proxy')
            status, _, data = post('auto-calibrate', {})
            check(status == 202 and finished(json.loads(data)['id'])['state'] == 'completed',
                  'Automatic calibration completes a read-only assessment')
            assessment = api('calibration-assessment')
            check(assessment['state'] == 'blocked' and not assessment['jointCalibrationApproved']
                  and not assessment['motorCommandsIssued'], 'Automatic calibration does not fabricate calibration approval')
            status, _, data = post('prepare', {})
            check(status == 202 and finished(json.loads(data)['id'])['state'] == 'failed',
                  'Preparation reports unavailable fixture hardware rather than guessing connected devices')
            check(post('stop', {})[0] == 200 and api('status')['stopLatched'], 'Software stop leaves the app disarmed')
            status, _, data = post('estop', {})
            check(status == 202 and json.loads(data)['emergencyStopLatched'] and (state / 'estop-latched').exists(),
                  'E-stop persists its latch within the container state volume')
            post('stop', {})
            stop()
            start()
            restarted = api('status')
            check(restarted['configured'] and restarted['emergencyStopLatched'] and restarted['stopLatched']
                  and restarted['armedUntil'] is None and api('configuration') == configuration,
                  'Container restart preserves settings and E-stop while never resuming a motion session')
            status, _, data = post('reset-estop', {})
            check(status == 202 and finished(json.loads(data)['id'])['state'] == 'completed',
                  'E-stop reset checks fresh feedback from all seventeen fixture motors')
            check(not api('status')['emergencyStopLatched'] and api('status')['stopLatched']
                  and not (state / 'estop-latched').exists(), 'Checked reset retains disarmed state')
            calls = [json.loads(line) for line in calls_path.read_text().splitlines()]
            check(calls and all(c['operation'] in ['camera', 'dashboard', 'inspect-markers'] for c in calls),
                  'All adapter calls remain read-only observations; no motor operation is issued')
            check(not (directory / 'agent.sock').exists(), 'Independent robotics app never requires an agent socket')
        finally:
            stop()
            log.close()
            if not any('never requires an agent socket' in label for label in passed):
                print((evidence / 'app.log').read_text())
    receipt = dict(suite='RobotNativeAotIndependent', passed=passed,
                   hardware='Local fake subprocess tools; no physical devices or engine/agent sockets')
    (evidence / 'validation.json').write_text(json.dumps(receipt, indent=2) + '\n')
    print(json.dumps(receipt))


if __name__ == '__main__':
    main()
