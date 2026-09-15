#!/usr/bin/env python3
"""Prepare all four verified payloads and the backwards-compatible release catalog."""
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
from version import build_number

ROOT = Path(__file__).resolve().parents[1]


def prepare(directory, version, commit, notes, plugin_version):
    directory = Path(directory)
    assets, sums = [], []
    for platform, architecture, kind in [('darwin', 'arm64', 'dmg'), ('darwin', 'amd64', 'dmg'),
                                         ('windows', 'amd64', 'setup'), ('windows', '386', 'setup')]:
        suffix = 'dmg' if kind == 'dmg' else 'setup.exe'
        filename = f'YTray-{version}-{platform}-{architecture}.{suffix}' if kind == 'dmg' else f'YTray-{version}-{platform}-{architecture}-{suffix}'
        path = directory / filename
        if not path.is_file() or path.stat().st_size <= 0:
            raise ValueError(f'Missing payload: {filename}')
        digest = hashlib.sha256(path.read_bytes()).hexdigest()
        checksum = f'{digest}  {filename}\n'
        (directory / (filename + '.sha256.txt')).write_text(checksum)
        sums.append(checksum)
        assets.append(dict(platform=platform, architecture=architecture, kind=kind, filename=filename,
            url=f'https://aliyun-oss.yaklang.com/ytray/{version}/{filename}', sha256=digest, size=path.stat().st_size))
    manifest = dict(schema_version=1, product='ytray', version=version, build_number=build_number(version),
        commit=commit, released_at=subprocess.check_output(['git', '-C', str(ROOT), 'show', '-s', '--format=%cI', commit], text=True).strip(),
        release_notes=f'https://github.com/yaklang/ytray/releases/tag/v{version}', release_notes_text=notes,
        plugin=dict(name='Yakit Browser Agent', version=plugin_version), assets=assets)
    (directory / 'manifest.json').write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + '\n')
    (directory / 'SHA256SUMS').write_text(''.join(sums))


if __name__ == '__main__':
    version = (ROOT / 'VERSION').read_text().strip()
    notes = subprocess.check_output(['bash', str(ROOT / 'script/release-notes.sh'), version], text=True).strip()
    prepare(sys.argv[1], version, os.environ['GITHUB_SHA'], notes, os.environ['PLUGIN_VERSION'])
