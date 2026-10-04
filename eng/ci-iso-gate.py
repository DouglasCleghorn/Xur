#!/usr/bin/env python3
"""Select installer builds only when explicitly requested by manual dispatch."""
import argparse
import json
import os


def decision(event, requested=False):
    return event == 'workflow_dispatch' and requested


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--requested', action='store_true')
    args = parser.parse_args()
    build = decision(os.environ['GITHUB_EVENT_NAME'], args.requested)
    with open(os.environ['GITHUB_OUTPUT'], 'a') as output:
        output.write('build=' + str(build).lower() + '\n')
    print(json.dumps({'buildIso': build, 'reason': 'Manually requested' if build else 'Installer builds are manual only'}))
