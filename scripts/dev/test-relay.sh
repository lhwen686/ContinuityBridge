#!/bin/sh
set -eu
checkout=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
cd "$checkout"
sdk=${CONTINUITYBRIDGE_DOTNET:-dotnet}
"$sdk" restore ContinuityBridge.Portable.slnx --locked-mode
"$sdk" test tests/ContinuityBridge.Relay.Tests/ContinuityBridge.Relay.Tests.csproj -c Release --no-restore --logger 'trx;LogFileName=relay.trx' --results-directory artifacts/relay-test-results
python3 tests/relay-blackbox/run.py --launch "$sdk" "$checkout/src/ContinuityBridge.Relay/bin/Release/net10.0/ContinuityBridge.Relay.dll"
