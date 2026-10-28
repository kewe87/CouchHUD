#!/usr/bin/env bash
# Everything CI checks, before a push: build (Windows projects too), tests, UI syntax.
# Works on Windows (Git Bash), Linux and macOS with the .NET 10 SDK and Node.
set -euo pipefail
cd "$(dirname "$0")/.."

if [ -e catalog/index.json.unsigned ]; then
  echo "! WARNUNG: catalog/index.json.unsigned existiert, die Katalog-Signatur ist noch zu erneuern (scripts/catalog-sign.ps1), dann die Datei löschen."
  if [ "${RELEASE:-}" = "1" ]; then
    echo "  RELEASE=1: so wird nichts veröffentlicht."
    exit 1
  fi
fi

echo "› dotnet build"
dotnet build CouchHud.slnx -c Release -p:EnableWindowsTargeting=true --nologo -v quiet

echo "› dotnet test"
if [ "${RELEASE:-}" = "1" ]; then echo "  RELEASE=1: the catalog test also demands that index.json.sig matches index.json (pinned key from the code)."; fi
dotnet test CouchHud.slnx -c Release -p:EnableWindowsTargeting=true --no-build --nologo -v quiet

echo "› UI syntax"
# The UI files are ES modules; plain `node --check file.js` can pass a module with a syntax error.
find ui scripts -name '*.js' -o -name '*.mjs' | while read -r f; do
  node --input-type=module --check < "$f" || { echo "  in $f"; exit 1; }
done

echo "› UI logic"
node scripts/ui-logic-check.mjs

echo "› Catalog addresses"
node scripts/catalog-check.mjs

echo "› CSS syntax"
node scripts/css-check.mjs

echo "› UI texts (de/en)"
node scripts/i18n-check.mjs

echo "✓ alles grün"
