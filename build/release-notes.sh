#!/usr/bin/env bash
# Prints the CHANGELOG.md section of a version (without its heading), for the release body and
# the update notes shown by installers. Usage: build/release-notes.sh 1.0.0 > notes.md
set -euo pipefail
version="$1"
section=$(awk -v v="$version" '
  /^## / { if (found) exit; if (index($0, "[" v "]") > 0) { found = 1; next } }
  found { print }
' CHANGELOG.md)
if [[ -z "${section//[[:space:]]/}" ]]; then
  echo "AI Dataset Studio $version"
else
  printf '%s\n' "$section"
fi
