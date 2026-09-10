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
import tempfile
from urllib.parse import urlsplit
import uuid

ROOT = Path(__file__).resolve().parents[3]
FIXTURES = {
    'unicode': ('fixtures/unicode.txt', 40, '6f67bc714d92c21d40b417bdf052f970b8ca7d9f11344b7512b97d4df841f92c'),
    'long-unicode': ('long-text/long-unicode.txt', 504028, '030ef73b7b4f381b32fe090e402bb0d1b7e8f93e762857097340c13893cc1b92'),
    'transparent': ('fixtures/transparent.png', 86, 'ea2069671044c7c011a5781f0fa27edef7161464e6544219d3922a4d6dcad2e4'),
    'jpeg': ('fixtures/synthetic.jpg', 632, 'ed8de651b6efa7e3f6a4c034e4f25d7539918037956fa29803aee994905073c5'),
    'visual-transparent': ('fixtures/visual-transparent.png', 1880, 'd29004bdf6149483ed76cbe458129edf1d01df8aee3a2f0adf4a297800ee82dd'),
    'visual-jpeg': ('fixtures/visual.jpg', 18413, 'cabf40ed4a4907ba496771dc3dd52cc00f2f338df4e5120c70a18d3147d334c7'),
    'near-limit': ('fixtures/rgba-19999999.png', 19999999, '030e0b27993e82957cc052e09a3d78b56b391cde238c2ddb167ae05e209dddba'),
    'at-limit': ('fixtures/rgba-20000000.png', 20000000, '118cee42174d9e2a0e33108ec625fdb1d20e0d2c1d0d13065d770270af06c142'),
    'over-limit': ('fixtures/rgba-20000001.png', 20000001, '8708b26a6577125e5ad3f2a156f5515b16924053b60442a8574fc023ee7b7ea5'),
}

# Pinned receipts observed during the explicit real-iPhone QA fixture lease.
# These are synthetic fixture derivatives, never arbitrary clipboard contents.
IPHONE_OUTPUTS = {
    'transparent': (169, 'a3f55aab21a010c2cca23154217d671920eaebe1169f19df4197c4fae225dcd8'),
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
            try:
                connection.request(method, path, body, request_headers)
            except BrokenPipeError:
                # An early size rejection may arrive before the body finishes.
                # Only a real response below can establish the expected status.
                pass
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
        # Runtime differences can encode stored blocks differently. Validate before
        # touching the pinned files already used by the real-device run.
        with tempfile.TemporaryDirectory(prefix='cb-p4-fixtures-') as temporary:
            generate(temporary)
            prepared = {}
            for name, (relative, size, digest) in FIXTURES.items():
                if name.startswith('visual-') or name == 'long-unicode':
                    continue
                contents = (Path(temporary) / Path(relative).name).read_bytes()
                require(len(contents) == size and hashlib.sha256(contents).hexdigest() == digest,
                        'Generator runtime differs from pinned fixture encoding; existing files preserved')
                prepared[relative] = contents
            (directory / 'fixtures').mkdir(parents=True, exist_ok=True)
            for relative, contents in prepared.items():
                (directory / relative).write_bytes(contents)
        long_dir = directory / 'long-text'
        long_dir.mkdir(parents=True, exist_ok=True)
        text = '  CB-P4-BEGIN\r\n' + ''.join(
            '第%05d行 中文🙂 e\u0301\t 引号" 反斜线\\ 保留空格  \r\n' % i
            for i in range(8000)) + 'CB-P4-END  \r\n'
        long_bytes = text.encode('utf-8')
        _, long_size, long_digest = FIXTURES['long-unicode']
        require(len(long_bytes) == long_size and hashlib.sha256(long_bytes).hexdigest() == long_digest,
                'Long-text generator differs from pinned fixture; existing file preserved')
        (long_dir / 'long-unicode.txt').write_bytes(long_bytes)
        for name in FIXTURES:
            if not name.startswith('visual-'):
                fixture(name)
        return {'status': 'PASS', 'scope': 'Local fixture preparation only', 'fixtures': 7}
    if mode == 'prepare-visual':
        from io import BytesIO
        directory = ROOT / 'artifacts/p4-iphone-20260910'
        (directory / 'fixtures').mkdir(parents=True, exist_ok=True)
        for name in ('visual-transparent', 'visual-jpeg'):
            if (directory / FIXTURES[name][0]).exists():
                fixture(name)
        # The bundled lab Pillow runtime is used only for visible synthetic fixtures.
        # The core relay fixture generator above remains unchanged.
        from PIL import Image, ImageDraw
        visual = Image.new('RGBA', (480, 320), (0, 0, 0, 0))
        drawing = ImageDraw.Draw(visual)
        drawing.rectangle((20, 20, 220, 140), fill=(255, 40, 40, 255))
        drawing.rectangle((260, 20, 460, 140), fill=(40, 80, 255, 128))
        drawing.ellipse((20, 170, 220, 300), fill=(40, 190, 70, 255))
        drawing.rectangle((270, 180, 450, 290), outline=(0, 0, 0, 255), width=8)
        visual_jpeg = Image.new('RGB', visual.size, 'white')
        visual_jpeg.paste(visual, mask=visual.getchannel('A'))
        png_bytes, jpeg_bytes = BytesIO(), BytesIO()
        visual.save(png_bytes, format='PNG')
        visual_jpeg.save(jpeg_bytes, format='JPEG', quality=90, subsampling=0)
        prepared = {'visual-transparent': png_bytes.getvalue(), 'visual-jpeg': jpeg_bytes.getvalue()}
        for name, contents in prepared.items():
            _, size, digest = FIXTURES[name]
            require(len(contents) == size and hashlib.sha256(contents).hexdigest() == digest,
                    'Visual generator differs from pinned encoding; existing files preserved')
        for name, contents in prepared.items():
            (directory / FIXTURES[name][0]).write_bytes(contents)
        return {'status': 'PASS', 'scope': 'Local visual fixture preparation only', 'fixtures': 2}
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
    if mode == 'verify-iphone':
        from io import BytesIO
        from PIL import Image
        require(fixture_id in IPHONE_OUTPUTS, 'No pinned iPhone receipt for fixture')
        size, phone_digest = IPHONE_OUTPUTS[fixture_id]
        item = state['item']
        require(item and item.get('sha256') == phone_digest and
                item.get('byteLength') == size and item.get('mimeType') == 'image/png',
                'Current metadata does not match pinned iPhone fixture receipt')
        require(isinstance(item.get('itemId'), str) and item['itemId'] and
                all(c in 'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_-' for c in item['itemId']),
                'Unexpected item ID syntax')
        downloaded, _ = client.request('GET', '/v1/items/' + item['itemId'] + '/content', limit=size)
        require(hashlib.sha256(downloaded).hexdigest() == phone_digest, 'iPhone output hash mismatch')
        expected_image = Image.open(BytesIO(data)).convert('RGBA')
        actual_image = Image.open(BytesIO(downloaded)).convert('RGBA')
        require(actual_image.size == expected_image.size, 'Image dimensions changed')
        (ROOT / 'artifacts/p4-iphone-20260910' / ('iphone-output-' + fixture_id + '.png')).write_bytes(downloaded)
        require(actual_image.tobytes() == expected_image.tobytes(), 'RGBA pixels changed; pinned derivative saved for local inspection')
        require(client.state()['etag'] == state['etag'], 'Item changed during pixel verification')
        return {'status': 'PASS', 'scope': 'API readback of pinned iPhone fixture receipt',
                'fixtureId': fixture_id, 'byteLength': size, 'sha256': phone_digest,
                'dimensions': list(actual_image.size), 'rgbaPixelEqual': True}
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
            return {'status': 'PASS', 'scope': 'API-side fixture check only',
                    'fixtureId': fixture_id, 'httpStatus': 413, 'cloudStatePreserved': True}
        require(receipt.get('available') is True and receipt.get('replayed') is False,
                'Submission not available or unexpected replay')
        expected = receipt['result']
        state = client.state()
        require(state['etag'] == expected['etag'], 'Current item changed after submission')
    item = state['item']
    metadata_mime = 'text/plain; charset=utf-8' if mime == 'text/plain' else mime
    # Refuse to download an unrecognized current item. Never retrieve private contents.
    require(item and item.get('sha256') == digest and item.get('byteLength') == len(data)
            and item.get('mimeType') == metadata_mime, 'Current metadata does not match pinned fixture')
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
    return {'status': 'PASS', 'scope': 'API-side fixture check only',
            'operation': mode, 'fixtureId': fixture_id, 'mimeType': metadata_mime,
            'byteLength': len(data), 'sha256': digest, 'readbackByteEqual': True}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('mode', choices=('prepare', 'prepare-visual', 'status', 'inject', 'verify', 'verify-iphone'))
    parser.add_argument('--fixture-id', choices=FIXTURES)
    args = parser.parse_args()
    if args.mode in ('inject', 'verify', 'verify-iphone') and not args.fixture_id:
        parser.error('--fixture-id is required for inject/verify')
    try:
        print(json.dumps(execute(args.mode, args.fixture_id), ensure_ascii=False, indent=2))
    except Exception as error:
        # Avoid printing URLs, credentials, current metadata or server response bodies.
        message = str(error) if isinstance(error, CheckFailed) else type(error).__name__
        print(json.dumps({'status': 'FAIL', 'reason': message}), file=sys.stderr)
        sys.exit(1)
