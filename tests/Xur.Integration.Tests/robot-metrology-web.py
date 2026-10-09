#!/usr/bin/env python3
"""Exact Native AOT app + pinned native estimator, synthetic cameras only."""
import argparse
import importlib.util
import json
import math
import os
from pathlib import Path
import shlex
import subprocess
import sys
import tempfile
import time

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('robot_web', Path(__file__).with_name('robot-web.py'))
web = importlib.util.module_from_spec(spec)
spec.loader.exec_module(web)

BRIDGE = '''import json,os,sys
from datetime import datetime,timedelta,timezone
if len(sys.argv)>1:
    if sys.argv[1] not in ["--idle","--stop"]: raise SystemExit(2)
    raise SystemExit(0)
request=json.loads(sys.stdin.readline())
with open(os.environ["XUR_ROBOT_TEST_CALLS"],"a") as calls:
    calls.write(json.dumps(request)+"\\n")
if request["operation"]!="inspect-markers": raise SystemExit("Unexpected operation")
fixture=json.load(open(os.environ["XUR_ROBOT_POSE_FIXTURE"]))
now=datetime.now(timezone.utc)
def camera(name):
    return {"name":name,"jpeg":"/9j/2Q==","frames":[{
        "capturedAt":(now+timedelta(milliseconds=i)).isoformat(),"width":640,"height":480,
        "detections":[{"id":0,"hamming":0,"decisionMargin":100,
        "center":[sum(p[0] for p in fixture["pixels"])/4,sum(p[1] for p in fixture["pixels"])/4],
        "corners":fixture["pixels"]}]} for i in range(3)]}
print(json.dumps({"family":"tagStandard41h12","detectorSha256":"b"*64,"cameras":[camera("head"),camera("hand")]}))
'''


def fixture(angle=40, distance=.35, distorted=True):
    radians = math.radians(angle)
    rotation = [math.cos(radians), 0, math.sin(radians), 0, 1, 0, -math.sin(radians), 0, math.cos(radians)]
    translation = [.03, -.01, distance] if distance < 1 else [0, 0, distance]
    coefficients = [-.15, .03, .001, -.002, .005] if distorted else []
    pixels = []
    for point in [(-.025, .025, 0), (.025, .025, 0), (.025, -.025, 0), (-.025, -.025, 0)]:
        transformed = [sum(rotation[row * 3 + column] * point[column] for column in range(3)) + translation[row] for row in range(3)]
        x, y = transformed[0] / transformed[2], transformed[1] / transformed[2]
        if distorted:
            k1, k2, p1, p2, k3 = coefficients
            r2 = x*x + y*y
            radial = 1 + k1*r2 + k2*r2*r2 + k3*r2*r2*r2
            x, y = x*radial + 2*p1*x*y + p2*(r2+2*x*x), y*radial + p1*(r2+2*y*y) + 2*p2*x*y
        pixels.append([800*x+320, 810*y+240])
    return dict(pixels=pixels, trueTranslation=translation), coefficients


def main():
    parser = argparse.ArgumentParser()
    target = parser.add_mutually_exclusive_group(required=True)
    target.add_argument('--binary', type=Path)
    target.add_argument('--container')
    parser.add_argument('--helper', type=Path, help='Required for a native binary test; container uses its installed helper.')
    parser.add_argument('--engine', choices=['docker', 'podman'], default='docker')
    options = parser.parse_args()
    if options.binary and not options.helper:
        parser.error('--binary requires the compiled pinned --helper')
    if options.binary:
        options.binary = options.binary.resolve()
        options.helper = options.helper.resolve()
    if options.container and os.geteuid() != 0:
        raise SystemExit('Use sudo for the production root-owned private-socket container test.')
    evidence = ROOT / '.build/evidence/metrology'
    evidence.mkdir(parents=True, exist_ok=True)
    passed = []
    def check(condition, label):
        if not condition:
            raise AssertionError(label)
        passed.append(label)
    with tempfile.TemporaryDirectory(prefix='metrology-', dir=ROOT / '.build') as temporary:
        directory = Path(temporary)
        state, tools = directory / 'state', directory / 'tools'
        state.mkdir()
        tools.mkdir()
        (tools / 'bridge.py').write_text(BRIDGE)
        helper = str(options.helper.resolve()) if options.binary else '/opt/xur/estimate-marker-pose'
        (tools / 'estimate-marker-pose').write_text('#!/bin/sh\nexec ' + shlex.quote(helper) + ' "$@"\n')
        (tools / 'estimate-marker-pose').chmod(0o700)
        observations, coefficients = fixture()
        fixture_path = directory / 'fixture.json'
        fixture_path.write_text(json.dumps(observations))
        calls_path = directory / 'calls.jsonl'
        calls_path.touch()
        # The file still lives in ignored state. /proc/self/cwd gives Kestrel an
        # absolute path below Linux's 108-byte limit in nested isolated worktrees.
        previous_directory = Path.cwd()
        os.chdir(directory)
        app_socket = Path('app.sock')
        name = 'xur-metrology-test-' + str(os.getpid())
        log = (evidence / 'app.log').open('w')
        process = None
        active_container = False
        def api(route, method='GET', body=None):
            status, _, data = web.request(app_socket, '/robot/api/' + route, method,
                json.dumps(body).encode() if body is not None else None,
                {'Content-Type': 'application/json'} if body is not None else {})
            return status, json.loads(data) if data else None
        def start():
            nonlocal process, active_container
            if options.binary:
                binary = options.binary.resolve()
                environment = dict(os.environ, XUR_ROBOT_SOCKET='/proc/self/cwd/app.sock', XUR_ROBOT_STATE=str(state), ASPNETCORE_CONTENTROOT=str(binary.parent),
                    XUR_ROBOT_TOOLS=str(tools), XUR_ROBOT_PYTHON=sys.executable, XUR_ROBOT_WORKLOAD_ID='metrology-test',
                    XUR_ROBOT_TEST_CALLS=str(calls_path), XUR_ROBOT_POSE_FIXTURE=str(fixture_path),
                    PYTHONPYCACHEPREFIX=str(directory / 'pycache'))
                environment.pop('XUR_ROBOT_DEV_URL', None)
                environment.pop('XUR_AGENT_SOCKET', None)
                process = subprocess.Popen([str(binary)], cwd=directory, env=environment, stdout=log, stderr=subprocess.STDOUT)
            else:
                subprocess.run([options.engine, 'run', '--detach', '--name', name, '--pull=never',
                    '--network=none', '--cap-drop=ALL', '--security-opt=no-new-privileges', '--security-opt=label=disable',
                    '--read-only', '--memory=512m', '--cpus=1', '--pids-limit=128',
                    '--tmpfs=/tmp:rw,nosuid,nodev,size=32m', '--volume', str(directory) + ':/test:rw',
                    '--env', 'XUR_ROBOT_SOCKET=/test/app.sock', '--env', 'XUR_ROBOT_STATE=/test/state',
                    '--env', 'XUR_ROBOT_TOOLS=/test/tools', '--env', 'XUR_ROBOT_PYTHON=python3',
                    '--env', 'XUR_ROBOT_WORKLOAD_ID=metrology-test', '--env', 'XUR_ROBOT_TEST_CALLS=/test/calls.jsonl',
                    '--env', 'XUR_ROBOT_POSE_FIXTURE=/test/fixture.json', '--env', 'PYTHONPYCACHEPREFIX=/test/pycache',
                    options.container], check=True, stdout=log, stderr=subprocess.STDOUT)
                active_container = True
            for _ in range(100):
                try:
                    if web.request(app_socket, '/robot/health')[0] == 200:
                        return
                except OSError:
                    pass
                time.sleep(.1)
            raise AssertionError('App startup failed; see ' + str(evidence / 'app.log'))
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
                subprocess.run([options.engine, 'stop', '--time', '10', name], check=True, stdout=log, stderr=subprocess.STDOUT)
                subprocess.run([options.engine, 'rm', name], check=True, stdout=log, stderr=subprocess.STDOUT)
                active_container = False
            app_socket.unlink(missing_ok=True)
        def scan():
            status, job = api('tasks', 'POST', {'kind': 'inspect-markers'})
            check(status == 202, 'Read-only marker observation accepted')
            for _ in range(150):
                status, result = api('jobs/' + job['id'])
                if result['state'] != 'running':
                    check(status == 200 and result['state'] == 'completed', 'Synthetic observation validates through the real adapter boundary')
                    return api('jobs/' + job['id'] + '/markers')[1]
                time.sleep(.05)
            raise AssertionError('Marker job did not finish')
        configuration = dict(robotId='fixture', leftPort='/dev/serial/by-id/left', rightPort='/dev/serial/by-id/right',
            controllerDevice='', headCamera='/dev/v4l/by-id/head-video-index0', handCamera='/dev/v4l/by-id/hand-video-index0',
            skills=[], motionEnabled=False, motorLimits=dict(maxLoadRaw=100, maxCurrentRaw=100, maxFollowingErrorDegrees=2))
        settings = dict(version=1, cameras=[dict(name=role, device=configuration[role+'Camera'],
            width=640, height=480, fx=800, fy=810, cx=320, cy=240, distortionModel='brown-conrady-5',
            distortion=coefficients, measurementSource='Synthetic offline fixture; not xur-255 metrology') for role in ['head', 'hand']],
            tags=[dict(id=0, referenceEdgeMeters=.05, measurementSource='Synthetic reference edge')], maximumReprojectionRmsPixels=2,
            minimumCandidateSeparationPixels=.5)
        try:
            start()
            check(api('metrology') == (200, None), 'No measured metrology is synthesized at startup')
            check(api('configure', 'POST', configuration)[0] == 200, 'Fixture camera selection configured')
            check('metric' not in scan(), 'Unconfigured metrology preserves the pixel-only report schema')
            check(api('metrology', 'POST', dict(settings, motorGoal=90))[0] == 400, 'AOT schema rejects attempted motor fields')
            bad = json.loads(json.dumps(settings))
            bad['cameras'][0]['distortion'] = coefficients[:4]
            check(api('metrology', 'POST', bad)[0] == 409, 'Unsupported coefficient lengths cannot be saved')
            check(api('metrology', 'POST', settings) == (200, settings), 'Source-generated metrology persists exact supplied numbers and provenance')
            report = scan()
            check(report['metric']['measurementSettings'] == settings and len(report['metric']['settingsSha256']) == 64,
                  'Pose evidence retains the exact supplied measurements and provenance alongside their hash')
            for camera in report['metric']['cameras']:
                check(camera['state'] == 'estimated' and all(t['state'] == 'estimated' for t in camera['tags']),
                      camera['name'] + ': distorted raw corners yield a resolved visual pose')
                for tag in camera['tags']:
                    pose = tag['candidates'][tag['preferredCandidate']]
                    check(pose['reprojectionRmsPixels'] < .001 and max(abs(a-b) for a,b in zip(pose['translationMeters'], observations['trueTranslation'])) < .00005,
                          camera['name'] + ': full .NET inversion/native estimation/raw reprojection recovers known geometry')
            check(not report['motorCommandsIssued'] and not report['jointCalibrationApproved'] and not report['metric']['jointCalibrationApproved'],
                  'Metric visual candidates do not approve joints or motion')
            wrong_resolution = json.loads(json.dumps(settings))
            for camera in wrong_resolution['cameras']:
                camera['width'] = 1280
            api('metrology', 'POST', wrong_resolution)
            check(all(c['state'] == 'unavailable' and not c['tags'] for c in scan()['metric']['cameras']),
                  'Exact measured resolution is required through the AOT API')
            observations, _ = fixture(angle=5, distance=2, distorted=False)
            fixture_path.write_text(json.dumps(observations))
            for camera in settings['cameras']:
                camera.update(distortionModel='none', distortion=[])
            api('metrology', 'POST', settings)
            ambiguous = scan()
            check(all(t['state'] == 'ambiguous' and t['preferredCandidate'] is None and len(t['candidates']) == 2
                      for c in ambiguous['metric']['cameras'] for t in c['tags']),
                  'Weak-perspective real native candidates remain ambiguous in the AOT report')
            stop()
            start()
            check(api('metrology') == (200, settings), 'Metrology survives an app restart')
            status = api('status')[1]
            check(status['stopLatched'] and status['mode'] == 'disarmed' and not (state / 'calibration/receipt.json').exists(),
                  'Restart and measurement configuration never arm or create a calibration receipt')
            pixel_only = dict(version=1, cameras=[], tags=[], maximumReprojectionRmsPixels=2, minimumCandidateSeparationPixels=.5)
            api('metrology', 'POST', pixel_only)
            check('metric' not in scan(), 'Pixel-only mode is restored without deleting raw observations')
            check(all(json.loads(line)['operation'] == 'inspect-markers' for line in calls_path.read_text().splitlines()),
                  'The exact application performs only camera observations; no motor operations')
        finally:
            stop()
            log.close()
            os.chdir(previous_directory)
    receipt = dict(suite='RobotNativeAotMetrology', scope='Synthetic camera measurements and actual pinned native estimator; no robot hardware', passed=passed)
    (evidence / 'api-validation.json').write_text(json.dumps(receipt, indent=2) + '\n')
    print(json.dumps(receipt))


if __name__ == '__main__':
    main()
