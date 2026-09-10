"""P4 staging-only fixture preparation/readback. Never reads a system clipboard.

Credentials come only from process environment CB_BASE_URL and CB_TOKEN_A.
Fixture IDs are pinned synthetic data, not arbitrary paths or upload contents.
This is an API-side aid, not the QA mailbox or an iPhone clipboard oracle.
"""
import argparse
import hashlib
import http.client
import json
import os
from pathlib import Path
import ssl
import sys
from urllib.parse import urlsplit
import uuid

ROOT = Path(__file__).resolve().parents[3]
FIXTURES = {
    'unicode': ('fixtures/unicode.txt', 40, '6f67bc714d92c21d40b417bdf052f970b8ca7d9f11344b7512b97d4df841f92c'),
    'long-unicode': ('long-text/long-unicode.txt', 504028, '030ef73b7b4f381b32fe090e402bb0d1b7e8f93e762857097340c13893cc1b92'),
    'transparent': ('fixtures/transparent.png', 86, 'ea2069671044c7c011a5781f0fa27edef7161464e6544219d3922a4d6dcad2e4'),
    'jpeg': ('fixtures/synthetic.jpg', 632, 'ed8de651b6efa7e3f6a4c034e4f25d7539918037956fa29803aee994905073c5'),
    'near-limit': ('fixtures/rgba-19999999.png', 19999999, '030e0b27993e82957cc052e09a3d78b56b391cde238c2ddb167ae05e209dddba'),
    'at-limit': ('fixtures/rgba-20000000.png', 20000000, '118cee42174d9e2a0e33108ec625fdb1d20e0d2c1d0d13065d770270af06c142'),
    'over-limit': ('fixtures/rgba-20000001.png', 20000001, '8708b26a6577125e5ad3f2a156f5515b16924053b60442a8574fc023ee7b7ea5'),
}


class CheckFailed(Exception):
    """Only fixed, non-private diagnostic messages may use this exception."""


def require(condition, message):
    if not condition:
        raise CheckFailed(message)


def fixture(fixture_id):
    relative, size, digest = FIXTURES[fixture_id]
    data = (ROOT / 'artifacts/p4-iphone-20260910' / relative).read_bytes()
    require(len(data) == size and hashlib.sha256(data).hexdigest() == digest,
            'Pinned fixture length/hash mismatch')
    mime = 'text/plain' if relative.endswith('.txt') else (
        'image/jpeg' if relative.endswith('.jpg') else 'image/png')
    return data, mime, digest


class Staging:
    def __init__(self):
        self.base = urlsplit(os.environ.get('CB_BASE_URL', ''))
        self.token = os.environ.get('CB_TOKEN_A', '')
        require(self.base.scheme == 'https' and self.base.hostname and
                not self.base.username and not self.base.password and
                self.base.path in ('', '/') and not self.base.query and
                not self.base.fragment, 'Set a staging HTTPS origin locally')
        require(self.token and not any(c.isspace() for c in self.token),
                'Set a staging device token locally')

    def request(self, method, path, body=None, headers=None, limit=131072, expected=200):
        connection = http.client.HTTPSConnection(
            self.base.hostname, self.base.port, timeout=45,
            context=ssl.create_default_context())
        request_headers = dict(headers or {})
        request_headers['Authorization'] = 'Bearer ' + self.token
        try:
            connection.request(method, path, body, request_headers)
            response = connection.getresponse()
            data = response.read(limit + 1)
            require(len(data) <= limit, 'Response exceeds expected size bound')
            # HTTP header field names are case-insensitive, including Caddy's Etag.
            result_headers = {k.lower(): v for k, v in response.getheaders()}
            require(response.status == expected, 'HTTP status ' + str(response.status))
            require(result_headers.get('cache-control') == 'no-store', 'Missing no-store')
            return data, result_headers
        finally:
            connection.close()

    def state(self):
        data, headers = self.request('GET', '/v1/clipboard')
        state = json.loads(data)
        require(state.get('protocolVersion') == 1, 'Unexpected protocol version')
        etag = state.get('etag')
        require(isinstance(etag, str) and len(etag) > 2 and
                etag.startswith('"') and etag.endswith('"') and
                headers.get('etag') == etag, 'Full header/body ETag mismatch')
        return state


def execute(mode, fixture_id):
    if mode == 'prepare':
        sys.path.insert(0, str(ROOT / 'tests/relay-blackbox'))
        from fixtures import generate
        directory = ROOT / 'artifacts/p4-iphone-20260910'
        for name, (relative, _, _) in FIXTURES.items():
            if (directory / relative).exists():
                fixture(name)  # Refuse to overwrite changed or unrelated fixture files.
        generate(directory / 'fixtures')
        long_dir = directory / 'long-text'
        long_dir.mkdir(parents=True, exist_ok=True)
        text = '  CB-P4-BEGIN\r\n' + ''.join(
            '第%05d行 中文🙂 e\u0301\t 引号" 反斜线\\ 保留空格  \r\n' % i
            for i in range(8000)) + 'CB-P4-END  \r\n'
        (long_dir / 'long-unicode.txt').write_bytes(text.encode('utf-8'))
        for name in FIXTURES:
            fixture(name)
        return {'status': 'PASS', 'scope': 'Local fixture preparation only', 'fixtures': len(FIXTURES)}
    client = Staging()
    if mode == 'status':
        caps, _ = client.request('GET', '/v1/capabilities')
        caps = json.loads(caps)
        require(1 in caps.get('protocolVersions', []), 'Unsupported capabilities')
        state = client.state()
        return {'status': 'PASS', 'scope': 'API metadata only',
                'protocolVersion': 1, 'limits': caps['limits'],
                'retentionSeconds': caps['retentionSeconds'],
                'itemPresent': state['item'] is not None, 'etagHeaderBodyMatch': True}
    data, mime, digest = fixture(fixture_id)
    state = client.state()
    if mode == 'inject':
        body = json.dumps({'text': data.decode('utf-8')}, ensure_ascii=False).encode('utf-8') if mime == 'text/plain' else data
        path = '/v1/items/text' if mime == 'text/plain' else '/v1/items/image'
        oversized = fixture_id == 'over-limit'
        response, _ = client.request('POST', path, body, {
            'Content-Type': 'application/json' if mime == 'text/plain' else mime,
            'If-Match': state['etag'], 'Idempotency-Key': str(uuid.uuid4())},
            expected=413 if oversized else 200)
        receipt = json.loads(response)
        if oversized:
            require(receipt.get('error', {}).get('code') == 'payload_too_large',
                    'Unexpected oversized error code')
            require(client.state() == state, 'Cloud state changed during rejected upload')
            return {'status': 'PASS', 'scope': 'API fixture only; iPhone NOT RUN',
                    'fixtureId': fixture_id, 'httpStatus': 413, 'cloudStatePreserved': True}
        require(receipt.get('available') is True and receipt.get('replayed') is False,
                'Submission not available or unexpected replay')
        expected = receipt['result']
        state = client.state()
        require(state['etag'] == expected['etag'], 'Current item changed after submission')
    item = state['item']
    # Refuse to download an unrecognized current item. Never retrieve private contents.
    require(item and item.get('sha256') == digest and item.get('byteLength') == len(data)
            and item.get('mimeType') == mime, 'Current metadata does not match pinned fixture')
    item_id = item['itemId']
    require(isinstance(item_id, str) and item_id and
            all(c in 'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_-' for c in item_id),
            'Unexpected item ID syntax')
    downloaded, headers = client.request('GET', '/v1/items/' + item_id + '/content', limit=len(data))
    require(headers.get('content-type', '').split(';')[0].strip() == mime and downloaded == data,
            'Downloaded type or bytes differ from fixture')
    final = client.state()
    require(final['etag'] == state['etag'] and final['item'] and final['item']['itemId'] == item_id,
            'Item replaced or expired during readback')
    return {'status': 'PASS', 'scope': 'API fixture only; iPhone NOT RUN',
            'operation': mode, 'fixtureId': fixture_id, 'mimeType': mime,
            'byteLength': len(data), 'sha256': digest, 'readbackByteEqual': True}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('mode', choices=('prepare', 'status', 'inject', 'verify'))
    parser.add_argument('--fixture-id', choices=FIXTURES)
    args = parser.parse_args()
    if args.mode in ('inject', 'verify') and not args.fixture_id:
        parser.error('--fixture-id is required for inject/verify')
    try:
        print(json.dumps(execute(args.mode, args.fixture_id), ensure_ascii=False, indent=2))
    except Exception as error:
        # Avoid printing URLs, credentials, current metadata or server response bodies.
        message = str(error) if isinstance(error, CheckFailed) else type(error).__name__
        print(json.dumps({'status': 'FAIL', 'reason': message}), file=sys.stderr)
        sys.exit(1)
