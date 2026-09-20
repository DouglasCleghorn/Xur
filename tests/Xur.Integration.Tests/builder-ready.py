#!/usr/bin/env python3
"""Verify builder initialization handling without starting a VM."""
import importlib.util
import io
import json
import pathlib
import subprocess
from contextlib import redirect_stdout, redirect_stderr
from unittest.mock import patch

repo = pathlib.Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('builder_ready', repo / 'eng/builder_ready.py')
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


def check(wait_code, status_code, body, accepted):
    commands = []
    replies = iter([
        subprocess.CompletedProcess([], wait_code),
        subprocess.CompletedProcess([], status_code, body, 'fixture diagnostic'),
    ])
    def run(command, **kwargs):
        commands.append(command)
        return next(replies)
    output = io.StringIO()
    with patch.object(module.subprocess, 'run', run), redirect_stdout(output), redirect_stderr(output):
        try:
            module.wait_for_cloud_init(['ssh', 'builder@fixture'])
        except RuntimeError:
            assert not accepted, output.getvalue()
        else:
            assert accepted, 'Unhealthy builder was accepted'
    assert commands == [
        ['ssh', 'builder@fixture', 'sudo -n cloud-init status --wait'],
        ['ssh', 'builder@fixture', 'sudo -n cloud-init status --format json'],
    ], commands
    assert 'fixture diagnostic' in output.getvalue()
    return output.getvalue()


done = json.dumps({'status': 'done', 'errors': [], 'recoverable_errors': {}})
check(0, 0, done, True)
degraded = json.dumps({'status': 'done', 'errors': [], 'recoverable_errors': {'WARNING': ['fixture warning']}})
output = check(2, 2, degraded, True)
assert 'fixture warning' in output and 'Continuing with Fedora toolchain' in output
check(1, 0, done, False)
check(0, 1, done, False)
check(255, 0, done, False)
check(0, 255, '', False)
check(2, 2, '', False)
check(0, 0, '[]', False)
for status in ('running', 'disabled', 'error', None):
    check(0, 0, json.dumps({'status': status}), False)
check(2, 2, json.dumps({'status': 'done', 'errors': ['package installation failed']}), False)
print(json.dumps({'suite': 'BuilderReady', 'privilegedStatus': True,
                  'recoverableWarningsVisible': True, 'fatalAndMalformedStatusRejected': True}))
