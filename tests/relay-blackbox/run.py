"""Wire-only contract suite. Uses no .NET objects, test endpoints or third-party libraries.

External: CB_BASE_URL, CB_TOKEN_A, CB_TOKEN_B; destructive to the TEST group's slot.
Local: --launch DOTNET RELAY_DLL starts only a loopback child with random test tokens.
"""
import argparse
import base64
from concurrent.futures import ThreadPoolExecutor
from contextlib import contextmanager
import hashlib
import http.client
import json
import os
from pathlib import Path
import secrets
import socket
import ssl
import subprocess
import time
from urllib.parse import urlsplit
import uuid
from fixtures import png, chunk, JPEG
import struct


def require(value, message):
    if not value:
        raise AssertionError(message)


class Client:
    def __init__(self, base, token):
        self.base, self.token = urlsplit(base), token

    def request(self, method, path, body=None, headers=None, token=True, chunks=False, expected=200):
        cls = http.client.HTTPSConnection if self.base.scheme == 'https' else http.client.HTTPConnection
        options = {'timeout': 15}
        if self.base.scheme == 'https':
            options['context'] = ssl.create_default_context(cafile=os.environ.get('CB_CA_FILE'))
        connection = cls(self.base.hostname, self.base.port, **options)
        request_headers = dict(headers or {})
        if token:
            request_headers['Authorization'] = 'Bearer ' + self.token
        wire = (body[i:i+16384] for i in range(0, len(body), 16384)) if chunks else body
        try:
            connection.request(method, path, wire, request_headers, encode_chunked=chunks)
            response = connection.getresponse()
            data = response.read()
            require(response.status == expected, f'{method} {path}: expected {expected}, got {response.status}')
            require(response.getheader('Cache-Control') == 'no-store', 'Missing no-store')
            if expected >= 400:
                error = json.loads(data)
                require(set(error) == {'error'} and set(error['error']) == {'code', 'message', 'requestId'}, 'Error shape')
                uuid.UUID(error['error']['requestId'])
                require(self.token.encode() not in data, 'Credential reflected')
            return data, dict(response.getheaders())
        finally:
            connection.close()

    def state(self):
        data, headers = self.request('GET', '/v1/clipboard')
        state = json.loads(data)
        check_state(state)
        require(headers.get('ETag') == state['etag'], 'Header/body ETag differs')
        return state

    def events(self):
        connection = socket.create_connection((self.base.hostname,self.base.port or (443 if self.base.scheme == 'https' else 80)), timeout=10)
        try:
            if self.base.scheme == 'https':
                context = ssl.create_default_context(cafile=os.environ.get('CB_CA_FILE'))
                connection = context.wrap_socket(connection,server_hostname=self.base.hostname)
            key = base64.b64encode(secrets.token_bytes(16)).decode()
            request = f'GET /v1/events HTTP/1.1\r\nHost: {self.base.netloc}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Key: {key}\r\nAuthorization: Bearer {self.token}\r\n\r\n'
            connection.sendall(request.encode())
            headers = b''
            while not headers.endswith(b'\r\n\r\n'):
                require(len(headers) < 16384, 'Oversize WebSocket handshake')
                value = connection.recv(1); require(value, 'Incomplete WebSocket handshake'); headers += value
            require(headers.startswith(b'HTTP/1.1 101 '), 'WebSocket upgrade failed')
            expected = base64.b64encode(hashlib.sha1((key+'258EAFA5-E914-47DA-95CA-C5AB0DC85B11').encode()).digest())
            require(b'sec-websocket-accept: '+expected.lower() in headers.lower(), 'WebSocket accept mismatch')
            return connection
        except BaseException:
            connection.close()
            raise

    def mutate(self, state, body, kind='text', key=None, expected=200, chunks=False, extra=None):
        headers = {'If-Match': state['etag'], 'Idempotency-Key': key or str(uuid.uuid4())}
        headers.update(extra or {})
        if kind == 'text':
            headers.setdefault('Content-Type', 'application/json')
            body = json.dumps({'text': body}, ensure_ascii=False).encode() if isinstance(body, str) else body
        elif kind == 'image':
            headers.setdefault('Content-Type', 'image/png')
        path = '/v1/clipboard' if kind == 'clear' else '/v1/items/' + kind
        data, _ = self.request('DELETE' if kind == 'clear' else 'POST', path, body, headers, chunks=chunks, expected=expected)
        result = json.loads(data)
        if expected == 200:
            require(set(result) == {'requestId', 'operation', 'result', 'state', 'replayed', 'available'}, 'Receipt shape')
            check_state(result['result']); check_state(result['state'])
        return result


def check_state(state):
    require(set(state) == {'protocolVersion', 'serverEpoch', 'revision', 'etag', 'item'}, 'State shape')
    require(state['protocolVersion'] == 1, 'Protocol version')
    uuid.UUID(state['serverEpoch'])
    require(isinstance(state['revision'], str) and str(int(state['revision'])) == state['revision'], 'Revision string')
    require(state['etag'].startswith('"') and state['etag'].endswith('"'), 'Strong ETag')
    if state['item'] is not None:
        require(set(state['item']) == {'itemId','kind','mimeType','byteLength','sha256','sourceDeviceId','cryptoMode','committedAt','expiresAt'}, 'Item shape')
        require(state['item']['cryptoMode'] == 'none', 'Unexpected encryption claim')


def hint(connection):
    def read(count):
        result = b''
        while len(result) < count:
            data = connection.recv(count-len(result)); require(data, 'WebSocket closed before hint'); result += data
        return result
    first, second = read(2)
    require(first == 0x81 and second & 0x80 == 0, 'Expected complete unmasked text hint')
    size = second & 127
    if size == 126: size = struct.unpack('>H',read(2))[0]
    elif size == 127: size = struct.unpack('>Q',read(8))[0]
    require(size <= 4096, 'Event size limit')
    event = json.loads(read(size))
    require(set(event) == {'type','serverEpoch','revision'} and event['type'] == 'changed', 'Event shape/body leak')
    return event


def suite(a, b):
    passed = []
    def done(case):
        passed.append(case)
        print('PASS ' + case, flush=True)

    for path in ['/v1/capabilities', '/v1/clipboard', '/v1/items/no-such-item/content', '/v1/events']:
        a.request('GET', path, token=False, expected=401)
    a.request('POST', '/v1/items/text', headers={'Content-Length': '999999999', 'Expect': '100-continue'}, token=False, expected=401)
    caps = json.loads(a.request('GET', '/v1/capabilities')[0])
    require(caps['cryptoModes'] == ['none'] and caps['storage'] == {'mode':'memory','survivesRestart':False}, 'Capabilities security/storage')
    require(caps['idempotency']['storesBodies'] is False, 'Metadata-only capability')
    require(json.loads(a.request('GET', '/healthz', token=False)[0]) == {'status':'ok'}, 'Minimal health')
    done('C10 authentication before body; capabilities; no-store; health')

    state = a.state()
    for etag, code in [(None,428), ('*',400), ('W/"weak"',400), ('"a", "b"',400)]:
        headers = {'Content-Type':'application/json','Idempotency-Key':str(uuid.uuid4())}
        if etag: headers['If-Match'] = etag
        a.request('POST', '/v1/items/text', b'{"text":"x"}', headers, expected=code)
    for key in ['', 'bad', str(uuid.UUID(int=0))]:
        a.mutate(state, 'x', key=key if key else 'bad', expected=400)
    require(a.state() == state, 'Invalid conditions changed state')
    done('C02 strict conditions and UUID; errors preserve state')

    for text in ['', '中文🧪🙂\r\n\t e\u0301  ', ' ']:
        result = a.mutate(a.state(), text)
        item = result['result']['item']
        raw = a.request('GET', '/v1/items/' + item['itemId'] + '/content', headers={'Range':'bytes=0-1'})[0]
        require(raw == text.encode() and item['byteLength'] == len(raw) and item['sha256'] == hashlib.sha256(raw).hexdigest(), 'Text changed/truncated/hash')
    limit = caps['limits']['maxTextUtf8Bytes']
    for size in [limit-1,limit,limit+1]:
        # Multibyte characters with exact UTF-8 byte size, including one-byte remainder.
        text = '中' * (size//3) + 'x' * (size%3)
        a.mutate(a.state(), text, expected=413 if size > limit else 200)
    state = a.state()
    # Worst JSON escaping accepted without charging six wire bytes as six text bytes.
    escaped = b'{"text":"' + br'\u0001' * limit + b'"}'
    a.mutate(state, escaped)
    json_limit = caps['limits']['maxTextJsonBytes']
    small = b'{"text":"x"}'
    a.mutate(a.state(), small + b' ' * (json_limit - len(small)), chunks=True)
    before = a.state()
    a.mutate(before, small + b' ' * (json_limit + 1 - len(small)), expected=413, chunks=True)
    require(a.state() == before, 'JSON limit failure replaced body')
    done('C07/C08 Unicode, empty/whitespace, UTF-8 limit +/-1, JSON escaping/wire limit and Range')

    state = a.state()
    for body in [br'{"text":"\u0000"}',br'{"text":"\ud800"}',br'{"text":"\udc00"}',br'{"text":"\ud800x"}', b'{"text":"\xff"}',b'{"text":"a","text":"b"}',b'{"text":"x","sourceDeviceId":"spoof"}',b'{"text":"x","keyId":"k"}',b'{"text":"x","nonce":"n"}',b'{"text":1}',b'[]']:
        a.mutate(state, body, expected=400)
    a.mutate(state,b'{"text":"x","cryptoMode":"encrypted"}',expected=415)
    a.mutate(state,'x',extra={'Content-Encoding':'gzip'},expected=415)
    a.mutate(state,png(),kind='image',extra={'X-CB-Crypto-Mode':'opaque'},expected=415)
    require(a.state() == state, 'Invalid body mutated state')
    done('C08/C10 strict Unicode/JSON, identity spoofing and unknown envelope rejection')

    initial = a.state(); key = str(uuid.uuid4())
    original = a.mutate(initial, 'original fixture', key=key)
    replacement = b.mutate(a.state(), png(), kind='image')
    replay = a.mutate(initial, br'{"cryptoMode":"none", "text":"original\u0020fixture"}', key=key)
    require(replay['result'] == original['result'] and replay['state'] == replacement['state'] and replay['replayed'] and not replay['available'], 'Replay resurrected or renewed')
    a.request('GET', '/v1/items/' + original['result']['item']['itemId'] + '/content', expected=410)
    a.mutate(initial,'changed fixture',key=key,expected=409)
    a.mutate(a.state(),'original fixture',key=key,expected=409)
    a.mutate(initial,None,kind='clear',key=key,expected=409)
    a.mutate(initial,png(),kind='image',key=key,expected=409)
    b.mutate(a.state(),'same key other device',key=key)
    require(original['result']['item']['sourceDeviceId'] != replacement['result']['item']['sourceDeviceId'], 'Identity not resolved per token')
    # Dropped successful response: deliberately throw away receipt and resend identical operation.
    current = a.state(); lost_key = str(uuid.uuid4())
    a.mutate(current, 'lost response fixture', key=lost_key)
    replay = a.mutate(current, 'lost response fixture', key=lost_key)
    require(replay['replayed'] and replay['available'], 'Lost-response retry committed twice')
    done('C03/C04 fingerprint, device isolation, old item 410 and lost-response replay')

    state = a.state()
    # Exactly two requests match default admission; no unbounded concurrency assumption.
    def contender(client):
        body = json.dumps({'text':'race fixture'}).encode()
        headers = {'If-Match':state['etag'],'Idempotency-Key':str(uuid.uuid4()),'Content-Type':'application/json'}
        conn = http.client.HTTPSConnection if client.base.scheme == 'https' else http.client.HTTPConnection
        options = {'timeout':15}
        if client.base.scheme == 'https': options['context'] = ssl.create_default_context(cafile=os.environ.get('CB_CA_FILE'))
        c = conn(client.base.hostname,client.base.port,**options)
        headers['Authorization'] = 'Bearer ' + client.token
        try:
            c.request('POST','/v1/items/text',body,headers); r = c.getresponse(); r.read(); return r.status
        finally: c.close()
    with ThreadPoolExecutor(2) as pool:
        statuses = list(pool.map(contender,[a,b]))
    require(sorted(statuses) == [200,412], 'CAS race expected one success one 412')
    state = a.state(); same_key = str(uuid.uuid4())
    with ThreadPoolExecutor(2) as pool:
        receipts = list(pool.map(lambda _: a.mutate(state,'same concurrent fixture',key=same_key),range(2)))
    require(sum(not r['replayed'] for r in receipts) == 1, 'Same key committed twice')
    done('C03 real simultaneous HTTP CAS and same-key serialization')

    # Drop the notification connection and recover only from its initial hint + REST.
    with a.events() as events:
        first = hint(events)
        require(first['revision'] == a.state()['revision'], 'Initial hint stale')
        changed = b.mutate(b.state(),'notification fixture')
        require(hint(events)['revision'] == changed['state']['revision'], 'Change hint missing')
    b.mutate(b.state(),'missed while disconnected fixture')
    with a.events() as events:
        current = hint(events); state = a.state()
        require(current['serverEpoch'] == state['serverEpoch'] and current['revision'] == state['revision'], 'Reconnect did not converge')
    done('C13 wire-only WS/WSS changed hint, disconnect, missed event and reconnect')

    require(caps['limits']['maxImageBytes'] == 20_000_000, 'Boundary suite requires default 20MB staging configuration')
    for size in [19_999_999,20_000_000,20_000_001]:
        before = a.state(); data = png(size)
        result = a.mutate(before,data,kind='image',chunks=True,expected=413 if size > 20_000_000 else 200)
        if size <= 20_000_000:
            item = result['result']['item']
            require(item['byteLength'] == size, 'Image boundary bytes')
            returned = a.request('GET','/v1/items/'+item['itemId']+'/content')[0]
            require(returned == data and hashlib.sha256(data).hexdigest() == item['sha256'], 'Image bytes/hash changed')
        else: require(a.state() == before, 'Chunked oversize replaced latest')
    # Declared oversize as well as streamed oversize.
    a.mutate(a.state(),png(20_000_001),kind='image',expected=413)
    done('C07 legal RGBA PNG 20MB +/-1, chunked/declared limits and authenticated binary download')

    result = a.mutate(a.state(),JPEG,kind='image',extra={'Content-Type':'image/jpeg'})
    require(a.request('GET','/v1/items/'+result['result']['item']['itemId']+'/content')[0] == JPEG, 'JPEG transport')
    a.mutate(a.state(),JPEG[:-2],kind='image',extra={'Content-Type':'image/jpeg'},expected=400)
    a.mutate(a.state(),JPEG,kind='image',expected=400)
    state = a.state(); small = png()
    import zlib
    malformed = [b'GIF89a'+bytes(50), small[:-1], small+bytes(1), small[:40]+bytes([small[40]^1])+small[41:]]
    malformed += [small[:33]+chunk(b'acTL',struct.pack('>II',1,0))+small[33:]]
    malformed += [small[:8]+chunk(b'IHDR',struct.pack('>IIBBBBB',100000,100000,8,6,0,0,0))+small[33:]]
    malformed += [small[:33]+chunk(b'IDAT',zlib.compress(bytes([9])+bytes(16)))+chunk(b'IEND',b'')]
    for data in malformed: a.mutate(state,data,kind='image',expected=400)
    a.mutate(state,small,kind='image',extra={'Content-Type':'image/jpeg'},expected=400)
    a.mutate(state,b'GIF89a',kind='image',extra={'Content-Type':'image/gif'},expected=415)
    require(a.state() == state, 'Malformed image replaced current')
    clear = a.mutate(state,None,kind='clear'); clear2 = a.mutate(clear['state'],None,kind='clear')
    require(int(clear2['state']['revision']) == int(clear['state']['revision'])+1 and not clear2['available'], 'Empty clear must advance')
    done('C06/C08 malformed PNG, APNG/GIF, MIME/CRC/pixels/filter rejection and empty clear')
    return passed


@contextmanager
def launched(dotnet, dll):
    root = Path(__file__).resolve().parents[2]
    directory = root/'artifacts'/'relay-blackbox'/uuid.uuid4().hex
    directory.mkdir(parents=True)
    tokens = [secrets.token_hex(32),secrets.token_hex(32)]
    registry = directory/'devices.json'
    registry.write_text(json.dumps([{'deviceId':f'synthetic-{i}','tokenSha256':hashlib.sha256(token.encode()).hexdigest()} for i,token in enumerate(tokens)]))
    with socket.socket() as sock:
        sock.bind(('127.0.0.1',0)); port = sock.getsockname()[1]
    env = dict(os.environ, ASPNETCORE_URLS=f'http://127.0.0.1:{port}',Relay__DeviceFile=str(registry))
    # Explicit fixture limits; no accidental inheritance of production options.
    env.update(Relay__RetentionSeconds='1800',Relay__IdempotencyRetentionSeconds='1800',Relay__MaxImageBytes='20000000',Relay__MaxTextUtf8Bytes='1000000',Relay__MaxTextJsonBytes='8000000',Relay__MaxUploads='2')
    flags = subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0
    process = subprocess.Popen([dotnet,dll],cwd=root,env=env,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,creationflags=flags)
    try:
        base = f'http://127.0.0.1:{port}'
        for _ in range(100):
            require(process.poll() is None, 'Relay exited during readiness')
            try: Client(base,tokens[0]).request('GET','/healthz',token=False); break
            except OSError: time.sleep(.05)
        else: raise AssertionError('Relay readiness timeout')
        yield Client(base,tokens[0]),Client(base,tokens[1])
    finally:
        process.terminate()
        try: process.wait(10)
        except subprocess.TimeoutExpired: process.kill(); process.wait()


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--launch',nargs=2,metavar=('DOTNET','RELAY_DLL'))
    args = parser.parse_args()
    if args.launch:
        with launched(*args.launch) as clients: passed = suite(*clients)
    else:
        passed = suite(Client(os.environ['CB_BASE_URL'],os.environ['CB_TOKEN_A']),Client(os.environ['CB_BASE_URL'],os.environ['CB_TOKEN_B']))
    print(json.dumps({'status':'PASS','groups':len(passed),'cases':passed},indent=2))
