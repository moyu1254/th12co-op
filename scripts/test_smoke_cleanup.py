"""Owned smoke-process exit races; fake APIs only, no game or process launched."""
import ctypes
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch

import smoke_gameplay as smoke


class TerminationTests(unittest.TestCase):
    def owned(self):
        process = smoke.OwnedProcess.__new__(smoke.OwnedProcess)
        process.info = SimpleNamespace(process=123)
        process.returncode = None
        process.args = ['owned-fake-game']
        return process

    def api(self, waits):
        api = Mock()
        api.WaitForSingleObject.side_effect = waits
        api.TerminateProcess.return_value = False
        def exit_code(handle, output):
            output._obj.value = 0xc0000005
            return True
        api.GetExitCodeProcess.side_effect = exit_code
        return api

    def test_accepts_exit_race_only_after_owned_handle_signals(self):
        process = self.owned()
        api = self.api([258, 0, 0])
        with patch.object(smoke, 'k', api), patch.object(ctypes, 'get_last_error', return_value=5):
            process.terminate()
        self.assertEqual(process.returncode, 0xc0000005)
        self.assertEqual(api.WaitForSingleObject.call_args_list[1].args, (123, 10000))
        api.TerminateProcess.assert_called_once_with(123, 0)

    def test_timeout_preserves_termination_error(self):
        process = self.owned()
        api = self.api([258, 258])
        with patch.object(smoke, 'k', api), patch.object(ctypes, 'get_last_error', return_value=5):
            with self.assertRaises(OSError) as failure:
                process.terminate()
        self.assertEqual(failure.exception.winerror, 5)
        self.assertIsNone(process.returncode)

    def test_already_exited_process_is_not_terminated(self):
        process = self.owned()
        api = self.api([0])
        with patch.object(smoke, 'k', api):
            process.terminate()
        api.TerminateProcess.assert_not_called()


if __name__ == '__main__':
    unittest.main(verbosity=2)
