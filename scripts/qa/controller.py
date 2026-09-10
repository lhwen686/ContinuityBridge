"""Staging-only fixed-action controller. Credentials come from the host environment, never argv."""
import argparse
import json
import os
import re
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

FIXTURES = ('sentinel-v1', 'unicode-v1', 'long-text-v1', 'alpha-png-v1', 'jpeg-v1',
            'png-20000000-v1', 'png-20000001-v1', 'bitmap-v1', 'file-drop-v1')


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=('SetFixtureClipboard', 'VerifyClipboard', 'Status', 'EndRun'))
    parser.add_argument('--fixture-id', choices=FIXTURES)
    args = parser.parse_args()
    base = os.environ.get('CB_QA_BASE_URL', '').rstrip('/')
    run_id = os.environ.get('CB_QA_RUN_ID', '')
    sha = os.environ.get('CB_QA_CANDIDATE_SHA', '')
    session = os.environ.get('CB_QA_CONTROLLER_SESSION_ID', '')
    token = os.environ.get('CB_QA_CONTROLLER_TOKEN', '')
    uri = urllib.parse.urlsplit(base)
    if (uri.scheme != 'https' or not uri.hostname or uri.username or uri.password or uri.path or uri.query or uri.fragment
            or not re.fullmatch('[a-f0-9]{40}', sha) or not re.fullmatch('[a-f0-9]{64}', token)):
        raise ValueError('invalid local QA configuration')
    for value in (run_id, session):
        if str(uuid.UUID(value)) != value or uuid.UUID(value).int == 0:
            raise ValueError('invalid run or session')
    if (args.action in ('SetFixtureClipboard', 'VerifyClipboard')) != bool(args.fixture_id):
        raise ValueError('fixture required only for clipboard actions')
    command_id = str(uuid.uuid4())
    command = dict(runId=run_id, candidateSha=sha, sessionId=session, commandId=command_id,
                   action=args.action, fixtureId=args.fixture_id)
    opener = urllib.request.build_opener(NoRedirect)

    def request(path, body=None):
        data = None if body is None else json.dumps(body).encode()
        for attempt in range(3):
            try:
                req = urllib.request.Request(base + path, data=data,
                    headers={'Authorization': 'Bearer ' + token, 'Content-Type': 'application/json'})
                with opener.open(req, timeout=15) as response:
                    if response.status != 200 or 'no-store' not in response.headers.get('Cache-Control', ''):
                        raise ValueError('invalid QA response')
                    result = response.read(8193)
                    if len(result) > 8192:
                        raise ValueError('QA response limit')
                    return json.loads(result)
            except urllib.error.HTTPError as error:
                raise ValueError('QA rejected request: HTTP ' + str(error.code)) from None
            except urllib.error.URLError:
                if attempt == 2:
                    raise ValueError('QA unavailable') from None
                time.sleep(1)

    result = request('/qa/v1/commands', command)
    deadline = time.monotonic() + 125
    while result.get('result') is None:
        if time.monotonic() >= deadline:
            raise ValueError('QA action timeout')
        time.sleep(1)
        result = request('/qa/v1/status')
    if (result.get('runId') != run_id or result.get('candidateSha') != sha or
            (result.get('command') or {}).get('commandId') != command_id):
        raise ValueError('QA result mismatch')
    outcome = result['result']
    if outcome not in ('PASS', 'MISMATCH', 'BLOCKED', 'STOPPED'):
        raise ValueError('invalid QA outcome')
    print(json.dumps(dict(action=args.action, fixtureId=args.fixture_id, result=outcome)))
    return 0 if outcome == 'PASS' or args.action == 'EndRun' and outcome == 'STOPPED' else 1


if __name__ == '__main__':
    try:
        raise SystemExit(main())
    except (ValueError, OSError, KeyError):
        print('QA FAILED: check local configuration, lease, candidate and fixed action; no response body logged.')
        raise SystemExit(1)
