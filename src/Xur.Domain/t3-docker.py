"""Persistent, signed upstream Docker packages and a rootless DinD supervisor."""
import fcntl
import hashlib
import os
from pathlib import Path
import signal
import subprocess
import tempfile
import time

PACKAGES = ('docker-ce', 'docker-ce-cli', 'containerd.io', 'docker-ce-rootless-extras',
            'docker-buildx-plugin', 'docker-compose-plugin')
ROOT = Path.home() / '.local/share/xur-docker'


def checked(args, **kwargs):
    return subprocess.run(args, check=True, timeout=180, **kwargs)


def update():
    ROOT.mkdir(parents=True, exist_ok=True)
    with (ROOT / 'update.lock').open('a') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        # APT verifies the signed Release metadata and each package's checksum.
        # Give it private state rather than writing the container's package DB.
        for part in ('apt/lists/partial', 'apt/cache/archives/partial', 'versions'):
            (ROOT / part).mkdir(parents=True, exist_ok=True)
        key = ROOT / 'docker.asc'
        checked(['curl', '--fail', '--silent', '--show-error', '--location',
                 '--retry', '2', '--connect-timeout', '10', '--max-time', '30',
                 'https://download.docker.com/linux/debian/gpg', '-o', str(key) + '.tmp'])
        os.replace(str(key) + '.tmp', key)
        release = dict(line.split('=', 1) for line in Path('/etc/os-release').read_text().splitlines() if '=' in line)
        distro = release['ID'].strip('"')
        if distro not in ('debian', 'ubuntu'):
            raise RuntimeError('T3 Docker requires the Debian/Ubuntu base image.')
        codename = release['VERSION_CODENAME'].strip('"')
        source = ROOT / 'docker.list'
        source.write_text(f'deb [signed-by={key}] https://download.docker.com/linux/{distro} {codename} stable\n')
        status = ROOT / 'apt/status'
        status.touch()
        options = ['-o', f'Dir::State={ROOT / "apt"}', '-o', f'Dir::State::status={status}',
                   '-o', f'Dir::Cache={ROOT / "apt/cache"}', '-o', f'Dir::Etc::sourcelist={source}',
                   '-o', 'Dir::Etc::sourceparts=-', '-o', 'APT::Get::List-Cleanup=0',
                   '-o', 'Acquire::Retries=2', '-o', 'Acquire::https::Timeout=20']
        checked(['apt-get', *options, 'update', '--error-on=any'])
        selected = checked(['apt-get', *options, '--print-uris', 'download', *PACKAGES],
                           capture_output=True, text=True).stdout
        if not selected.strip():
            raise RuntimeError('Docker package selection was empty.')
        version = ROOT / 'versions' / hashlib.sha256(selected.encode()).hexdigest()
        if not version.exists():
            with tempfile.TemporaryDirectory(prefix='.download-', dir=ROOT) as temporary:
                stage = Path(temporary)
                checked(['apt-get', *options, 'download', *PACKAGES], cwd=stage)
                archives = list(stage.glob('*.deb'))
                if len(archives) != len(PACKAGES):
                    raise RuntimeError('Docker package download was incomplete; saved runtime retained.')
                unpacked = stage / 'runtime'
                for archive in archives:
                    checked(['dpkg-deb', '--extract', str(archive), str(unpacked)])
                for binary in ('docker', 'dockerd', 'containerd', 'runc', 'rootlesskit', 'dockerd-rootless.sh'):
                    if not os.access(unpacked / 'usr/bin' / binary, os.X_OK):
                        raise RuntimeError('Docker runtime is missing ' + binary)
                checked([str(unpacked / 'usr/bin/docker'), '--version'])
                checked([str(unpacked / 'usr/bin/dockerd'), '--version'])
                # Retain the extracted distributions, including copyright notices.
                os.rename(unpacked, version)
        plugins = Path.home() / '.docker/cli-plugins'
        plugins.mkdir(parents=True, exist_ok=True)
        for name in ('docker-buildx', 'docker-compose'):
            candidates = [version / base / 'docker/cli-plugins' / name for base in ('usr/libexec', 'usr/lib')]
            target = next((p for p in candidates if os.access(p, os.X_OK)), None)
            if target is None:
                raise RuntimeError('Docker runtime is missing ' + name)
            # Keep manually installed plugins; update only links owned by Xur.
            alias = plugins / name
            if not (alias.exists() or alias.is_symlink()) or alias.is_symlink() and str(alias.readlink()).startswith(str(ROOT) + '/'):
                replacement = plugins / (name + '.xur-tmp')
                replacement.unlink(missing_ok=True)
                replacement.symlink_to(ROOT / 'current' / target.relative_to(version))
                os.replace(replacement, alias)
        replacement = ROOT / 'current.tmp'
        replacement.unlink(missing_ok=True)
        replacement.symlink_to(version)
        os.replace(replacement, ROOT / 'current')


def run(command):
    # Bound installation/update work, retain the last complete selection offline.
    ROOT.mkdir(parents=True, exist_ok=True)
    updater = subprocess.Popen(['timeout', '--foreground', '--kill-after=5s', '240s',
                               'python3', '-c', SOURCE + '''
try:
    update()
except (OSError, RuntimeError, subprocess.SubprocessError) as error:
    print('Docker update failed; saved runtime retained: ' + str(error), flush=True)
    raise SystemExit(1)
'''])
    daemon = server = None
    stopping = False

    def stop(signum, frame):
        nonlocal stopping
        stopping = True
        for process in (server, daemon, updater):
            if process is not None and process.poll() is None:
                process.terminate()

    signal.signal(signal.SIGTERM, stop)
    signal.signal(signal.SIGINT, stop)
    try:
        # A saved installation starts immediately; updates are selected next start.
        if not (ROOT / 'current').exists():
            if updater.wait() != 0:
                raise RuntimeError('Docker installation failed. Check internet access and retry T3 startup.')
        if stopping:
            return 0
        selected = (ROOT / 'current').resolve(strict=True)
        env = dict(os.environ, PATH=str(selected / 'usr/bin') + ':' + os.environ['PATH'])
        runtime = Path(env['XDG_RUNTIME_DIR'])
        runtime.mkdir(parents=True, exist_ok=True, mode=0o700)
        data = Path.home() / '.local/share/docker'
        daemon = subprocess.Popen([str(selected / 'usr/bin/dockerd-rootless.sh'),
                                   '--host=' + env['DOCKER_HOST'], '--data-root=' + str(data),
                                   '--exec-root=' + str(runtime / 'docker'), '--pidfile=' + str(runtime / 'docker.pid'),
                                   '--init-path=' + str(selected / 'usr/libexec/docker/docker-init')],
                                  env=env)
        deadline = time.monotonic() + 60
        while not stopping and time.monotonic() < deadline:
            if daemon.poll() is not None:
                raise RuntimeError('Rootless Docker exited before becoming ready; check the workload logs.')
            try:
                probe = subprocess.run([str(selected / 'usr/bin/docker'), 'info'], env=env,
                                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=3)
                if probe.returncode == 0:
                    break
            except subprocess.TimeoutExpired:
                pass
            time.sleep(.25)
        else:
            if stopping:
                return 0
            raise RuntimeError('Rootless Docker did not become ready within 60 seconds.')
        print('T3 rootless Docker is ready (docker, docker compose, docker buildx).', flush=True)
        server = subprocess.Popen(command)
        while not stopping:
            if daemon.poll() is not None:
                raise RuntimeError('Rootless Docker stopped unexpectedly; restarting T3 is required.')
            if server.poll() is not None:
                return server.returncode
            time.sleep(.25)
        return 0
    finally:
        stop(None, None)
        for process in (server, daemon, updater):
            if process is not None:
                try:
                    process.wait(timeout=8)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait()
