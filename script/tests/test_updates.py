import base64
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'script'))
from version import build_number


def module(filename):
    spec = importlib.util.spec_from_file_location(filename.replace('-', '_'), ROOT / 'script' / filename)
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


class Updates(unittest.TestCase):
    def test_monotonic_build_numbers(self):
        self.assertGreater(build_number('0.2.0'), build_number('0.1.999'))
        self.assertGreater(build_number('1.0.0'), build_number('0.999.999'))
        for value in ['0.2.0-dev', '01.2.3', '1.1000.0', '1.2.3\n', '../evil']:
            with self.assertRaises(ValueError): build_number(value)

    def test_four_platform_feeds_and_tamper_rejection(self):
        with tempfile.TemporaryDirectory() as temp:
            directory = Path(temp)
            key = directory / 'private.pem'
            subprocess.run(['openssl', 'genpkey', '-algorithm', 'ED25519', '-out', str(key)], check=True)
            public = base64.b64encode(subprocess.check_output(['openssl', 'pkey', '-in', str(key), '-pubout', '-outform', 'DER'])[12:]).decode()
            payloads = ['darwin-arm64.dmg', 'darwin-amd64.dmg', 'windows-amd64-setup.exe', 'windows-386-setup.exe']
            for suffix in payloads: (directory / ('YTray-0.2.0-' + suffix)).write_bytes(('fixture ' + suffix).encode())
            commit = subprocess.check_output(['git', '-C', str(ROOT), 'rev-parse', 'HEAD'], text=True).strip()
            module('prepare-release-assets.py').prepare(directory, '0.2.0', commit, 'Signed update <details>', '0.2.4')
            signer = module('sign-appcasts.py')
            signer.generate(directory, key.read_text(), public)
            manifest = json.loads((directory / 'manifest.json').read_text())
            self.assertEqual(manifest['build_number'], 2000)
            self.assertEqual(len(manifest['assets']), 4)
            for system, arch, suffix in [('macos', 'arm64', payloads[0]), ('macos', 'amd64', payloads[1]), ('windows', 'amd64', payloads[2]), ('windows', '386', payloads[3])]:
                feed = ET.parse(directory / f'appcast-{system}-{arch}.xml')
                enclosure = feed.find('.//enclosure')
                ns = '{' + signer.NS + '}'
                self.assertEqual(enclosure.attrib['url'], f'https://aliyun-oss.yaklang.com/ytray/0.2.0/YTray-0.2.0-{suffix}')
                self.assertEqual(len(base64.b64decode(enclosure.attrib[ns + 'edSignature'])), 64)
                if system == 'windows':
                    self.assertEqual(enclosure.attrib[ns + 'os'], 'windows-x86' if arch == '386' else 'windows-x64')
                    self.assertEqual(enclosure.attrib[ns + 'installerArguments'], '/SILENT /SP- /NORESTART /NOFORCECLOSEAPPLICATIONS /YTRAYAUTOUPDATE=1')
            with self.assertRaises(ValueError): signer.generate(directory, key.read_text(), base64.b64encode(bytes(32)).decode())
            (directory / ('YTray-0.2.0-' + payloads[0])).write_bytes(b'tampered')
            with self.assertRaises(ValueError): signer.generate(directory, key.read_text(), public)


if __name__ == '__main__': unittest.main()
