"""Regression checks for test-owned process cleanup, without launching a game.

Only NetGame.close is exercised; every process/mapping API it sees is fake.
The report records this harness scope separately from real-game verification.
"""
import argparse
import hashlib
import io
import json
from pathlib import Path
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import test_native_lockstep as native


class FakeApi:
    def __init__(self, *, exited=False, terminate_ok=True, wait_timeout=False,
                 wait_failed=False, close_failed=False, exit_race=False):
        self.exited = exited
        self.terminate_ok = terminate_ok
        self.wait_timeout = wait_timeout
        self.wait_failed = wait_failed
        self.close_failed = close_failed
        self.exit_race = exit_race
        self.requested = False
        self.calls = []

    def WaitForSingleObject(self, handle, timeout):
        self.calls.append(('wait', handle, timeout))
        if self.wait_failed:
            return 0xffffffff
        if self.requested and not self.wait_timeout:
            self.exited = True
        return 0 if self.exited else 258

    def TerminateProcess(self, handle, code):
        self.calls.append(('terminate', handle, code))
        if self.exit_race:
            self.exited = True
        self.requested = self.terminate_ok
        return int(self.terminate_ok)

    def CloseHandle(self, handle):
        self.calls.append(('close', handle))
        return int(not (self.close_failed and handle == 101))


class FakeMapping:
    def __init__(self, *, write_error=False, close_error=False):
        self.write_error = write_error
        self.close_error = close_error
        self.writes = []
        self.closed = False

    def write32(self, offset, value):
        self.writes.append((offset, value))
        if self.write_error:
            raise OSError('injected mapping write failure')

    def close(self):
        if self.close_error:
            raise OSError('injected mapping close failure')
        self.closed = True


def game(mapping):
    result = native.NetGame.__new__(native.NetGame)
    result.info = SimpleNamespace(process=101, thread=102)
    result.map = mapping
    return result


class CleanupTests(unittest.TestCase):
    def test_running_owned_process_is_confirmed_exited(self):
        api, mapping = FakeApi(), FakeMapping()
        owned = game(mapping)
        with patch.object(native, 'k', api):
            owned.close()
        self.assertTrue(api.exited)
        self.assertTrue(mapping.closed)
        self.assertEqual(mapping.writes, [(28, 99)])
        self.assertIsNone(owned.info.process)
        self.assertIsNone(owned.info.thread)

    def test_already_exited_process_is_not_terminated_again(self):
        api, owned = FakeApi(exited=True), game(None)
        with patch.object(native, 'k', api):
            owned.close()
        self.assertFalse(any(call[0] == 'terminate' for call in api.calls))
        self.assertIsNone(owned.info.process)

    def test_failed_termination_cannot_report_closed(self):
        api, owned = FakeApi(terminate_ok=False), game(None)
        with patch.object(native, 'k', api):
            with self.assertRaisesRegex(RuntimeError, 'TerminateProcess'):
                owned.close()
        self.assertFalse(api.exited)
        self.assertEqual(owned.info.process, 101)
        self.assertNotIn(('close', 101), api.calls)
        self.assertIsNone(owned.info.thread)

    def test_wait_timeout_cannot_report_closed(self):
        api, owned = FakeApi(wait_timeout=True), game(None)
        with patch.object(native, 'k', api):
            with self.assertRaisesRegex(RuntimeError, 'timed out'):
                owned.close()
        self.assertFalse(api.exited)
        self.assertEqual(owned.info.process, 101)
        self.assertNotIn(('close', 101), api.calls)
        self.assertIsNone(owned.info.thread)

    def test_wait_api_failure_is_not_success(self):
        api, owned = FakeApi(wait_failed=True), game(None)
        with patch.object(native, 'k', api):
            with self.assertRaisesRegex(RuntimeError, 'WaitForSingleObject'):
                owned.close()
        self.assertEqual(owned.info.process, 101)
        self.assertIsNone(owned.info.thread)

    def test_mapping_write_failure_still_cleans_owned_process(self):
        api, mapping = FakeApi(), FakeMapping(write_error=True)
        owned = game(mapping)
        with patch.object(native, 'k', api):
            with self.assertRaisesRegex(RuntimeError, 'mapping write'):
                owned.close()
        self.assertTrue(api.exited)
        self.assertTrue(mapping.closed)
        self.assertIsNone(owned.info.process)
        self.assertIsNone(owned.info.thread)

    def test_mapping_close_failure_still_cleans_owned_process(self):
        api, mapping = FakeApi(), FakeMapping(close_error=True)
        owned = game(mapping)
        with patch.object(native, 'k', api):
            with self.assertRaisesRegex(RuntimeError, 'mapping close'):
                owned.close()
        self.assertTrue(api.exited)
        self.assertIs(owned.map, mapping)
        self.assertIsNone(owned.info.process)
        self.assertIsNone(owned.info.thread)

    def test_handle_close_failure_is_reported(self):
        api, owned = FakeApi(close_failed=True), game(None)
        with patch.object(native, 'k', api):
            with self.assertRaisesRegex(RuntimeError, 'CloseHandle'):
                owned.close()
        self.assertTrue(api.exited)
        self.assertEqual(owned.info.process, 101)
        self.assertIsNone(owned.info.thread)

    def test_exit_between_poll_and_termination_is_accepted(self):
        api, owned = FakeApi(terminate_ok=False, exit_race=True), game(None)
        with patch.object(native, 'k', api):
            owned.close()
        self.assertTrue(api.exited)
        self.assertIsNone(owned.info.process)

    def test_successful_close_is_idempotent(self):
        api, owned = FakeApi(), game(FakeMapping())
        with patch.object(native, 'k', api):
            owned.close()
            calls = list(api.calls)
            owned.close()
        self.assertEqual(api.calls, calls)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--out', type=Path, required=True)
    args = parser.parse_args()
    stream = io.StringIO()
    result = unittest.TextTestRunner(stream=stream, verbosity=2).run(
        unittest.defaultTestLoader.loadTestsFromTestCase(CleanupTests))
    report = {'passed': result.wasSuccessful(), 'tests': result.testsRun,
              'scope': 'Fake APIs only; no game launched and no real process terminated',
              'sourceSha256': hashlib.sha256(Path(native.__file__).read_bytes()).hexdigest(),
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
