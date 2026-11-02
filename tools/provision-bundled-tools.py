"""Prepare pinned tools at build time, never from the installed application.
Packaging is deliberately gated on verified corresponding-source material.
"""
import argparse
import hashlib
import json
import re
from pathlib import Path
import shutil
import subprocess
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parents[1]
LOCK = json.loads((ROOT / 'tools/bundled-tools.lock.json').read_text(encoding='utf-8'))
CACHE = ROOT / 'artifacts/helper-bundle'

def check(path, size, digest):
    if not path.is_file() or path.stat().st_size != size:
        raise RuntimeError(f'Missing or incorrect helper size: {path}')
    with path.open('rb') as stream:
        actual = hashlib.file_digest(stream, 'sha256').hexdigest()
    if actual != digest:
        raise RuntimeError(f'Helper SHA-256 mismatch: {path}')

def fetch(url, target, limit):
    # curl uses the Windows trust store on this host. Certificate verification stays enabled.
    subprocess.run(['curl', '--disable', '--fail', '--location', '--max-redirs', '3', '--proto', '=https', '--proto-redir', '=https', '--max-time', '600', '--max-filesize', str(limit), '--continue-at', '-', '--output', str(target), url], check=True)

def compiled_pins():
    youtube = (ROOT / 'src/NDM.Core/YoutubeService.cs').read_text(encoding='utf-8')
    muxer = (ROOT / 'src/NDM.Core/ToolManager.cs').read_text(encoding='utf-8')
    for pin, source, field in zip(LOCK['tools'], (youtube, youtube, muxer), ('ExtractorPin', 'RuntimePin', 'FfmpegPin')):
        match = re.search(r'\b' + field + r'\s*=\s*new\((.*?)\);', source, re.S)
        if match is None:
            raise RuntimeError('Compiled tool pin could not be read: ' + field)
        values = [text or number for text, number in re.findall(r'"([^"\n]*)"|(\d+)L?', match.group(1))]
        expected = [pin['url'], pin['version'], str(pin['archiveSize']), pin['archiveSha256'], pin['entry'] or pin['name'] + '.exe', str(pin['exeSize']), pin['exeSha256']]
        if values != expected:
            raise RuntimeError('Build lock differs from compiled tool pin: ' + field)
    runtime = (ROOT / 'src/NDM.Core/FfmpegRuntime.cs').read_text(encoding='utf-8')
    compiled = re.findall(r'new\("([a-z0-9.-]+)", (\d+)L, "([0-9a-f]{64})"\)', runtime)
    expected = [(p['name'], str(p['size']), p['sha256']) for p in LOCK['tools'][2]['runtimeFiles']]
    if compiled != expected:
        raise RuntimeError('Build lock differs from compiled runtime dependency pins')

def payload(pin):
    return [{'name': pin['name'] + '.exe', 'entry': pin['entry'], 'size': pin['exeSize'], 'sha256': pin['exeSha256']}, *pin.get('runtimeFiles', [])]

def safe_entry(zipped, name, size):
    entries = zipped.infolist()
    if len(entries) > 4000 or any('..' in e.orig_filename or e.orig_filename.startswith(('/', '\\')) or ':' in e.orig_filename or '\\' in e.orig_filename or (e.external_attr >> 16) & 0o170000 == 0o120000 for e in entries):
        raise RuntimeError('Unsafe helper archive')
    matches = [e for e in entries if e.filename == name]
    if len(matches) != 1 or matches[0].file_size != size:
        raise RuntimeError('Unexpected runtime archive entry: ' + name)
    return matches[0]

def prepare():
    compiled_pins()
    CACHE.mkdir(parents=True, exist_ok=True)
    for pin in LOCK['tools']:
        directory = CACHE / 'tools' / pin['name']; directory.mkdir(parents=True, exist_ok=True)
        archive = CACHE / (('ffmpeg-tas' if pin['name'] == 'ffmpeg' else pin['name']) + '.download')
        if not archive.exists():
            if 'releaseApi' in pin:
                request = urllib.request.Request(pin['releaseApi'], headers={'User-Agent': 'NeoDM-build/1.8.0'})
                with urllib.request.urlopen(request, timeout=30) as response:
                    assets = json.load(response)['assets']
                asset, = [a for a in assets if a['name'] == pin['asset']]
                if asset['size'] != pin['archiveSize'] or asset.get('digest') != 'sha256:' + pin['archiveSha256']:
                    raise RuntimeError('Official release digest does not match the pin')
            else:
                digest = subprocess.check_output(['curl', '--disable', '--fail', '--location', '--max-time', '30', '--proto', '=https', '--proto-redir', '=https', pin['url'] + '.sha256']).decode().split()[0].lower()
                if digest != pin['archiveSha256']:
                    raise RuntimeError('Official archive digest does not match the pin')
            partial = archive.with_suffix('.partial')
            fetch(pin['url'], partial, pin['archiveSize'])
            check(partial, pin['archiveSize'], pin['archiveSha256']); partial.replace(archive)
        check(archive, pin['archiveSize'], pin['archiveSha256'])
        for item in payload(pin):
            target = directory / item['name']
            if not re.fullmatch(r'[a-z0-9-]+\.(exe|dll)', item['name']):
                raise RuntimeError('Unsafe runtime file name')
            if target.exists():
                try:
                    check(target, item['size'], item['sha256'])
                    continue
                except RuntimeError:
                    target.unlink()
            if item['entry'] is None:
                shutil.copyfile(archive, target)
            else:
                with zipfile.ZipFile(archive) as zipped:
                    entry = safe_entry(zipped, item['entry'], item['size'])
                    with zipped.open(entry) as source, target.open('wb') as output:
                        shutil.copyfileobj(source, output)
            check(target, item['size'], item['sha256'])
        print(f"VERIFIED {pin['name']} {pin['version']} {pin['exeSize']} {pin['exeSha256']}")

def verify(output):
    compiled_pins()
    expected = {f"tools/{p['name']}/{f['name']}" for p in LOCK['tools'] for f in payload(p)}
    actual = {p.relative_to(CACHE).as_posix() for p in (CACHE / 'tools').rglob('*') if p.is_file()}
    if actual != expected:
        raise RuntimeError('Missing or unlisted helper payload files: ' + str(actual.symmetric_difference(expected)))
    for pin in LOCK['tools']:
        for item in payload(pin):
            check(CACHE / 'tools' / pin['name'] / item['name'], item['size'], item['sha256'])
    # An upstream source link alone does not cover a standalone executable's bundled dependencies.
    sources = ROOT / 'tools/bundled-source.lock.json'
    if not sources.is_file():
        raise RuntimeError('Redistribution blocked: exact corresponding source, dependency notices and build material have not been verified. See docs/BUNDLE-VERIFICATION.md.')
    manifest = json.loads(sources.read_text(encoding='utf-8'))
    if manifest.get('toolHashes') != {p['name']: p['exeSha256'] for p in LOCK['tools']} or not manifest.get('files'):
        raise RuntimeError('Source material does not cover these exact binaries')
    runtime_hashes = {f"tools/{p['name']}/{f['name']}": f['sha256'] for p in LOCK['tools'] for f in payload(p)}
    if manifest.get('runtimeHashes') != runtime_hashes:
        raise RuntimeError('Source material does not cover these exact runtime dependencies')
    for item in manifest['files']:
        relative = Path(item['path'])
        if relative.is_absolute() or '..' in relative.parts or relative.parts[0] not in ('licenses', 'sources'):
            raise RuntimeError('Unsafe source-material path')
        check(CACHE / relative, item['size'], item['sha256'])
    # Source distribution is a separate release asset, not a second large copy inside the MSI.
    version = re.search(r'<Version>([^<]+)</Version>', (ROOT / 'Directory.Build.props').read_text()).group(1)
    source_zip = ROOT / 'artifacts' / f'NeoDM-helper-sources-{version}.zip'
    with zipfile.ZipFile(source_zip, 'w', compression=zipfile.ZIP_DEFLATED, compresslevel=9) as zipped:
        for name, data in [('MANIFEST.json', manifest), ('BINARY-PINS.json', LOCK)]:
            info = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED; info.external_attr = 0o100644 << 16
            zipped.writestr(info, json.dumps(data, sort_keys=True, indent=2).encode() + b'\n')
        for item in sorted(manifest['files'], key=lambda f: f['path']):
            info = zipfile.ZipInfo(item['path'], date_time=(1980, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_STORED if item['path'].endswith(('.xz', '.gz', '.bz2', '.zip', '.crate', '.tgz')) else zipfile.ZIP_DEFLATED
            info.external_attr = 0o100644 << 16
            with (CACHE / item['path']).open('rb') as source, zipped.open(info, 'w', force_zip64=True) as target:
                shutil.copyfileobj(source, target, 1024 * 1024)
    if output:
        for pin in LOCK['tools']:
            for item in payload(pin):
                relative = Path('tools') / pin['name'] / item['name']
                (output / relative).parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(CACHE / relative, output / relative)
        for item in manifest['files']:
            relative = Path(item['path'])
            if relative.parts[0] == 'licenses':
                (output / relative).parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(CACHE / relative, output / relative)

    print('Helper bundle and corresponding-source payload verified')

if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--prepare', action='store_true')
    parser.add_argument('--verify', action='store_true')
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    if not args.prepare and not args.verify:
        parser.error('Choose --prepare or --verify')
    if args.prepare:
        prepare()
    if args.verify:
        verify(args.output)
