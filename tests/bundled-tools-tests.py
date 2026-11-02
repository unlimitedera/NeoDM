"""Offline safety checks for build-time bundled helper preparation."""
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import tempfile
import unittest
import zipfile

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('bundle_provision', ROOT / 'tools/provision-bundled-tools.py')
bundle = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bundle)

class BundleTests(unittest.TestCase):
    def archive(self, names):
        memory = io.BytesIO()
        with zipfile.ZipFile(memory, 'w') as output:
            for name in names:
                entry = zipfile.ZipInfo(name)
                # Windows ZipInfo normalizes backslashes at construction. Preserve the raw unsafe fixture name.
                entry.filename = name
                output.writestr(entry, b'abc')
        memory.seek(0)
        return zipfile.ZipFile(memory)

    def test_compiled_primary_and_dependency_pins_agree(self):
        bundle.compiled_pins()

    def test_all_eleven_runtime_files_are_source_bound(self):
        lock = json.loads((ROOT / 'tools/bundled-source.lock.json').read_text())
        expected = {f"tools/{p['name']}/{f['name']}": f['sha256'] for p in bundle.LOCK['tools'] for f in bundle.payload(p)}
        self.assertEqual(len(expected), 11)
        self.assertEqual(expected, lock['runtimeHashes'])

    def test_exact_single_entry(self):
        with self.archive(['bin/tool.exe']) as archive:
            self.assertEqual(bundle.safe_entry(archive, 'bin/tool.exe', 3).file_size, 3)

    def test_traversal_absolute_and_alternate_stream_entries_fail(self):
        for name in ['../evil.dll', '/evil.dll', 'a/../../evil.dll', 'a\\evil.dll', 'C:evil.dll', 'bin/file:stream']:
            with self.subTest(name=name), self.archive(['bin/tool.exe', name]) as archive:
                with self.assertRaises(RuntimeError):
                    bundle.safe_entry(archive, 'bin/tool.exe', 3)

    def test_duplicate_target_and_wrong_size_fail(self):
        with self.archive(['bin/tool.exe', 'bin/tool.exe']) as archive:
            with self.assertRaises(RuntimeError):
                bundle.safe_entry(archive, 'bin/tool.exe', 3)
        with self.archive(['bin/tool.exe']) as archive:
            with self.assertRaises(RuntimeError):
                bundle.safe_entry(archive, 'bin/tool.exe', 4)

    def test_symlink_entry_fails(self):
        memory = io.BytesIO()
        with zipfile.ZipFile(memory, 'w') as output:
            entry = zipfile.ZipInfo('bin/tool.exe')
            entry.external_attr = 0o120777 << 16
            output.writestr(entry, b'abc')
        memory.seek(0)
        with zipfile.ZipFile(memory) as archive:
            with self.assertRaises(RuntimeError):
                bundle.safe_entry(archive, 'bin/tool.exe', 3)

    def test_same_size_changed_material_is_refused(self):
        scratch = os.environ['TMPDIR']
        with tempfile.TemporaryDirectory(prefix='ndm-bundle-check-', dir=scratch) as temporary:
            path = Path(temporary) / 'notice.txt'
            path.write_bytes(b'abc')
            expected = hashlib.sha256(b'abc').hexdigest()
            bundle.check(path, 3, expected)
            path.write_bytes(b'cba')
            with self.assertRaises(RuntimeError):
                bundle.check(path, 3, expected)

if __name__ == '__main__':
    unittest.main(verbosity=2)
