#!/usr/bin/env bash
set -euo pipefail

mkdir -p "$HOME/transfer/mon"

rsync -a \
  --exclude='appsettings.json' \
  publish/win-x64 "$HOME/transfer/mon/"

tree -d "$HOME/transfer/mon"
