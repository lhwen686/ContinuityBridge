"""Deterministic non-private fixtures, Python standard library only. No clipboard access."""
import argparse
import base64
import hashlib
import json
from pathlib import Path
import struct
import zlib

# Generated once with installed Pillow from Image.new('RGB',(2,2),(100,150,200)),
# JPEG default quality. Embedded synthetic bytes keep runtime tests stdlib-only.
JPEG = base64.b64decode('/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0aHBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/2wBDAQkJCQwLDBgNDRgyIRwhMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjL/wAARCAACAAIDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwC7RRRXunin/9k=')


def chunk(kind, payload):
    return struct.pack('>I', len(payload)) + kind + payload + struct.pack('>I', zlib.crc32(kind + payload))


def png(target_size=None):
    width, height = (2500, 1999) if target_size else (2, 2)
    # RGBA pixels with alpha 0, 85, 170, 255; deterministic gradient, no private input.
    row = b'\0' + b''.join(bytes((x % 256, (x * 7) % 256, 128, (x % 4) * 85)) for x in range(width))
    data = b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', width, height, 8, 6, 0, 0, 0))
    data += chunk(b'IDAT', zlib.compress(row * height, level=0))
    if target_size:
        padding = target_size - len(data) - 24
        if padding < 0:
            raise ValueError('Target too small for fixture pixels')
        # A well-formed private ancillary chunk INSIDE the PNG, with length + CRC.
        # Most bytes are real uncompressed image scanlines, not appended trailing junk.
        data += chunk(b'npAD', bytes(padding))
    return data + chunk(b'IEND', b'')


def generate(directory):
    directory = Path(directory)
    directory.mkdir(parents=True, exist_ok=True)
    fixtures = {'unicode.txt': '合成 fixture\r\n🙂 e\u0301\t 尾部空格  '.encode(), 'transparent.png': png(), 'synthetic.jpg': JPEG}
    for size in (19_999_999, 20_000_000, 20_000_001):
        fixtures[f'rgba-{size}.png'] = png(size)
    manifest = {'version': 1, 'generator': 'fixtures.py / Python zlib level 0', 'fixtures': []}
    for name, data in fixtures.items():
        (directory / name).write_bytes(data)
        manifest['fixtures'].append({'name': name, 'byteLength': len(data), 'sha256': hashlib.sha256(data).hexdigest()})
    (directory / 'manifest.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
    return manifest


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('directory')
    args = parser.parse_args()
    print(json.dumps(generate(args.directory), indent=2))
