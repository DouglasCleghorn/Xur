#!/usr/bin/env python3
"""Exercise the generated desktop environment with JavaScript timezone detection."""
import json, os, pathlib, shutil, subprocess, tempfile, textwrap

repo = pathlib.Path(__file__).resolve().parents[2]
sdk = os.environ.get('XUR_DOTNET') or shutil.which('dotnet') or str(pathlib.Path.home() / '.local/share/xur-build/dotnet/dotnet')
script = textwrap.dedent((repo / 'src/Xur.Agent/StationRuntime.cs').read_text().split('internal const string UserConfiguration="""\n')[1].split('""";')[0])
evidence = repo / '.build/evidence'
evidence.mkdir(parents=True, exist_ok=True)
results = []

with tempfile.TemporaryDirectory(dir=evidence, prefix='desktop-timezone-') as directory:
    home = pathlib.Path(directory)
    config = home / '.config/environment.d/90-xur-gpu.conf'
    config.parent.mkdir(parents=True)
    config.write_text('TZ=:/etc/localtime\nKWIN_DRM_DEVICES=/dev/dri/card0\n')
    for zone, summer, winter in [('America/Denver', '20:00', '19:00'), ('Asia/Kolkata', '07:30', '07:30'), ('UTC', '02:00', '02:00')]:
        env = dict(os.environ, TZ=zone)
        setting = subprocess.check_output([sdk, 'run', '--project', str(repo / 'tests/Xur.Unit.Tests'), '-c', 'Release', '--', '--station-timezone'], cwd=repo, env=env, text=True).strip().splitlines()[-1]
        assert setting == 'TZ=' + zone, (zone, setting)
        for mode in ('headless', 'local'):
            subprocess.run(['bash', '-c', script, 'xur', str(home), '/dev/dri/card7', mode, '', 'seat-xur-test', setting], check=True, env=env)
            desktop_env = dict(line.split('=', 1) for line in config.read_text().splitlines() if '=' in line)
            assert desktop_env['TZ'] == zone, 'Reload must replace the old pathname TZ override'
            observed = json.loads(subprocess.check_output(['node', '-e', '''
const format = new Intl.DateTimeFormat('en-GB', {hour:'2-digit', minute:'2-digit', hourCycle:'h23'});
console.log(JSON.stringify({zone:format.resolvedOptions().timeZone,
    summer:format.format(new Date('2026-10-05T02:00:00Z')),
    winter:format.format(new Date('2026-12-05T02:00:00Z'))}));
'''], env=env | desktop_env, text=True))
            # Older ICU releases use Kolkata's legacy spelling.
            aliases = ('Asia/Kolkata', 'Asia/Calcutta') if zone == 'Asia/Kolkata' else (zone,)
            assert observed['zone'] in aliases and observed['summer'] == summer and observed['winter'] == winter, (zone, mode, observed)
            results.append(dict(mode=mode, **observed))

print(json.dumps(dict(suite='DesktopTimezone', result='Passed', cases=results)))
