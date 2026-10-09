#!/usr/bin/env python3
"""Keep robotics implementation and settings outside Xur's host package."""
from pathlib import Path
import re
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
runtime_types = re.compile(r'\b(?:RoboticsRuntime|RoboticsConfiguration|RoboticsEndpoints|RobotMarkerSurvey|LeRobotTools|RobotSkill|RobotStatus|RobotMotorLimits)\b')
checked = []
for project in ('Xur.Agent', 'Xur.Control', 'Xur.Domain'):
    directory = ROOT / 'src' / project
    document = ET.parse(directory / (project + '.csproj'))
    for element in document.iter():
        include = element.get('Include', '')
        assert 'containers/robot' not in include, f'{project} must not reference the robotics application: {include}'
        assert 'Xur.Robotics' not in include and 'robotics/' not in element.get('Link', ''), \
            f'{project} must not package robotics tools or settings: {include}'
    for source in directory.rglob('*'):
        if source.suffix not in ('.cs', '.razor') or any(part in ('obj', 'bin', '.build') for part in source.parts):
            continue
        match = runtime_types.search(source.read_text())
        assert match is None, f'{source.relative_to(ROOT)} retains container-owned runtime type {match.group()}'
        checked.append(str(source.relative_to(ROOT)))
utility = (ROOT / 'tools/Xur.Util/Program.cs').read_text()
assert not re.search(r'robot-calibrate|RoboticsCalibration', utility), 'The host utility must not own robot calibration'
container = ROOT / 'containers/robot'
assert (container / 'Runtime/RoboticsRuntime.cs').is_file(), 'Robot runtime must live in the robot container project'
assert (container / 'Runtime/RoboticsEndpoints.cs').is_file(), 'Robot settings API must live in the robot container project'
assert not re.search(r'\bXUR_AGENT_SOCKET\b|\bUnixDomainSocketEndPoint\b', (container / 'Program.cs').read_text()), \
    'The robotics application must not relay operations to the privileged host agent'
assert not list((ROOT / 'src/Xur.Domain').glob('Robot*.cs')), 'Robot setting/domain records belong to the container'
print(f'Robotics ownership boundary passed across {len(checked)} host source files')
