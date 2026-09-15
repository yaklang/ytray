#!/usr/bin/env python3
"""Use the real WinSparkle DLL with isolated keys, localhost feeds and an intercepted installer."""
import base64
import functools
import http.server
from pathlib import Path
import subprocess
import sys
import tempfile
import threading
import xml.etree.ElementTree as ET

NS='http://www.andymatuschak.org/xml-namespaces/sparkle'

class QuietHandler(http.server.SimpleHTTPRequestHandler):
    def log_message(self,*args): pass

with tempfile.TemporaryDirectory(prefix='ytray-update-test-') as temporary:
    root=Path(temporary); key=root/'key.pem'
    subprocess.run(['openssl','genpkey','-algorithm','ED25519','-out',str(key)],check=True)
    public=base64.b64encode(subprocess.check_output(['openssl','pkey','-in',str(key),'-pubout','-outform','DER'])[12:]).decode()
    server=http.server.ThreadingHTTPServer(('127.0.0.1',0),functools.partial(QuietHandler,directory=str(root)))
    threading.Thread(target=server.serve_forever,daemon=True).start()
    try:
        for tampered in [False,True]:
            payload=root/'setup.exe'; payload.write_bytes(b'Isolated signed test payload. Not executable; execution is intercepted.')
            signature=base64.b64encode(subprocess.check_output(['openssl','pkeyutl','-sign','-rawin','-inkey',str(key),'-in',str(payload)])).decode()
            if tampered: payload.write_bytes(b'Altered unsigned payload')
            rss=ET.Element('rss',version='2.0'); item=ET.SubElement(ET.SubElement(rss,'channel'),'item')
            ET.SubElement(item,'title').text='Isolated fixture update'
            ET.SubElement(item,f'{{{NS}}}version').text='2.0.0'
            ET.SubElement(item,'enclosure',url=f'http://127.0.0.1:{server.server_port}/setup.exe',length=str(payload.stat().st_size),type='application/octet-stream',attrib={f'{{{NS}}}edSignature':signature})
            ET.ElementTree(rss).write(root/'appcast.xml',encoding='utf-8',xml_declaration=True)
            subprocess.run([sys.argv[1],f'http://127.0.0.1:{server.server_port}/appcast.xml',public,str(tampered)],check=True,timeout=60)
    finally: server.shutdown(); server.server_close()
