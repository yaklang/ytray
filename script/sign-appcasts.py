#!/usr/bin/env python3
"""Sign both platform payloads with Ed25519 and generate native update feeds."""
import base64
from email.utils import format_datetime
from datetime import datetime
import hashlib
import html
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
NS = 'http://www.andymatuschak.org/xml-namespaces/sparkle'
ET.register_namespace('sparkle', NS)

def generate(directory, private_key, public_key):
    directory = Path(directory)
    manifest = json.loads((directory / 'manifest.json').read_text())
    public = base64.b64decode(public_key.strip(), validate=True)
    if len(public) != 32:
        raise ValueError('Invalid update public key')
    with tempfile.TemporaryDirectory() as temp:
        key = Path(temp) / 'key.pem'
        key.write_text(private_key); key.chmod(0o600)
        der = subprocess.check_output(['openssl', 'pkey', '-in', str(key), '-pubout', '-outform', 'DER'])
        if der != bytes.fromhex('302a300506032b6570032100') + public:
            raise ValueError('CI update signing key does not match the embedded public key')
        pub = Path(temp) / 'public.pem'
        pub.write_bytes(subprocess.check_output(['openssl', 'pkey', '-in', str(key), '-pubout']))
        for platform, arch, kind in [('darwin', 'arm64', 'dmg'), ('darwin', 'amd64', 'dmg'), ('windows', 'amd64', 'setup'), ('windows', '386', 'setup')]:
            name = f"appcast-{'macos' if platform == 'darwin' else 'windows'}-{arch}.xml"
            asset, = [a for a in manifest['assets'] if a['platform'] == platform and a['architecture'] == arch and a['kind'] == kind]
            payload = directory / asset['filename']
            if payload.parent != directory or hashlib.sha256(payload.read_bytes()).hexdigest() != asset['sha256'] or payload.stat().st_size != asset['size']:
                raise ValueError('Release payload mismatch before signing')
            signature = subprocess.check_output(['openssl', 'pkeyutl', '-sign', '-rawin', '-inkey', str(key), '-in', str(payload)])
            sig = Path(temp) / 'signature'; sig.write_bytes(signature)
            subprocess.run(['openssl', 'pkeyutl', '-verify', '-pubin', '-inkey', str(pub), '-rawin', '-in', str(payload), '-sigfile', str(sig)], check=True, stdout=subprocess.DEVNULL)
            rss = ET.Element('rss', version='2.0'); channel = ET.SubElement(rss, 'channel')
            ET.SubElement(channel, 'title').text = 'YTray updates'
            ET.SubElement(channel, 'language').text = 'zh-CN'
            item = ET.SubElement(channel, 'item')
            ET.SubElement(item, 'title').text = f"YTray {manifest['version']}"
            ET.SubElement(item, 'pubDate').text = format_datetime(datetime.fromisoformat(manifest['released_at']))
            build = str(manifest['build_number']) if platform == 'darwin' else manifest['version']
            ET.SubElement(item, f'{{{NS}}}version').text = build
            ET.SubElement(item, f'{{{NS}}}shortVersionString').text = manifest['version']
            ET.SubElement(item, f'{{{NS}}}minimumSystemVersion').text = '14.0' if platform == 'darwin' else '10.0'
            ET.SubElement(item, 'description').text = '<html><body>' + ''.join('<p>' + html.escape(line) + '</p>' for line in manifest['release_notes_text'].splitlines() if line.strip()) + '</body></html>'
            enclosure = ET.SubElement(item, 'enclosure', url=asset['url'], length=str(asset['size']), type='application/octet-stream')
            for field, value in [('version', build), ('shortVersionString', manifest['version']), ('edSignature', base64.b64encode(signature).decode()), ('os', 'macos' if platform == 'darwin' else ('windows-x64' if arch == 'amd64' else 'windows-x86'))]:
                enclosure.set(f'{{{NS}}}{field}', value)
            if platform == 'windows':
                enclosure.set(f'{{{NS}}}installerArguments', '/SILENT /SP- /NORESTART /NOFORCECLOSEAPPLICATIONS /YTRAYAUTOUPDATE=1')
            ET.indent(rss)
            ET.ElementTree(rss).write(directory / name, encoding='utf-8', xml_declaration=True)
            print(f'Signed and verified native update feed: {name}')

if __name__ == '__main__':
    generate(sys.argv[1], os.environ['YTRAY_UPDATE_PRIVATE_KEY'], (ROOT / 'resources/updates/ed25519-public-key.txt').read_text())
