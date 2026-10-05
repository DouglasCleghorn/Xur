#!/usr/bin/env python3
"""Real HTTPS session persistence and fresh login-token checks; no saved credentials."""
import base64
import hashlib
import http.client
import json
import os
import pathlib
import re
import secrets
import shutil
import signal
import socket
import ssl
import subprocess
import tempfile
import time
import urllib.parse
from http.cookies import SimpleCookie

repo = pathlib.Path(__file__).resolve().parents[2]
evidence = repo / '.build/evidence/auth'
evidence.mkdir(parents=True, exist_ok=True)
with tempfile.TemporaryDirectory(prefix='sessions-', dir=evidence) as tmp:
    root = pathlib.Path(tmp)
    state, run = root / 'state', root / 'run'
    (root / 'bin').mkdir()
    stub = root / 'bin/tailscale'
    stub.write_text('#!/bin/sh\necho \'{"BackendState":"NeedsLogin"}\'\n')
    stub.chmod(0o700)
    env = dict(os.environ, XUR_RUN=str(run), XUR_STATE=str(state), XUR_MODE='Installed',
               XUR_PORT='18810', XUR_CONSOLE='stdio', PATH=str(root / 'bin') + ':' + os.environ['PATH'])
    binary = os.environ.get('XUR_CONTROL_BINARY', str(repo / '.build/context/publish/control/Xur.Control'))
    process = None
    cookies = {}

    def request(method, path, data=None, headers=None):
        headers = dict(headers or {})
        if cookies:
            headers['Cookie'] = '; '.join(name + '=' + value for name, value in cookies.items())
        connection = http.client.HTTPSConnection('127.0.0.1', 19173, context=ssl._create_unverified_context(), timeout=5)
        try:
            connection.request(method, path, body=data, headers=headers)
            response = connection.getresponse()
            body = response.read()
            fields = response.getheaders()
            for name, value in fields:
                if name.lower() == 'set-cookie':
                    for cookie in SimpleCookie(value).values():
                        if cookie['max-age'] == '0' or not cookie.value:
                            cookies.pop(cookie.key, None)
                        else:
                            cookies[cookie.key] = cookie.value
            return response.status, dict(fields), body
        finally:
            connection.close()

    def start():
        global process
        run.mkdir(parents=True, exist_ok=True)
        process = subprocess.Popen([binary], env=env, stdin=subprocess.PIPE,
                                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, start_new_session=True)
        for _ in range(100):
            try:
                if request('GET', '/health')[0] == 200:
                    return
            except OSError:
                pass
            if process.poll() is not None:
                raise AssertionError('Manager exited before HTTPS became available')
            time.sleep(.1)
        raise AssertionError('Manager did not start')

    def stop():
        global process
        if process and process.poll() is None:
            os.killpg(process.pid, signal.SIGTERM)
            process.wait(timeout=5)
        process = None

    def token():
        status, headers, body = request('GET', '/auth/login-token', headers={'Accept': 'application/json'})
        assert status == 200 and 'no-store' in headers['Cache-Control']
        assert 'Content-Encoding' not in headers
        return json.loads(body)

    def form(path, values, csrf, accept=None):
        headers = {'Content-Type': 'application/x-www-form-urlencoded', 'Origin': 'https://127.0.0.1:19173'}
        if accept:
            headers['Accept'] = accept
        return request('POST', path, urllib.parse.urlencode({**values, csrf['fieldName']: csrf['requestToken']}),
                       headers)

    def payload(value):
        encoded = value.split('.')[1]
        return json.loads(base64.urlsafe_b64decode(encoded + '=' * (-len(encoded) % 4)))

    try:
        start()
        local = http.client.HTTPConnection('localhost')
        local.sock = socket.socket(socket.AF_UNIX)
        local.sock.connect(str(run / 'control.sock'))
        local.request('GET', '/local/login')
        code = re.search(r'Access code: ([0-9A-Z-]+)', local.getresponse().read().decode())[1]
        local.close()
        csrf = token()
        assert form('/auth/login', {'code': code}, csrf)[0] == 302
        assert payload(cookies['xur.session'])['purpose'] == 'bootstrap'
        password = secrets.token_urlsafe(24)
        credentials = {'username': 'session-owner', 'password': password}
        status, headers, _ = form('/auth/setup', credentials, token())
        assert status == 302 and headers['Location'] == '/'
        remembered = cookies['xur.session']
        assert payload(remembered)['purpose'] == 'manager-browser' and 'exp' not in payload(remembered)
        assert 'max-age=34560000' in headers['Set-Cookie']
        assert request('GET', '/api/api-keys')[0] == 200
        authenticated_token = token()
        key_paths = [state / 'session-signing.key', state / 'manager-tls.pfx', *sorted((state / 'form-keys').glob('*.xml'))]
        keys = {path.name: hashlib.sha256(path.read_bytes()).digest() for path in key_paths}
        assert len(keys) >= 3
        stop()
        shutil.rmtree(run)
        start()
        assert request('GET', '/api/api-keys')[0] == 200
        assert cookies['xur.session'] == remembered
        assert keys == {path.name: hashlib.sha256(path.read_bytes()).digest() for path in key_paths}
        assert request('GET', '/login')[1]['Location'] == '/'
        assert form('/auth/logout', {}, authenticated_token)[0] == 302 and 'xur.session' not in cookies
        # A page restored after the CSRF cookie disappears cannot submit its old
        # token, but a fresh token lets it submit the same credentials successfully.
        stale = token()
        cookies.pop('xur.csrf.https')
        assert form('/auth/login', credentials, stale)[1]['Location'] == '/login?error=refresh'
        fresh = token()
        assert form('/auth/login', credentials, fresh)[1]['Location'] == '/'
        # A token from before login no longer matches the authenticated identity.
        assert form('/auth/login', credentials, fresh)[1]['Location'] == '/login?error=refresh'
        assert form('/auth/login', credentials, token())[1]['Location'] == '/'
        assert request('GET', '/auth/login-token', headers={'Origin': 'https://evil.example'})[0] == 403
        assert request('GET', '/auth/login-token', headers={'Sec-Fetch-Site': 'cross-site'})[0] == 403
        assert request('POST', '/auth/logout')[0] == 400
        # API login remains an eight-hour bearer exchange and never sets a cookie.
        status, headers, body = request('POST', '/api/auth/login', json.dumps(credentials), {'Content-Type': 'application/json'})
        api = json.loads(body)
        assert status == 200 and api['expiresIn'] == 28800 and 'Set-Cookie' not in headers
        assert payload(api['accessToken'])['purpose'] == 'manager' and 'exp' in payload(api['accessToken'])
        # Still-valid cookies from an older bundle upgrade on first authenticated GET.
        cookies['xur.session'] = api['accessToken']
        assert request('GET', '/api/api-keys')[0] == 200
        assert payload(cookies['xur.session'])['purpose'] == 'manager-browser'
        assert form('/auth/logout', {}, token())[0] == 302 and 'xur.session' not in cookies
        assert request('GET', '/api/api-keys')[0] == 401
        # The explicit browser-save path confirms successful password login;
        # failed credentials and missing CSRF cannot issue a persistent session.
        status, _, body = form('/auth/login', {**credentials, 'password': 'wrong password'}, token(), 'application/json')
        assert status == 401 and 'error' in json.loads(body) and 'xur.session' not in cookies
        status, _, _ = request('POST', '/auth/login', urllib.parse.urlencode(credentials),
                               {'Content-Type': 'application/x-www-form-urlencoded', 'Accept': 'application/json'})
        assert status == 302 and 'xur.session' not in cookies
        status, headers, body = form('/auth/login', credentials, token(), 'application/json')
        assert status == 200 and json.loads(body) == {'signedIn': True, 'redirectTo': '/'}
        assert 'max-age=34560000' in headers['Set-Cookie'] and 'Content-Encoding' not in headers
        assert request('GET', '/api/api-keys')[0] == 200
        assert form('/auth/logout', {}, token())[0] == 302 and 'xur.session' not in cookies
    finally:
        stop()

print(json.dumps({'suite': 'BrowserSessions', 'result': 'Passed', 'persistentSignup': True,
                  'persistentLogin': True, 'rebootStatePreserved': True, 'freshLoginTokens': True,
                  'crossOriginDenied': True, 'csrfRequired': True, 'apiExpiryUnchanged': True,
                  'oldCookieUpgrade': True, 'browserSaveConfirmation': True, 'signOut': True}))
