"""Inject one generated fixture into an explicitly selected staging group; never a clipboard API."""
import argparse
import hashlib
import json
import os
from pathlib import Path
from run import Client, require

parser = argparse.ArgumentParser()
parser.add_argument('fixture', type=Path)
args = parser.parse_args()
data = args.fixture.read_bytes()
manifest = json.loads((args.fixture.parent / 'manifest.json').read_text(encoding='utf-8'))
record = next(item for item in manifest['fixtures'] if item['name'] == args.fixture.name)
require(len(data) == record['byteLength'] and hashlib.sha256(data).hexdigest() == record['sha256'], 'Fixture manifest mismatch')
client = Client(os.environ['CB_BASE_URL'], os.environ['CB_TOKEN_A'])
state = client.state()
if args.fixture.suffix == '.txt':
    result = client.mutate(state, data.decode('utf-8'))
else:
    result = client.mutate(state, data, kind='image', extra={'Content-Type':'image/jpeg' if args.fixture.suffix == '.jpg' else 'image/png'})
require(result['available'] and not result['replayed'], 'Fixture no longer current')
print(json.dumps({'fixture':args.fixture.name,'state':result['state']}, ensure_ascii=False, indent=2))
