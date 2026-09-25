#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "Usage: $0 DESTINATION" >&2
  exit 2
fi

source_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
destination="$(realpath -m -- "$1")"

if [[ "$destination" == "/" || "$destination" == "$source_root" || "$destination" == *"TeamFlix-Uploader"* ]]; then
  echo "Refusing unsafe destination: $destination" >&2
  exit 2
fi

mkdir -p -- "$destination"
rsync -a \
  --exclude='.git/' \
  --exclude='bin/' \
  --exclude='obj/' \
  --exclude='.vs/' \
  --exclude='*.sqlite' \
  --exclude='*.sqlite-*' \
  --exclude='cache/' \
  --exclude='indexes/' \
  --exclude='logs/' \
  --exclude='secrets/' \
  --exclude='user-data/' \
  "$source_root/" "$destination/"

echo "Neutral source copied to: $destination"
