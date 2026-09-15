#!/usr/bin/env python3
"""Exercise real Sparkle download, Ed25519 rejection, replacement and relaunch in temporary app bundles."""
import base64
import functools
import http.server
from pathlib import Path
import plistlib
import shutil
import subprocess
import tempfile
import threading
import time
import uuid
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
FRAMEWORK = ROOT / 'darwin/.build/artifacts/darwin/Sparkle/Sparkle.xcframework/macos-arm64_x86_64/Sparkle.framework'
NS = 'http://www.andymatuschak.org/xml-namespaces/sparkle'

def run(*args):
    try:
        return subprocess.run(list(map(str, args)), check=True, capture_output=True).stdout
    except subprocess.CalledProcessError as error:
        raise RuntimeError(f'{args[0]} failed: {error.stderr.decode(errors="replace")}') from error

class QuietHandler(http.server.SimpleHTTPRequestHandler):
    def log_message(self, *args): pass

def main():
    with tempfile.TemporaryDirectory(prefix='ytray-update-test-') as temporary:
        root = Path(temporary)
        binary = root/'Fixture'
        run('swiftc', '-parse-as-library', '-F', FRAMEWORK.parent, '-framework', 'Sparkle', '-Xlinker', '-rpath', '-Xlinker', '@executable_path/../Frameworks', ROOT/'script/fixtures/UpdaterFixture.swift', '-o', binary)
        key = root/'key.pem'; run('openssl', 'genpkey', '-algorithm', 'ED25519', '-out', key)
        public = base64.b64encode(run('openssl', 'pkey', '-in', key, '-pubout', '-outform', 'DER')[12:]).decode()
        server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), functools.partial(QuietHandler, directory=str(root)))
        threading.Thread(target=server.serve_forever, daemon=True).start()
        try:
            for bad in [True, False]:
                label = 'tamper' if bad else 'upgrade'; case = root/label; case.mkdir()
                result = case/'result.txt'; identifier = 'io.yaklang.ytray.fixture.'+uuid.uuid4().hex
                old = case/'installed/UpdaterFixture.app'; new = case/'new/UpdaterFixture.app'
                for version, app in [(1, old), (2, new)]:
                    (app/'Contents/MacOS').mkdir(parents=True)
                    (app/'Contents/Frameworks').mkdir()
                    shutil.copy2(binary, app/'Contents/MacOS/Fixture')
                    run('ditto', FRAMEWORK, app/'Contents/Frameworks/Sparkle.framework')
                    info = dict(CFBundleIdentifier=identifier, CFBundleName='UpdaterFixture', CFBundleExecutable='Fixture', CFBundlePackageType='APPL', CFBundleVersion=str(version), CFBundleShortVersionString=f'1.0.{version}',
                        SUFeedURL=f'http://127.0.0.1:{server.server_port}/{label}/appcast.xml', SUPublicEDKey=public, SUEnableAutomaticChecks=False, SUVerifyUpdateBeforeExtraction=True, FixtureResult=str(result),
                        NSAppTransportSecurity={'NSAllowsArbitraryLoads':True})
                    (app/'Contents/Info.plist').write_bytes(plistlib.dumps(info))
                    run('codesign','--force','--deep','--sign','-',app)
                payload = case/'update.zip'; run('ditto','-c','-k','--keepParent',new,payload)
                signature = base64.b64encode(run('openssl','pkeyutl','-sign','-rawin','-inkey',key,'-in',payload)).decode()
                if bad:
                    with payload.open('ab') as f: f.write(b'tampered')
                rss=ET.Element('rss',version='2.0'); item=ET.SubElement(ET.SubElement(rss,'channel'),'item')
                ET.SubElement(item,'title').text='Isolated updater test'
                ET.SubElement(item,f'{{{NS}}}version').text='2'
                ET.SubElement(item,'enclosure',url=f'http://127.0.0.1:{server.server_port}/{label}/update.zip',length=str(payload.stat().st_size),type='application/octet-stream',attrib={f'{{{NS}}}edSignature':signature})
                ET.ElementTree(rss).write(case/'appcast.xml',encoding='utf-8',xml_declaration=True)
                sentinel = case/'account-data'; sentinel.write_text('must remain unchanged')
                with (case/'host.log').open('w') as output:
                    process=subprocess.Popen([str(old/'Contents/MacOS/Fixture')],stdout=output,stderr=output)
                    try:
                        deadline=time.monotonic()+90
                        while not result.exists() and time.monotonic()<deadline: time.sleep(.2)
                        if not result.exists(): raise AssertionError('Updater timed out: '+(case/'host.log').read_text()[-5000:])
                        message=result.read_text()
                        installed=plistlib.loads((old/'Contents/Info.plist').read_bytes())['CFBundleVersion']
                        if bad:
                            assert message.startswith('updater-error:') and 'Code=3002' in message, message
                            assert installed=='1', 'Tampered payload replaced the application'
                        else:
                            assert message=='installed-and-relaunched', message
                            assert installed=='2'
                        assert sentinel.read_text()=='must remain unchanged'
                        print(f'Sparkle {label}: verified; installed version {installed}; account data preserved',flush=True)
                    finally:
                        if process.poll() is None: process.terminate()
                        try: process.wait(timeout=10)
                        except subprocess.TimeoutExpired: process.kill(); process.wait()
                        shutil.rmtree(Path.home()/'Library/Caches'/identifier, ignore_errors=True)
                        subprocess.run(['defaults','delete',identifier],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
        finally: server.shutdown(); server.server_close()

if __name__=='__main__': main()
