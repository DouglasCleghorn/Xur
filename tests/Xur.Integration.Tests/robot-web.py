#!/usr/bin/env python3
"""Exercise the independent Native AOT robotics app with local fake tools.

The app owns requests and persistent settings. No agent socket, physical devices,
container engine socket, model weights or robot motion are used by this suite.
"""
import argparse
import base64
import http.client
import hashlib
import io
import json
import os
from pathlib import Path
import socket
import subprocess
import sys
import tempfile
import time
import zipfile

ROOT = Path(__file__).resolve().parents[2]
JPEG = base64.b64decode('/9j/2Q==')
PNG = base64.b64decode('iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAIAAACQkWg2AAAAEElEQVR4nGNgGAWjYBTAAAADEAABPywr7AAAAABJRU5ErkJggg==')


def emote_fixture():
    # This minimal MP4 signature tests HTTP storage/ranges, not video decoding.
    # Actual generated MP4s are decoded and reviewed separately in MuJoCo exports.
    video = b'\x00\x00\x00\x28ftyp' + bytes(32)
    frames = [dict(frame=i, timestamp=i/10, simulationTime=i/10, action=[0]*6,
                   **{'observation.state': [0]*6, 'observation.velocity': [0]*6}, contacts=0) for i in range(11)]
    files = {'recording.jsonl': ''.join(json.dumps(frame)+'\n' for frame in frames).encode(),
             'trajectory.npz': b'fixture', 'preview.mp4': video,
             'XLeRobot-LICENSE': b'Apache-2.0 fixture', 'LICENSE': b'MIT fixture', 'licensing.md': b'Fixture notices'}
    manifest = dict(schemaVersion=1, kind='xur-emote-candidate', id='wave-right', name='Wave', arm='right', task='wave',
        coordinateSpace='xlerobot-model-radians', fps=10, durationSeconds=1,
        jointNames=[name+'_R' for name in ['Rotation', 'Pitch', 'Elbow', 'Wrist_Pitch', 'Wrist_Roll', 'Jaw']],
        source=dict(repository='https://github.com/Vector-Wangel/XLeRobot', revision='a'*40, simulator='MuJoCo', hardwareApproved=False),
        hardwareApproved=False, physicalCalibrationHash=None,
        files={name: hashlib.sha256(data).hexdigest() for name, data in files.items()})
    files['manifest.json'] = json.dumps(manifest).encode()
    def archive(payloads):
        stream = io.BytesIO()
        with zipfile.ZipFile(stream, 'w', zipfile.ZIP_DEFLATED) as package:
            for name, data in payloads.items(): package.writestr(name, data)
        return stream.getvalue()
    valid = archive(files)
    files['preview.mp4'] += b'changed'
    return valid, video, archive(files)


class UnixHTTP(http.client.HTTPConnection):
    def __init__(self, path):
        super().__init__('localhost', timeout=10)
        self.path = str(path)

    def connect(self):
        self.sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        self.sock.settimeout(self.timeout)
        # Linux resolves the directory descriptor without putting a long
        # checkout/worktree path into the 108-byte Unix socket address.
        descriptor = os.open(Path(self.path).parent, os.O_RDONLY | os.O_DIRECTORY)
        try:
            self.sock.connect('/proc/self/fd/' + str(descriptor) + '/' + Path(self.path).name)
        finally:
            os.close(descriptor)


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
FAKE_BRIDGE = '''import base64, hashlib, json, os, struct, sys, time, uuid
from datetime import datetime, timedelta, timezone
from pathlib import Path
if len(sys.argv)>1:
    if sys.argv[1] == "--hand-calibrate":
        json.loads(sys.stdin.readline())
        print(json.dumps(dict(phase="left-reference", detail="Fixture rejected temperature", canAdvance=True,
            torqueOffVerified=True, motors=[dict(name="left_arm_gripper", position=2047, minimum=2047,
            maximum=2047, temperature=45, torque=0)])), flush=True)
        raise SystemExit(0)
    if sys.argv[1] not in ["--idle", "--stop"]: raise SystemExit(2)
    raise SystemExit(0)
request=json.loads(sys.stdin.readline())
with open(os.environ["XUR_ROBOT_TEST_CALLS"],"a") as calls:
    calls.write(json.dumps(request)+"\\n")
operation=request["operation"]
now=datetime.now(timezone.utc)
if operation=="camera": result={"jpeg":"/9j/2Q=="}
elif operation=="prepare": raise SystemExit("Selected fixture devices are unavailable")
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
elif operation=="capture-calibration-observation":
    configuration=request["configuration"]
    samples=request["request"]["samples"]
    if type(samples) is not int or not 1<=samples<=3: raise SystemExit("Invalid bounded fixture request")
    identity=uuid.uuid4().hex
    state=Path(os.environ["XUR_ROBOT_STATE"])
    original=state/".build/evidence/calibration-observations"/identity
    backup=state/"backups/calibration-observations"/identity
    original.mkdir(parents=True);backup.mkdir(parents=True)
    files={}
    def stamp(): return {"utc":datetime.now(timezone.utc).isoformat(),"monotonicNanoseconds":time.monotonic_ns()}
    began=stamp()
    def add(name,value): files[name]=json.dumps(value,separators=(",",":")).encode()
    add("session.json",{"version":1,"id":identity,"startedAt":began,"requestedSamples":samples,"robotId":configuration["robotId"],
        "selectedDevices":{key:configuration[key] for key in ["leftPort","rightPort","headCamera","handCamera"]},
        "cameras":["head","hand"],"motorCommandsIssued":False,"jointCalibrationApproved":False,"synchronizedStereoFrames":False,
        "exposureTimestampsAvailable":False,"registerValuesNormalized":False,"timestampMeaning":"Synthetic sequential host read brackets.",
        "imageFormat":"lossless PNG plus unmodified NumPy uint8 BGR arrays"})
    png=base64.b64decode("iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAIAAACQkWg2AAAAEElEQVR4nGNgGAWjYBTAAAADEAABPywr7AAAAABJRU5ErkJggg==")
    header="{'descr': '|u1', 'fortran_order': False, 'shape': (16, 16, 3), }".encode()
    header+=b" "*(64-(10+len(header)+1)%64)+bytes([10])
    raw=bytes([147,78,85,77,80,89,1,0])+struct.pack("<H",len(header))+header+bytes(16*16*3)
    for index in range(samples):
        stem="sample-"+str(index).zfill(2)
        def motors(phase):
            first=stamp()
            buses=[{"role":role,"port":configuration[key],"motors":[{"id":number,"name":role.replace("/","-")+"-joint-"+str(number),
                "model":"STS3215","observedAt":datetime.now(timezone.utc).isoformat(),
                "registers":{"Present_Position":1024+number,"Torque_Enable":0,"Present_Velocity":0,"Operating_Mode":0,"Moving":0,"Status":0}} for number in range(1,count+1)]}
                for role,key,count in [("left/head","leftPort",8),("right/wheels","rightPort",9)]]
            last=stamp();name=stem+"-motors-"+phase+".json"
            add(name,{"phase":phase,"readStartedAt":first,"readCompletedAt":last,"buses":buses})
            return {"phase":phase,"file":name,"readStartedAt":first,"readCompletedAt":last}
        before=motors("before-cameras")
        cameras=[]
        for camera_name in ["head","hand"]:
            first=stamp();last=stamp()
            raw_name=stem+"-"+camera_name+".npy";png_name=stem+"-"+camera_name+".png"
            files[raw_name]=raw;files[png_name]=png
            cameras.append({"name":camera_name,"requestedFullResolution":True,"readStartedAt":first,"readCompletedAt":last,
                "width":16,"height":16,"channels":3,"dtype":"uint8","colorOrder":"BGR","rawArrayFile":raw_name,"losslessImageFile":png_name})
        after=motors("after-cameras")
        add(stem+".json",{"index":index,"cameras":cameras,"motorSamples":[before,after],
            "maxObservedEncoderDeltaRaw":0,"stationaryAcrossReadBrackets":True})
    manifest={"version":1,"id":identity,"startedAt":began,"completedAt":stamp(),"requestedSamples":samples,"sampleCount":samples,"complete":True,
        "motorCommandsIssued":False,"jointCalibrationApproved":False,"synchronizedStereoFrames":False,"problems":[],
        "files":[{"name":name,"bytes":len(data),"sha256":hashlib.sha256(data).hexdigest()} for name,data in sorted(files.items())]}
    manifest_bytes=json.dumps(manifest,separators=(",",":")).encode()
    files["manifest.json"]=manifest_bytes
    for name,data in files.items():
        for folder in [original,backup]:
            path=folder/name;path.write_bytes(data);path.chmod(0o400)
    original.chmod(0o500);backup.chmod(0o500)
    result={"version":1,"id":identity,"capturedAt":began["utc"],"sampleCount":samples,"complete":True,
        "manifestRelativePath":".build/evidence/calibration-observations/"+identity+"/manifest.json",
        "manifestSha256":hashlib.sha256(manifest_bytes).hexdigest(),"backupRelativePath":"backups/calibration-observations/"+identity,
        "backupVerified":True,"motorCommandsIssued":False,"jointCalibrationApproved":False,"synchronizedStereoFrames":False,"problems":[]}
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
        app_socket = directory / 'socket/app.sock'
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
                environment = dict(os.environ, XUR_ROBOT_SOCKET='/proc/self/cwd/socket/app.sock', ASPNETCORE_CONTENTROOT=str(binary.parent), XUR_ROBOT_STATE=str(state),
                                   XUR_ROBOT_TOOLS=str(tools), XUR_ROBOT_PYTHON=sys.executable,
                                   XUR_ROBOT_WORKLOAD_ID='isolated-test', XUR_ROBOT_TEST_CALLS=str(calls_path),
                                   PYTHONPYCACHEPREFIX=str(directory / 'pycache'))
                environment.pop('XUR_ROBOT_DEV_URL', None)
                environment.pop('XUR_AGENT_SOCKET', None)
                process = subprocess.Popen([str(binary)], cwd=directory, env=environment,
                                           stdout=log, stderr=subprocess.STDOUT)
            else:
                subprocess.run([options.engine, 'run', '--detach', '--name', name, '--pull=never',
                    '--network=none', '--cap-drop=ALL', '--security-opt=no-new-privileges', '--security-opt=label=disable',
                    '--read-only', '--memory=512m', '--cpus=1', '--pids-limit=128',
                    '--tmpfs=/tmp:rw,nosuid,nodev,size=32m', '--volume', str(directory) + ':/test:rw',
                    '--env', 'XUR_ROBOT_SOCKET=/test/socket/app.sock', '--env', 'XUR_ROBOT_STATE=/test/state',
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
            for page, text in [('/robot/', b'Every motor'), ('/robot/emotes', b'<h1>Emotes</h1>'), ('/robot/tags', b'Raw observations'), ('/robot/controller', b'Button map'), ('/robot/setup', b'<h1>Robot setup</h1>'), ('/robot/calibrate', b'<h1>Hand-guided calibration</h1>')]:
                status, headers, data = request(app_socket, page)
                check(status == 200 and text in data, 'Serves ' + page)
                check(b'id="estop"' in data and b'id="reset-estop"' in data, 'E-stop and reset are available on ' + page)
                check('blob: data:' in headers['Content-Security-Policy'], 'Camera CSP survives the proxy response')
                check(b'href="/robot/emotes"' in data, 'Emotes navigation is available on ' + page)
            for asset in ['robot.js', 'robot.css', 'setup.js', 'setup.css', 'emotes.js', 'emote-files.js', 'emote-files.css', 'backups.js', 'camera-capabilities.js', 'calibration-observations.js', 'calibrate.js', 'calibrate.css', 'vendor/signalr/signalr.min.js', 'vendor/signalr/LICENSE.txt', 'vendor/signalr/third-party-notices.txt']:
                check(request(app_socket, '/robot/' + asset)[0] == 200, 'Serves local ' + asset)
            dashboard = request(app_socket, '/robot/')[2]
            emotes = request(app_socket, '/robot/emotes')[2]
            check(b'id="emote-import"' not in dashboard and b'emote-files.js' not in dashboard
                  and b'emote-files.css' not in dashboard, 'Dashboard has no emote import panel or assets')
            check(b'data-page="emotes"' in emotes and b'id="emote-import"' in emotes
                  and b'/robot/emote-files.js' in emotes and b'/robot/emote-files.css' in emotes
                  and b'href="/robot/emotes" aria-current="page"' in emotes,
                  'Dedicated Emotes page provides local file import and video library')
            for removed in ['/robot/api/simulation', '/robot/api/simulation/emote', '/robot/api/simulation/stop']:
                check(request(app_socket, removed)[0] == 404, 'Embedded simulator route removed: ' + removed)
            guided = request(app_socket, '/robot/calibrate')[2]
            check(b'setup-mode' not in guided and b'gesture-simulation' not in guided
                  and b'Start hand-guided setup' in guided and b'id="hardware-checks" hidden' not in guided,
                  'Guided setup offers only real hand calibration with visible operator checks')
            status, _, negotiated = request(app_socket, '/robot/realtime/negotiate?negotiateVersion=1', 'POST', b'')
            check(status == 200, 'Real calibration SignalR transport negotiates')
            realtime = '/robot/realtime?id=' + json.loads(negotiated)['connectionToken']
            request(app_socket, realtime)
            request(app_socket, realtime, 'POST', b'{"protocol":"json","version":1}\x1e')
            updates = []
            for _ in range(3):
                frames = request(app_socket, realtime)[2].decode().split('\x1e')
                updates.extend(json.loads(frame) for frame in frames if frame)
                if any(frame.get('target') == 'setup' for frame in updates):
                    break
            setup = next(frame for frame in updates if frame.get('target') == 'setup')
            update = json.loads(setup['arguments'][0])
            check(update['calibration']['mode'] == 'hardware' and 'simulation' not in update,
                  'SignalR streams actual calibration state without embedded simulation')
            request(app_socket, realtime, 'DELETE')
            check(request(app_socket, '/robot/Program.cs')[0] == 404, 'Does not expose source files')
            initial = api('status')
            check(initial['workloadId'] == 'isolated-test' and initial['mode'] == 'disarmed' and not initial['configured'],
                  'Container owns unconfigured status without any agent socket')
            check(api('emote-files') == [], 'Emote library starts empty without hardware setup')
            package, video, corrupt_package = emote_fixture()
            status, _, data = request(app_socket, '/robot/api/emote-files', 'POST', package, {'Content-Type': 'application/zip'})
            candidate = json.loads(data)
            check(status == 201 and candidate['state'] == 'preview-only' and not candidate['manifest']['hardwareApproved']
                  and candidate['manifest']['physicalCalibrationHash'] is None, 'Native ZIP upload imports an unapproved preview candidate')
            key = candidate['key']
            check(request(app_socket, '/robot/api/emote-files', 'POST', package, {'Content-Type': 'application/zip'})[0] == 201
                  and len(api('emote-files')) == 1, 'Identical candidate uploads preserve and deduplicate the original')
            status, headers, data = request(app_socket, '/robot/api/emote-files/'+key+'/preview', headers={'Range': 'bytes=4-7'})
            check(status == 206 and data == b'ftyp' and headers['Content-Type'] == 'video/mp4'
                  and headers['Content-Range'] == 'bytes 4-7/40', 'Local MP4 preview supports byte ranges for browser seeking')
            check(request(app_socket, '/robot/api/emote-files/'+key+'/download')[2] == package,
                  'Candidate download retains the byte-identical portable emote archive')
            check(request(app_socket, '/robot/api/emote-files', 'POST', corrupt_package, {'Content-Type': 'application/zip'})[0] == 400
                  and request(app_socket, '/robot/api/emote-files', 'POST', b'{}', {'Content-Type': 'application/json'})[0] == 415
                  and request(app_socket, '/robot/api/emote-files', 'POST', b'x'*(16*1024*1024+1), {'Content-Type': 'application/zip'})[0] == 413,
                  'Native upload rejects corrupted payloads, wrong media types and oversized files')
            check(api('status') == initial and not calls_path.read_text() and not (state/'config.json').exists()
                  and not (state/'calibration').exists() and not (state/'datasets').exists(),
                  'Import and video downloads do not invoke hardware, approve motion or create replay datasets')
            capabilities = api('camera-capabilities')
            check(capabilities['selectedHeadCamera'] is None and capabilities['selectedHandCamera'] is None
                  and not capabilities['cameraSettingsChanged'] and not capabilities['motorCommandsIssued']
                  and isinstance(capabilities['devices'], list) and isinstance(capabilities['problems'], list)
                  and not calls_path.read_text(),
                  'Native camera capability inventory works before setup without robot/capture tool calls')
            check(request(app_socket, '/robot/api/camera-capabilities?device=/dev/video0&args=--set-ctrl')[0] == 400
                  and request(app_socket, '/robot/api/camera-capabilities', 'GET', b'{"pan":1}')[0] == 400
                  and post('camera-capabilities', {})[0] == 405 and not calls_path.read_text(),
                  'Native camera capability endpoint rejects supplied device/options/body and control-setting writes')
            check(api('backups/settings') == {'url': '', 'tokenStored': False} and api('backups/recordings') == [],
                  'Unconfigured backup settings remain inside the independent app and never claim verification')
            check(post('backups/settings', dict(url='http://xur-epyc/', token='t' * 64))[0] == 409,
                  'Native backup settings require a trusted HTTPS receiver')
            check(post('backups/settings', dict(url='https://example.invalid:9443/', token='t' * 64, motors={'goal': 180}))[0] == 400,
                  'Native backup settings reject unknown motor fields')
            status, _, data = post('backups/settings', dict(url='https://example.invalid:9443/', token='t' * 64))
            check(status == 200 and json.loads(data)['tokenStored'] and 't' * 64 not in data.decode(),
                  'Backup connection persists privately and its token is absent from public responses')
            check(post('backups/retry', dict(recordingId='../../config'))[0] == 409,
                  'Backup retry identities cannot traverse into application settings')
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
                                ('calibration-observations', dict(samples=1, motors={'goal': 180})),
                                ('start-controller', dict(seconds=60, motors={'goal': 180})),
                                ('reset-estop', dict(force=True)),
                                ('prepare', dict(motors={'goal': 180})),
                                ('detect-buses', dict(motors={'goal': 180})),
                                ('stop', dict(force=True)),
                                ('probe', dict(motors={'goal': 180}))]:
                check(post(route, body)[0] == 400, 'Rejects unknown high-level request fields in ' + route)
            check(not calls_path.read_text(), 'Invalid requests do not invoke local hardware tools')
            for samples in [0, 4]:
                check(post('calibration-observations', dict(samples=samples))[0] == 409 and not calls_path.read_text(),
                      'Rejects out-of-range stationary sample count before tool calls: ' + str(samples))
            check(post('calibration-observations', dict(samples=1, device='/dev/video0'))[0] == 400
                  and post('calibration-observations', dict(samples=True))[0] == 400 and not calls_path.read_text(),
                  'Observation capture rejects caller-selected devices and noninteger sample counts')
            for route in ['/robot/api/poweroff', '/robot/api/jobs/not-a-job/markers',
                          '/robot/api/jobs/' + 'a' * 32 + '/captures/config.json', '/robot/api/../diagnostics/ssh']:
                check(request(app_socket, route)[0] == 404, 'Rejects unsupported route ' + route)
            check(request(app_socket, '/robot/api/tasks', 'POST', b'x' * 16385,
                          {'Content-Type': 'application/json'})[0] == 413, 'Bounds large request bodies')
            check(request(app_socket, '/robot/api/tasks', 'POST', b'{}', {'Content-Type': 'text/plain'})[0] == 415,
                  'Requires JSON for operations')
            check(post('start-controller', dict(seconds=60))[0] == 409 and api('status')['stopLatched'],
                  'Controller start cannot bypass missing calibration')
            status, _, data = post('hand-calibration/start', dict(mode='hardware', operatorPresent=True,
                armsSupported=True, disconnectReady=True))
            check(status == 202 and finished(json.loads(data)['id'])['state'] == 'failed',
                  'Native calibration rejects the Python subprocess temperature at the exact 45C boundary')
            failed_setup = api('hand-calibration')
            check(failed_setup['phase'] == 'failed' and not failed_setup['torqueOffVerified']
                  and not failed_setup['canAdvance'] and 'left_arm_gripper reported 45°C' in failed_setup['detail']
                  and failed_setup['motors'][0]['temperature'] == 45 and failed_setup['motors'][0]['torque'] == 0,
                  'Native feedback preserves the rejected motor reading and names the cause without approving torque')
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
            status, _, data = post('calibration-observations', dict(samples=2))
            observation_job = json.loads(data)['id']
            check(status == 202 and finished(observation_job)['state'] == 'completed',
                  'Bounded stationary observations complete through the native subprocess boundary')
            capture = api('calibration-observations')
            check(capture['complete'] and capture['sampleCount'] == 2 and capture['backupVerified']
                  and not capture['motorCommandsIssued'] and not capture['jointCalibrationApproved']
                  and not capture['synchronizedStereoFrames'] and api('status')['stopLatched']
                  and not (state / 'calibration/receipt.json').exists(),
                  'Camera and raw encoder acquisition needs no controller, motion enablement or calibration and grants no approval')
            capture_prefix = 'calibration-observations/' + capture['id']
            status, _, manifest_bytes = request(app_socket, '/robot/api/' + capture_prefix + '/manifest')
            manifest = json.loads(manifest_bytes)
            check(status == 200 and len(manifest['files']) == 15
                  and hashlib.sha256(manifest_bytes).hexdigest() == capture['manifestSha256']
                  and (state / capture['backupRelativePath'] / 'manifest.json').read_bytes() == manifest_bytes,
                  'Native API exposes the hash-checked original manifest and identical independent local backup')
            original_folder = (state / capture['manifestRelativePath']).parent
            for artifact in manifest['files']:
                original = original_folder / artifact['name']
                copied = state / capture['backupRelativePath'] / artifact['name']
                check(original.stat().st_size == artifact['bytes'] and hashlib.sha256(original.read_bytes()).hexdigest() == artifact['sha256']
                      and copied.read_bytes() == original.read_bytes() and copied.stat().st_ino != original.stat().st_ino,
                      'Preserved original and separate backup bytes match ' + artifact['name'])
            status, headers, image_bytes = request(app_socket, '/robot/api/' + capture_prefix + '/files/sample-00-head.png')
            check(status == 200 and image_bytes == PNG and headers['Content-Type'] == 'image/png',
                  'Native observation download returns exact lossless PNG pixels')
            for invalid in [capture_prefix + '/files/unlisted.json', capture_prefix + '/files/latest.json',
                            'calibration-observations/not-an-id/manifest']:
                check(request(app_socket, '/robot/api/' + invalid)[0] == 404, 'Rejects unlisted observation download ' + invalid)
            camera_path = original_folder / 'sample-00-head.png'
            camera_path.chmod(0o600);camera_path.write_bytes(b'corrupt-original');camera_path.chmod(0o400)
            check(request(app_socket, '/robot/api/' + capture_prefix + '/files/sample-00-head.png')[0] == 404,
                  'Native download rejects changed original camera bytes')
            camera_path.chmod(0o600);camera_path.write_bytes(PNG);camera_path.chmod(0o400)
            original_folder.chmod(0o700);camera_path.unlink()
            camera_path.symlink_to(state / capture['backupRelativePath'] / 'sample-00-head.png');original_folder.chmod(0o500)
            check(request(app_socket, '/robot/api/' + capture_prefix + '/files/sample-00-head.png')[0] == 404,
                  'Native download rejects a symlink even when its target contains identical preserved pixels')
            original_folder.chmod(0o700);camera_path.unlink();camera_path.write_bytes(PNG);camera_path.chmod(0o400);original_folder.chmod(0o500)
            status, _, data = post('auto-calibrate', {})
            check(status == 202 and finished(json.loads(data)['id'])['state'] == 'completed',
                  'Automatic calibration completes a read-only assessment')
            assessment = api('calibration-assessment')
            check(assessment['state'] == 'blocked' and not assessment['jointCalibrationApproved']
                  and not assessment['motorCommandsIssued'] and len(assessment['phases']) == 6 and assessment['evidence']['backupVerified'],
                  'Automatic calibration collects unmarked observations and reports all unfinished phases without fabricating approval')
            latest_capture = api('calibration-observations')
            check(latest_capture['id'] == assessment['evidence']['id'] and latest_capture['id'] != capture['id']
                  and len([json.loads(line) for line in calls_path.read_text().splitlines() if json.loads(line)['operation'] == 'inspect-markers']) == 1,
                  'Automatic calibration uses camera/encoder acquisition without requiring another tag survey')
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
            check(api('emote-files')[0]['key'] == key
                  and request(app_socket, '/robot/api/emote-files/'+key+'/preview')[2] == video,
                  'Imported emote and its verified video survive a native app restart')
            check(restarted['configured'] and restarted['emergencyStopLatched'] and restarted['stopLatched']
                  and restarted['armedUntil'] is None and api('configuration') == configuration,
                  'Container restart preserves settings and E-stop while never resuming a motion session')
            check(api('backups/settings') == {'url': 'https://example.invalid:9443/', 'tokenStored': True},
                  'Container restart retains private receiver configuration without exposing its token')
            check(api('calibration-observations')['id'] == latest_capture['id']
                  and request(app_socket, '/robot/api/' + capture_prefix + '/manifest')[2] == manifest_bytes,
                  'Restart retains latest observation receipt and all prior sealed originals independently of jobs')
            status, _, data = post('reset-estop', {})
            check(status == 202 and finished(json.loads(data)['id'])['state'] == 'completed',
                  'E-stop reset checks fresh feedback from all seventeen fixture motors')
            check(not api('status')['emergencyStopLatched'] and api('status')['stopLatched']
                  and not (state / 'estop-latched').exists(), 'Checked reset retains disarmed state')
            calls = [json.loads(line) for line in calls_path.read_text().splitlines()]
            check(calls and all(c['operation'] in ['camera', 'dashboard', 'inspect-markers', 'prepare', 'capture-calibration-observation'] for c in calls)
                  and len([c for c in calls if c['operation'] == 'capture-calibration-observation']) == 2,
                  'Adapter calls use observations or filesystem preparation only; no motor operation is issued')
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
