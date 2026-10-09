#!/usr/bin/env python3
"""Verify the exact Native AOT receiver API without robot hardware or models."""
import argparse
import hashlib
import http.client
import json
import os
from pathlib import Path
import socket
import subprocess
import tempfile
import time

ROOT = Path(__file__).resolve().parents[2]


def main():
    parser = argparse.ArgumentParser()
    target = parser.add_mutually_exclusive_group(required=True)
    target.add_argument('--binary', type=Path)
    target.add_argument('--container')
    parser.add_argument('--engine', choices=['docker', 'podman'], default='docker')
    options = parser.parse_args()
    passed = []
    def check(value, label):
        if not value:
            raise AssertionError(label)
        passed.append(label)
    evidence = ROOT / '.build/evidence/recording-backups-native'
    evidence.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='backup-api-', dir=ROOT / '.build') as temporary:
        directory = Path(temporary)
        state = directory / 'state'
        state.mkdir()
        token_path = directory / 'token'
        token_path.write_text('t' * 64)
        token_path.chmod(0o600)
        with socket.socket() as available:
            available.bind(('127.0.0.1', 0))
            port = available.getsockname()[1]
        name = 'xur-backup-test-' + str(os.getpid())
        log = (evidence / 'app.log').open('w')
        process = None
        container_running = False
        def start():
            nonlocal process, container_running
            if options.binary:
                binary = options.binary.resolve()
                process = subprocess.Popen([str(binary)], cwd=binary.parent,
                    env=dict(os.environ, XUR_BACKUP_LISTEN=f'http://127.0.0.1:{port}',
                             XUR_BACKUP_STATE=str(state), XUR_BACKUP_TOKEN_FILE=str(token_path)),
                    stdout=log, stderr=subprocess.STDOUT)
            else:
                subprocess.run([options.engine, 'run', '--detach', '--name', name, '--pull=never',
                    '--cap-drop=ALL', '--security-opt=no-new-privileges', '--read-only',
                    '--memory=256m', '--pids-limit=128', '--tmpfs=/tmp:rw,nosuid,nodev,size=16m',
                    '--publish', f'127.0.0.1:{port}:7081', '--volume', str(state) + ':/backup:rw',
                    '--volume', str(token_path) + ':/run/secrets/backup-token:ro', options.container],
                    check=True, stdout=log, stderr=subprocess.STDOUT)
                container_running = True
            for _ in range(100):
                try:
                    if request('/health', authenticated=False)[0] == 200:
                        return
                except (OSError, http.client.HTTPException):
                    pass
                time.sleep(.1)
            raise AssertionError('Receiver failed to become ready; inspect ' + str(evidence / 'app.log'))
        def stop():
            nonlocal process, container_running
            if process:
                process.terminate()
                try:
                    process.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait()
                process = None
            if container_running:
                subprocess.run([options.engine, 'logs', name], stdout=log, stderr=subprocess.STDOUT)
                subprocess.run([options.engine, 'stop', '--time', '10', name], check=True, stdout=log, stderr=subprocess.STDOUT)
                subprocess.run([options.engine, 'rm', name], check=True, stdout=log, stderr=subprocess.STDOUT)
                container_running = False
        def request(route, method='GET', body=None, authenticated=True, content_type='application/json'):
            headers = {'Content-Type': content_type}
            if authenticated:
                headers['Authorization'] = 'Bearer ' + 't' * 64
            connection = http.client.HTTPConnection('127.0.0.1', port, timeout=10)
            try:
                connection.request(method, route, body=body, headers=headers)
                response = connection.getresponse()
                return response.status, response.read()
            finally:
                connection.close()
        def encoded(value):
            return json.dumps(value, separators=(',', ':')).encode()
        try:
            if options.binary:
                check(options.binary.resolve().read_bytes()[:4] == b'\x7fELF', 'Receiver is an actual native ELF executable')
                check(not (options.binary.resolve().parent / 'libcoreclr.so').exists(), 'Receiver contains no CoreCLR/JIT')
            start()
            check(b'native-aot' in request('/health', authenticated=False)[1], 'Independent Native AOT receiver starts without host or robot sockets')
            check(request('/api/snapshots/' + 'a' * 64, authenticated=False)[0] == 401, 'Artifact API rejects missing receiver credentials')
            content = b'preserved-original-sample'
            digest = hashlib.sha256(content).hexdigest()
            manifest = dict(version=1, recordingId='a' * 32, dataset='demo', robotId='robot', arm='right',
                task='Sort block', completedAt='2026-10-09T00:00:00+00:00',
                files=[dict(path='data/sample.parquet', size=len(content), sha256=digest)], provenance=None, outcome='completed')
            conflicting = dict(manifest, files=[dict(path=path, size=len(content), sha256=digest) for path in ('a', 'a-z', 'a/b')])
            conflicting_id = hashlib.sha256(encoded(conflicting)).hexdigest()
            check(request('/api/snapshots/' + conflicting_id + '/manifest', 'POST', encoded(conflicting))[0] == 409,
                  'Native receiver rejects nonadjacent file-directory prefix conflicts before upload')
            snapshot = hashlib.sha256(encoded(manifest)).hexdigest()
            route = '/api/snapshots/' + snapshot
            status, data = request(route + '/manifest', 'POST', encoded(manifest))
            check(status == 200 and json.loads(data)['missingBlobs'] == [digest], 'Native serializer accepts the canonical manifest and declares missing content')
            check(request(route + '/commit', 'POST', b'{}')[0] == 409, 'Incomplete manifests cannot become verified datasets')
            check(request(route + '/blobs/' + digest, 'PUT', b'corrupt', content_type='application/octet-stream')[0] == 409,
                  'Streaming uploads reject a corrupt checksum or length')
            check(request(route + '/blobs/' + 'b' * 64, 'PUT', content, content_type='application/octet-stream')[0] == 409,
                  'Uploads cannot add undeclared content')
            check(request(route + '/blobs/' + digest, 'PUT', content, content_type='application/octet-stream')[0] == 204,
                  'Declared sample bytes are streamed to content-addressed storage')
            status, data = request(route + '/commit', 'POST', b'{}')
            receipt = json.loads(data)
            check(status == 200 and receipt['state'] == 'verified' and receipt['verifiedAt'] and not receipt['missingBlobs'],
                  'Atomic commit reports verification only after the complete sample hashes match')
            check((state / 'snapshots' / snapshot / 'dataset/data/sample.parquet').read_bytes() == content,
                  'Materialized training data retains original bytes and relative names')
            check(request(route + '/manifest', 'POST', encoded(dict(manifest, motors={'goal': 180})))[0] == 409,
                  'Manifest API rejects unknown fields under Native AOT')
            check(request(route + '/commit', 'POST', b'{"force":true}')[0] == 409, 'Commit cannot bypass verification with force fields')
            check(request(route + '/manifest', 'POST', b'x' * (2 * 1024 * 1024 + 1))[0] == 413, 'Manifest metadata has a bounded request size')
            check(request(route + '/manifest', 'POST', b'{}', content_type='text/plain')[0] == 415, 'Manifest uploads require JSON')
            check(request('/api/delete', 'POST', b'{}')[0] == 404 and request(route, 'DELETE')[0] == 405,
                  'Receiver exposes no deletion or cleanup API')
            stop()
            start()
            status, data = request(route)
            check(status == 200 and json.loads(data)['state'] == 'verified', 'Receiver restart verifies durable persisted samples and receipts')
            check((state / 'snapshots' / snapshot / 'dataset/data/sample.parquet').read_bytes() == content,
                  'Restart never alters a committed recording')
        finally:
            stop()
            log.close()
            if not any('Restart never alters' in label for label in passed):
                print((evidence / 'app.log').read_text())
    receipt = dict(suite='NativeRecordingBackupReceiver', passed=passed, hardware='None')
    (evidence / 'validation.json').write_text(json.dumps(receipt, indent=2) + '\n')
    print(json.dumps(receipt))


if __name__ == '__main__':
    main()
