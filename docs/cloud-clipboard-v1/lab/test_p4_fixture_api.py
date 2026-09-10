"""Offline checks for the lab helper. No device, clipboard or network access."""
import hashlib
import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch

import p4_fixture_api as lab


class LabHelperTests(unittest.TestCase):
    def client(self):
        with patch.dict('os.environ', {'CB_BASE_URL': 'https://staging.example.invalid',
                                       'CB_TOKEN_A': 'synthetic-test-token'}):
            return lab.Staging()

    def response(self, status, data):
        return SimpleNamespace(status=status, read=lambda _: data,
                               getheaders=lambda: [('CaChE-CoNtRoL', 'no-store')])

    def test_early_rejection_requires_actual_413_response(self):
        connection = Mock()
        connection.request.side_effect = BrokenPipeError()
        connection.getresponse.return_value = self.response(413, b'{"error":{"code":"payload_too_large"}}')
        with patch.object(lab.http.client, 'HTTPSConnection', return_value=connection):
            body, _ = self.client().request('POST', '/v1/items/image', b'fixture', expected=413)
        self.assertEqual(json.loads(body)['error']['code'], 'payload_too_large')
        connection.close.assert_called_once()

    def test_broken_pipe_without_response_does_not_pass(self):
        connection = Mock()
        connection.request.side_effect = BrokenPipeError()
        connection.getresponse.side_effect = lab.http.client.RemoteDisconnected()
        with patch.object(lab.http.client, 'HTTPSConnection', return_value=connection):
            with self.assertRaises(lab.http.client.RemoteDisconnected):
                self.client().request('POST', '/v1/items/image', b'fixture', expected=413)

    def test_auth_failure_does_not_expose_body(self):
        connection = Mock()
        connection.getresponse.return_value = self.response(401, b'private-error-fixture')
        with patch.object(lab.http.client, 'HTTPSConnection', return_value=connection):
            with self.assertRaisesRegex(lab.CheckFailed, '^HTTP status 401$'):
                self.client().request('GET', '/v1/clipboard')

    def test_unknown_current_item_never_downloads_body(self):
        client = Mock()
        client.state.return_value = {'item': {'sha256': 'unknown'}, 'etag': '"fixture"'}
        with patch.object(lab, 'Staging', return_value=client), \
                patch.object(lab, 'fixture', return_value=(b'known', 'image/png', 'known-hash')):
            with self.assertRaisesRegex(lab.CheckFailed, 'metadata does not match'):
                lab.execute('verify', 'transparent')
        client.request.assert_not_called()

    def test_text_mime_and_full_etag_are_preserved(self):
        data = b'known synthetic text'
        digest = hashlib.sha256(data).hexdigest()
        state = {'protocolVersion': 1, 'etag': '"full-etag-fixture"',
                 'item': {'itemId': 'fixture-id', 'mimeType': 'text/plain; charset=utf-8',
                          'byteLength': len(data), 'sha256': digest}}
        client = Mock()
        client.state.return_value = state
        client.request.return_value = (data, {'content-type': 'text/plain; charset=utf-8'})
        with patch.object(lab, 'Staging', return_value=client), \
                patch.object(lab, 'fixture', return_value=(data, 'text/plain', digest)):
            self.assertTrue(lab.execute('verify', 'unicode')['readbackByteEqual'])
        real = self.client()
        with patch.object(real, 'request', return_value=(json.dumps(state).encode(), {'etag': state['etag']})):
            self.assertEqual(real.state()['etag'], '"full-etag-fixture"')

    def test_generator_mismatch_preserves_existing_fixture(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            target = root / 'artifacts/p4-iphone-20260910/fixtures/a.png'
            target.parent.mkdir(parents=True)
            target.write_bytes(b'known')
            pinned = {'one': ('fixtures/a.png', 5, hashlib.sha256(b'known').hexdigest())}
            generator = SimpleNamespace(generate=lambda p: (Path(p) / 'a.png').write_bytes(b'different'))
            with patch.object(lab, 'ROOT', root), patch.object(lab, 'FIXTURES', pinned), \
                    patch.dict('sys.modules', {'fixtures': generator}):
                with self.assertRaisesRegex(lab.CheckFailed, 'runtime differs'):
                    lab.execute('prepare', None)
            self.assertEqual(target.read_bytes(), b'known')


if __name__ == '__main__':
    unittest.main()
