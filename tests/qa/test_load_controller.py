"""DUMMY-only tests. No controller HTTP request or external service is used."""
from contextlib import redirect_stderr, redirect_stdout
from datetime import datetime, timedelta, timezone
import io
import json
import os
from pathlib import Path
import socket
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

QA = Path(__file__).resolve().parents[2] / 'scripts/qa'
sys.path.insert(0, str(QA))
import load_controller as loader


class LoaderTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name).resolve()
        self.root.chmod(0o700)
        self.delivery_path = self.root / 'controller.json'
        self.local_path = self.root / 'controller-local.json'
        self.now = datetime.now(timezone.utc)
        self.delivery = dict(runId='11111111-1111-4111-8111-111111111111', candidateSha='a' * 40,
                             expiresAt=(self.now + timedelta(minutes=30)).isoformat(), token='d' * 64)
        self.local = dict(baseUrl='https://qa.example.invalid', runId=self.delivery['runId'],
                          candidateSha=self.delivery['candidateSha'],
                          controllerSessionId='22222222-2222-4222-8222-222222222222')
        self.write()

    def tearDown(self):
        self.temporary.cleanup()

    def write(self):
        for path, value in ((self.delivery_path, self.delivery), (self.local_path, self.local)):
            path.write_text(json.dumps(value), encoding='utf-8')
            path.chmod(0o600)

    def call(self, *extra):
        out, err = io.StringIO(), io.StringIO()
        with redirect_stdout(out), redirect_stderr(err):
            code = loader.main(['--controller-file', str(self.delivery_path), '--local-config',
                                str(self.local_path), *extra])
        output = out.getvalue() + err.getvalue()
        self.assertNotIn(self.delivery['token'], output)
        self.assertNotIn(str(self.root), output)
        return code, output

    def rejected(self):
        code, output = self.call('validate')
        self.assertEqual(1, code)
        self.assertEqual(loader.FAILURE + '\n', output)

    def test_validation_never_starts_child_or_network(self):
        with patch.object(socket.socket, 'connect', side_effect=AssertionError('NETWORK')), \
                patch.object(loader.subprocess, 'run', side_effect=AssertionError('CHILD')):
            self.assertEqual(0, self.call('validate')[0])

    def test_fixed_session_and_no_parent_environment_mutation(self):
        with patch.dict(os.environ, {'CB_QA_RUNNER_TOKEN': 'DUMMY-RUNNER',
                                     'CB_QA_CONTROLLER_TOKEN': 'DUMMY-STALE'}):
            before = os.environ.copy()
            first, _ = loader.load_environment(self.delivery_path, self.local_path, self.now)
            second, _ = loader.load_environment(self.delivery_path, self.local_path, self.now)
            self.assertEqual(self.local['controllerSessionId'], first['CB_QA_CONTROLLER_SESSION_ID'])
            self.assertEqual(first, second)
            self.assertEqual(self.delivery['token'], first['CB_QA_CONTROLLER_TOKEN'])
            self.assertNotIn('CB_QA_RUNNER_TOKEN', first)
            self.assertEqual(before, dict(os.environ))

    def test_missing_unknown_nonstring_and_wrong_schema(self):
        original = self.delivery.copy()
        variants = [{k: v for k, v in original.items() if k != 'token'},
                    dict(original, runnerToken='DUMMY'), dict(original, token=7),
                    ['DUMMY'], dict(original, role='runner')]
        for value in variants:
            with self.subTest(valueType=type(value).__name__):
                self.delivery_path.write_text(json.dumps(value))
                self.rejected()

    def test_duplicates_malformed_encoding_and_oversize(self):
        for value in [b'{"token":"DUMMY","token":"DUMMY"}', b'{DUMMY', b'\xff',
                      b'x' * 16385, b'[' * 1500]:
            with self.subTest(size=len(value)):
                self.delivery_path.write_bytes(value)
                self.rejected()

    def test_run_sha_session_and_token_validation(self):
        for target, key, values in [
            (self.delivery, 'runId', ['DUMMY', '00000000-0000-0000-0000-000000000000']),
            (self.delivery, 'candidateSha', ['a' * 39, 'A' * 40]),
            (self.delivery, 'token', ['DUMMY', 'd' * 63, 'D' * 64, 'd' * 63 + '\n']),
            (self.local, 'runId', ['33333333-3333-4333-8333-333333333333']),
            (self.local, 'candidateSha', ['b' * 40]),
            (self.local, 'controllerSessionId', ['DUMMY', '00000000-0000-0000-0000-000000000000'])]:
            original = target[key]
            for value in values:
                with self.subTest(field=key):
                    target[key] = value
                    self.write()
                    self.rejected()
            target[key] = original
            self.write()

    def test_expiry_requires_current_utc_and_at_most_45_minutes(self):
        values = [(self.now - timedelta(seconds=1)).isoformat(),
                  (self.now + timedelta(minutes=46)).isoformat(),
                  (self.now + timedelta(minutes=30)).replace(tzinfo=None).isoformat(),
                  '2099-01-01T00:00:00+08:00', 'DUMMY']
        for value in values:
            with self.subTest(expiry=value):
                self.delivery['expiresAt'] = value
                self.write()
                self.rejected()

    def test_native_timestamp_precision_with_fixed_clock(self):
        clock = datetime(2026, 9, 19, 12, 0, tzinfo=timezone.utc)
        for expires in ['2026-09-19T12:30:00Z', '2026-09-19T12:30:00+00:00',
                        '2026-09-19T12:30:00.1234567Z', '2026-09-19T12:30:00.1234567+00:00']:
            with self.subTest(timestamp=expires):
                self.delivery['expiresAt'] = expires
                self.write()
                environment, _ = loader.load_environment(self.delivery_path, self.local_path, clock)
                self.assertEqual(self.delivery['token'], environment['CB_QA_CONTROLLER_TOKEN'])
        self.delivery['expiresAt'] = '2026-09-19T12:30:00.12345678Z'
        self.write()
        with self.assertRaises(loader.InvalidConfiguration):
            loader.load_environment(self.delivery_path, self.local_path, clock)
        self.delivery['expiresAt'] = '2026-09-19T12:45:00.0000001Z'
        self.write()
        with self.assertRaises(loader.InvalidConfiguration):
            loader.load_environment(self.delivery_path, self.local_path, clock)

    def test_literal_dummy_structure_is_rejected(self):
        self.delivery_path.write_text(json.dumps(dict.fromkeys(loader.DELIVERY_FIELDS, 'DUMMY')))
        self.rejected()

    def test_https_origin_only(self):
        for base in ['http://qa.example.invalid', 'https://qa.example.invalid/',
                     'https://qa.example.invalid/qa', 'https://qa.example.invalid?x=DUMMY',
                     'https://qa.example.invalid#', 'https://u:DUMMY@qa.example.invalid',
                     'https://qa.example.invalid:99999', 'https://qa.example.invalid\n',
                     'https://qa.example.invalid\\other', 'https://%71a.example.invalid']:
            with self.subTest(base=base):
                self.local['baseUrl'] = base
                self.write()
                self.rejected()

    def test_file_and_directory_permissions(self):
        for path, bad, restore in [(self.delivery_path, 0o644, 0o600),
                                   (self.local_path, 0o640, 0o600),
                                   (self.root, 0o755, 0o700)]:
            with self.subTest(mode=bad):
                path.chmod(bad)
                self.rejected()
                path.chmod(restore)

    def test_symlink_hardlink_and_fifo_are_rejected(self):
        saved = self.root / 'saved.json'
        self.delivery_path.rename(saved)
        self.delivery_path.symlink_to(saved)
        self.rejected()
        self.delivery_path.unlink()
        os.link(saved, self.delivery_path)
        self.rejected()
        self.delivery_path.unlink()
        os.mkfifo(self.delivery_path, 0o600)
        self.rejected()

    def test_foreign_owner_missing_file_and_linked_parent(self):
        with patch.object(loader.os, 'getuid', return_value=os.getuid() + 1):
            self.rejected()
        alias = self.root / 'alias'
        alias.symlink_to(self.root, target_is_directory=True)
        with self.assertRaises(loader.InvalidConfiguration):
            loader.read_restricted_json(alias / 'controller.json', loader.DELIVERY_FIELDS)
        self.delivery_path.unlink()
        self.rejected()

    def test_validate_cannot_submit_status_and_dummy_cannot_run(self):
        with patch.object(loader.subprocess, 'run', side_effect=AssertionError('CHILD')):
            for arguments in [('validate', 'Status'), ('run',), ('run', 'Status'),
                              ('run', 'EndRun', '--fixture-id', 'unicode-v1'),
                              ('run', 'SetFixtureClipboard')]:
                self.assertEqual(1, self.call(*arguments)[0])

    def test_cli_and_errors_never_echo_untrusted_text(self):
        self.assertEqual(1, self.call(self.delivery['token'])[0])
        self.delivery_path.write_text('DUMMY:' + self.delivery['token'])
        self.rejected()

    def prepared_environment(self):
        environment, expires = loader.load_environment(self.delivery_path, self.local_path, self.now)
        # This hostname is only passed to a mocked subprocess; it is never resolved.
        environment['CB_QA_BASE_URL'] = 'https://qa.example.test'
        return environment, expires

    def test_execution_only_fixed_child_and_token_only_in_child_environment(self):
        environment, expires = self.prepared_environment()
        calls = []

        def fake_run(command, **kwargs):
            calls.append((command, kwargs))
            if command[0] == 'git':
                return subprocess.CompletedProcess(command, 0, self.delivery['candidateSha'], '')
            self.assertNotIn(self.delivery['token'], ' '.join(command))
            self.assertEqual(self.delivery['token'], kwargs['env']['CB_QA_CONTROLLER_TOKEN'])
            self.assertEqual(self.local['controllerSessionId'], kwargs['env']['CB_QA_CONTROLLER_SESSION_ID'])
            self.assertEqual([sys.executable, '-I', str(QA / 'controller.py'), 'Status'], command)
            return subprocess.CompletedProcess(command, 0,
                json.dumps(dict(action='Status', fixtureId=None, result='PASS')), 'DUMMY STDERR')

        with patch.object(loader.subprocess, 'run', side_effect=fake_run), redirect_stdout(io.StringIO()) as out:
            self.assertEqual(0, loader.run_controller(environment, expires, 'Status', None))
        self.assertEqual(4, len(calls))
        self.assertNotIn('DUMMY STDERR', out.getvalue())
        self.assertNotIn(self.delivery['token'], out.getvalue())

    def test_child_error_or_unexpected_output_is_not_forwarded(self):
        self.local['baseUrl'] = 'https://qa.example.test'
        self.write()
        for payload in [self.delivery['token'], json.dumps(dict(action='Status', fixtureId=None,
                                                              result=self.delivery['token']))]:
            def fake_run(command, **kwargs):
                if command[0] == 'git':
                    return subprocess.CompletedProcess(command, 0, self.delivery['candidateSha'], '')
                return subprocess.CompletedProcess(command, 1, payload, self.delivery['token'])
            with patch.object(loader.subprocess, 'run', side_effect=fake_run):
                code, output = self.call('run', 'Status')
                self.assertEqual(1, code)
                self.assertEqual(loader.FAILURE + '\n', output)

    def test_wrong_checkout_or_dirty_controller_cannot_launch(self):
        environment, expires = self.prepared_environment()
        with patch.object(loader.subprocess, 'run', return_value=subprocess.CompletedProcess([], 0, 'b' * 40)) as run:
            with self.assertRaises(loader.InvalidConfiguration):
                loader.run_controller(environment, expires, 'Status', None)
            self.assertEqual(1, run.call_count)
        with patch.object(loader.subprocess, 'run', side_effect=[
                subprocess.CompletedProcess([], 0, self.delivery['candidateSha']),
                subprocess.CompletedProcess([], 0, ''),
                subprocess.CalledProcessError(1, ['git'])]) as run:
            with self.assertRaises(subprocess.CalledProcessError):
                loader.run_controller(environment, expires, 'Status', None)
            self.assertEqual(3, run.call_count)


if __name__ == '__main__':
    unittest.main()
