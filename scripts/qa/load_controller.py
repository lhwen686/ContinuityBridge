"""Load a restricted controller delivery file; validation is entirely offline."""
import argparse
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import re
import stat
import subprocess
import sys
from urllib.parse import urlsplit
import uuid

from controller import FIXTURES

ACTIONS = ('SetFixtureClipboard', 'VerifyClipboard', 'Status', 'EndRun')
DELIVERY_FIELDS = {'runId', 'candidateSha', 'expiresAt', 'token'}
LOCAL_FIELDS = {'baseUrl', 'runId', 'candidateSha', 'controllerSessionId'}
FAILURE = 'CONTROLLER LOAD FAILED: check restricted files, schema, lease and candidate; no values logged.'


class InvalidConfiguration(ValueError):
    pass


class SafeParser(argparse.ArgumentParser):
    def error(self, message):
        raise InvalidConfiguration()


def require(condition):
    if not condition:
        raise InvalidConfiguration()


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result)
        result[key] = value
    return result


def read_restricted_json(path, fields):
    """Open relative to a verified owner-only directory without following links."""
    require(os.name == 'posix')
    path = Path(path).absolute()
    require(path.parent.resolve() == path.parent)
    directory_fd = os.open(path.parent, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
    try:
        directory = os.fstat(directory_fd)
        require(directory.st_uid == os.getuid() and stat.S_IMODE(directory.st_mode) == 0o700)
        fd = os.open(path.name, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK, dir_fd=directory_fd)
        with os.fdopen(fd, 'rb') as stream:
            info = os.fstat(stream.fileno())
            require(stat.S_ISREG(info.st_mode) and info.st_nlink == 1
                    and info.st_uid == os.getuid() and stat.S_IMODE(info.st_mode) == 0o600
                    and 0 < info.st_size <= 16384)
            raw = stream.read(16385)
            require(len(raw) <= 16384)
    finally:
        os.close(directory_fd)
    value = json.loads(raw.decode('utf-8'), object_pairs_hook=unique_object)
    require(isinstance(value, dict) and set(value) == fields)
    require(all(isinstance(item, str) for item in value.values()))
    return value


def valid_id(value):
    parsed = uuid.UUID(value)
    require(str(parsed) == value and parsed.int != 0)


def load_environment(delivery_path, local_path, now=None):
    delivery = read_restricted_json(delivery_path, DELIVERY_FIELDS)
    local = read_restricted_json(local_path, LOCAL_FIELDS)
    valid_id(delivery['runId'])
    valid_id(local['controllerSessionId'])
    require(re.fullmatch('[a-f0-9]{40}', delivery['candidateSha']) is not None)
    require(re.fullmatch('[a-f0-9]{64}', delivery['token']) is not None)
    require(local['runId'] == delivery['runId'] and local['candidateSha'] == delivery['candidateSha'])
    base = local['baseUrl']
    require(base.isascii() and not any(c.isspace() or ord(c) < 32 or ord(c) == 127 for c in base))
    uri = urlsplit(base)
    require(uri.scheme == 'https' and uri.hostname and uri.username is None and uri.password is None
            and not uri.path and not uri.query and not uri.fragment
            and base == 'https://' + uri.netloc and '%' not in uri.netloc and '\\' not in uri.netloc)
    require(uri.port is None or 1 <= uri.port <= 65535)
    require(not uri.netloc.endswith(':'))
    timestamp = re.fullmatch(r'(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?:\.(\d{1,7}))?(?:Z|\+00:00)',
                             delivery['expiresAt'])
    require(timestamp is not None)
    fraction = (timestamp[2] or '').ljust(7, '0')
    # Python 3.9 cannot directly parse .NET's seven-digit fractional timestamps.
    expires = datetime.fromisoformat(timestamp[1] + '+00:00').replace(microsecond=int(fraction[:6]))
    now = now or datetime.now(timezone.utc)
    delta = expires - now
    remaining_ticks = ((delta.days * 86400 + delta.seconds) * 1000000 + delta.microseconds) * 10 + int(fraction[6])
    require(0 < remaining_ticks <= 45 * 60 * 10000000)
    environment = {key: value for key, value in os.environ.items() if not key.startswith('CB_QA_')}
    environment.update(CB_QA_BASE_URL=base, CB_QA_RUN_ID=delivery['runId'],
                       CB_QA_CANDIDATE_SHA=delivery['candidateSha'],
                       CB_QA_CONTROLLER_SESSION_ID=local['controllerSessionId'],
                       CB_QA_CONTROLLER_TOKEN=delivery['token'])
    return environment, expires


def run_controller(environment, expires, action, fixture):
    """Only invoke the adjacent committed controller, with credentials in child env."""
    require(action in ACTIONS)
    require((action in ('SetFixtureClipboard', 'VerifyClipboard')) == (fixture is not None))
    require(fixture is None or fixture in FIXTURES)
    host = urlsplit(environment['CB_QA_BASE_URL']).hostname.lower()
    require(host != 'invalid' and not host.endswith('.invalid'))
    root = Path(__file__).resolve().parents[2]
    head = subprocess.run(['git', '-C', str(root), 'rev-parse', 'HEAD'],
                          capture_output=True, text=True, check=True, timeout=5).stdout.strip()
    require(head == environment['CB_QA_CANDIDATE_SHA'])
    subprocess.run(['git', '-C', str(root), 'ls-files', '--error-unmatch', '--',
                    'scripts/qa/controller.py', 'scripts/qa/load_controller.py'],
                   capture_output=True, check=True, timeout=5)
    subprocess.run(['git', '-C', str(root), 'diff', '--quiet', 'HEAD', '--',
                    'scripts/qa/controller.py', 'scripts/qa/load_controller.py'],
                   capture_output=True, check=True, timeout=5)
    remaining = (expires - datetime.now(timezone.utc)).total_seconds()
    require(remaining > 0)
    command = [sys.executable, '-I', str(root / 'scripts/qa/controller.py'), action]
    if fixture is not None:
        command.extend(['--fixture-id', fixture])
    # Child output is never forwarded verbatim, including unexpected tracebacks.
    child = subprocess.run(command, env=environment, capture_output=True, text=True,
                           timeout=min(remaining, 240))
    result = json.loads(child.stdout, object_pairs_hook=unique_object)
    require(isinstance(result, dict) and set(result) == {'action', 'fixtureId', 'result'})
    require(result['action'] == action and result['fixtureId'] == fixture
            and result['result'] in ('PASS', 'MISMATCH', 'BLOCKED', 'STOPPED'))
    success = result['result'] == 'PASS' or action == 'EndRun' and result['result'] == 'STOPPED'
    require(child.returncode == (0 if success else 1))
    print(json.dumps(result))
    return child.returncode


def main(argv=None):
    try:
        parser = SafeParser(description=__doc__)
        parser.add_argument('--controller-file', required=True)
        parser.add_argument('--local-config', required=True)
        parser.add_argument('mode', choices=('validate', 'run'))
        parser.add_argument('action', nargs='?', choices=ACTIONS)
        parser.add_argument('--fixture-id', choices=FIXTURES)
        args = parser.parse_args(argv)
        require(args.mode == 'run' or args.action is None and args.fixture_id is None)
        require(args.mode != 'run' or args.action is not None)
        environment, expires = load_environment(args.controller_file, args.local_config)
        if args.mode == 'validate':
            print('CONTROLLER FILE VALID: offline only; no session bound, no QA enabled.')
            return 0
        return run_controller(environment, expires, args.action, args.fixture_id)
    except (ValueError, OSError, KeyError, TypeError, RecursionError, subprocess.SubprocessError):
        print(FAILURE, file=sys.stderr)
        return 1


if __name__ == '__main__':
    raise SystemExit(main())
