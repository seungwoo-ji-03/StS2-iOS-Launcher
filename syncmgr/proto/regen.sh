#!/bin/bash
# Regenerate syncmgr/gen/*.cs from the minimal Steam protocol definitions in this folder (needs protoc).
# These .proto files are trimmed excerpts of Valve's Steam client protocol (as published by
# SteamDatabase/Protobufs), kept to the few messages the in-app sync uses. See THIRD_PARTY_NOTICES.md.
set -euo pipefail
cd "$(dirname "$0")"
protoc --csharp_out=../gen cloud_min.proto cmproto.proto authproto.proto
echo "regenerated ../gen"
