#!/usr/bin/env python3
"""Stable Sparkle build number, independent of workflow runs and local commits."""
from pathlib import Path
import argparse
import re


def build_number(version):
    if not re.fullmatch(r'(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)', version):
        raise ValueError('Expected a stable major.minor.patch version')
    major, minor, patch = map(int, version.split('.'))
    if max(major, minor, patch) >= 1000:
        raise ValueError('Version components must be below 1000')
    return major * 1_000_000 + minor * 1000 + patch


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--version', default=(Path(__file__).resolve().parents[1] / 'VERSION').read_text().strip())
    parser.add_argument('--build-number', action='store_true')
    args = parser.parse_args()
    print(build_number(args.version) if args.build_number else args.version)
