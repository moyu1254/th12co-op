"""Check smoke-test folder boundaries using disposable files and fake processes.

No original game files are used. OwnedProcess always raises before any launch;
ROOT is a fresh temporary workspace so the legacy game-smoke path is disposable.
"""
import argparse
import hashlib
import io
import json
from pathlib import Path
import struct
import tempfile
import unittest
from unittest.mock import patch

import smoke_gameplay as smoke


class ReachedFakeProcess(Exception):
    pass


class FolderTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix='th12-smoke-path-')
        self.base = Path(self.temporary.name).resolve()
        self.root = self.base / 'workspace'
        self.root.mkdir()
        self.addCleanup(self.temporary.cleanup)

    def config(self, folder):
        folder.mkdir(parents=True, exist_ok=True)
        # Minimal synthetic input to the existing setup transformation, not a
        # copied or runnable game configuration. No executable is present.
        data = bytearray(60)
        data[:4] = b'\x01\x00\x12\x00'
        data[0x1d] = 3
        struct.pack_into('<I', data, 0x38, 0x100)
        (folder / 'th12.cfg').write_bytes(data)
        return bytes(data)

    def rejected(self, folder):
        original = self.config(folder)
        with patch.object(smoke, 'ROOT', self.root), \
                patch.object(smoke, 'OwnedProcess', side_effect=ReachedFakeProcess) as process:
            error = None
            try:
                smoke.Game(folder)
            except Exception as caught:
                error = caught
        self.assertEqual((folder / 'th12.cfg').read_bytes(), original,
                         'Rejected folder configuration was modified')
        process.assert_not_called()
        self.assertIsInstance(error, ValueError)

    def accepted(self, folder):
        original = self.config(folder)
        with patch.object(smoke, 'ROOT', self.root), \
                patch.object(smoke, 'OwnedProcess', side_effect=ReachedFakeProcess) as process:
            with self.assertRaises(ReachedFakeProcess):
                smoke.Game(folder)
        process.assert_called_once_with(folder.resolve())
        modified = (folder / 'th12.cfg').read_bytes()
        self.assertNotEqual(modified, original)
        self.assertEqual(modified[0x1d], original[0x1d])
        self.assertEqual(struct.unpack_from('<I', modified, 0x38)[0] & 0x100, 0)

    def test_external_test_prefix_is_rejected_before_write(self):
        self.rejected(self.base / 'external' / 'game-test-fixture')

    def test_workspace_non_test_folder_is_rejected_before_write(self):
        self.rejected(self.root / 'ordinary-game')

    def test_nested_test_folder_is_rejected_before_write(self):
        self.rejected(self.root / 'nested' / 'game-test-fixture')

    def test_direct_workspace_test_folder_reaches_fake_process(self):
        self.accepted(self.root / 'game-test-fixture')

    def test_legacy_game_smoke_reaches_fake_process(self):
        self.accepted(self.root / 'game-smoke')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--out', type=Path, required=True)
    args = parser.parse_args()
    stream = io.StringIO()
    result = unittest.TextTestRunner(stream=stream, verbosity=2).run(
        unittest.defaultTestLoader.loadTestsFromTestCase(FolderTests))
    report = {'passed': result.wasSuccessful(), 'tests': result.testsRun,
              'scope': 'Disposable synthetic configurations and fake OwnedProcess only; no game launched',
              'sourceSha256': hashlib.sha256(Path(smoke.__file__).read_bytes()).hexdigest(),
              'testSha256': hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
              'failures': [{'test': case.id(), 'traceback': trace}
                           for case, trace in result.failures + result.errors],
              'output': stream.getvalue()}
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(report, indent=2) + '\n', encoding='utf8')
    print(json.dumps({'passed': report['passed'], 'tests': result.testsRun,
                      'failed': len(report['failures']), 'report': str(args.out)}))
    return 0 if result.wasSuccessful() else 1


if __name__ == '__main__':
    raise SystemExit(main())
