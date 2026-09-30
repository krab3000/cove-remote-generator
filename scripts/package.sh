#!/usr/bin/env sh
# Builds the Cove extension ZIP (install it from Cove: Settings -> Extensions -> Install from ZIP).
#   scripts/package.sh [Configuration]
set -eu

repo=$(cd "$(dirname "$0")/.." && pwd)
configuration=${1:-Release}
version=$(tr -d '[:space:]' < "$repo/VERSION")
publish="$repo/artifacts/publish"
zip="$repo/artifacts/remote-heavylifter-$version.zip"

(cd "$repo/extension/frontend" && npm ci --no-audit --no-fund && npm run build)

rm -rf "$publish"
dotnet publish "$repo/extension/backend/RemoteHeavylifter/RemoteHeavylifter.csproj" -c "$configuration" -o "$publish"

for required in extension.json RemoteHeavylifter.dll assets/ui.mjs; do
  [ -f "$publish/$required" ] || { echo "Package is missing $required" >&2; exit 1; }
done
if ls "$publish" | grep -Eq '^(Cove\.|Microsoft\.EntityFrameworkCore|Npgsql|Pgvector).*\.dll$'; then
  echo "Package contains host assemblies" >&2
  exit 1
fi

rm -f "$zip"
(cd "$publish" && zip -qr "$zip" .)
echo "Built $zip"
(cd "$publish" && find . -type f | sed 's|^\./|  |')
