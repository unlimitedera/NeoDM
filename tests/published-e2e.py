# Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
"""Real published host -> auto-started WPF -> HTTP fixture -> saved file.
Leaves one clearly named verification download in the user's NDM queue.
"""
import hashlib, http.server, json, os, pathlib, struct, subprocess, threading, time, winreg
root = pathlib.Path(__file__).resolve().parents[1]
host_dir = root / 'dist' / 'host'
config = json.loads((host_dir / 'bridge-config.json').read_text())
manifest_path = str(host_dir / 'com.ndm.bridge.json')
with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r'Software\Google\Chrome\NativeMessagingHosts\com.ndm.bridge') as key:
    assert winreg.QueryValueEx(key, '')[0] == manifest_path
manifest = json.loads(pathlib.Path(manifest_path).read_text())
assert manifest['allowed_origins'] == [f"chrome-extension://{config['extensionId']}/"]
assert pathlib.Path(manifest['path']).is_file() and pathlib.Path(config['appPath']).is_file()
payload = (root / 'LICENSE').read_bytes()
class Fixture(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        self.send_response(200)
        self.send_header('Content-Length', str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)
    def do_HEAD(self):
        self.send_response(200)
        self.send_header('Content-Length', str(len(payload)))
        self.end_headers()
    def log_message(self, *args): pass
server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), Fixture)
threading.Thread(target=server.serve_forever, daemon=True).start()
try:
    request = json.dumps({'url': f'http://127.0.0.1:{server.server_port}/NDM-verification.txt', 'fileName': 'NDM-integration-check.txt'}).encode()
    result = subprocess.run([manifest['path'], manifest['allowed_origins'][0]], input=struct.pack('<I',len(request))+request, capture_output=True, timeout=20)
    assert result.returncode == 0, (result.returncode, result.stderr)
    size, = struct.unpack('<I', result.stdout[:4])
    assert len(result.stdout) == size+4
    ack = json.loads(result.stdout[4:]); assert ack.get('ok') and ack.get('id'), ack
    queue = pathlib.Path(os.environ['LOCALAPPDATA']) / 'NDM' / 'queue.json'
    deadline = time.monotonic() + 20
    while time.monotonic() < deadline:
        entries = json.loads(queue.read_text())
        entry = next((x for x in entries if x['Id'] == ack['id']), None)
        assert entry is not None, 'ACK before persisted queue'
        settings_file = queue.parent / 'settings.json'
        settings = json.loads(settings_file.read_text()) if settings_file.exists() else {}
        destination = pathlib.Path(settings.get('DownloadDirectory', str(pathlib.Path.home() / 'Downloads')))
        path = destination / entry['FileName']
        match = path if entry['Status'] == 'Completed' and path.is_file() and path.read_bytes() == payload else None
        if match: break
        time.sleep(.2)
    else: raise AssertionError('No exact completed fixture file: ' + repr(entry))
    print(json.dumps({'result':'PASS','registryVerified':True,'nativeAck':ack,'downloadPath':str(match),'sha256':hashlib.sha256(match.read_bytes()).hexdigest(),'bytes':len(payload)}, indent=2))
finally:
    server.shutdown(); server.server_close()
