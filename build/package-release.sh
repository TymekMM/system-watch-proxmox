#!/usr/bin/env bash
set -euo pipefail
if (($# < 1 || $# > 2)) || [[ ! $1 =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[a-zA-Z0-9.-]+)?$ ]]; then
  echo 'usage: bash build/package-release.sh VERSION [OUTPUT_DIRECTORY]' >&2
  exit 2
fi
repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
version=$1
output_dir=${2:-"$repo_root/artifacts"}
if [[ -n $(git -C "$repo_root" status --porcelain) ]]; then
  echo 'commit the reviewed source before packaging a release' >&2
  exit 1
fi
revision=$(git -C "$repo_root" rev-parse HEAD)
sdk_path=$(dotnet msbuild "$repo_root/src/SystemWatch.Worker/SystemWatch.Worker.csproj" \
  -nologo -getProperty:MSBuildSDKsPath)
dotnet_root=$(dirname -- "$(dirname -- "$(dirname -- "$sdk_path")")")
for notice in LICENSE.txt ThirdPartyNotices.txt; do
  if [[ ! -f "$dotnet_root/$notice" ]]; then
    echo "required .NET notice missing: $dotnet_root/$notice (SDK path: $sdk_path)" >&2
    exit 1
  fi
done
mkdir -p -- "$output_dir"
output_dir=$(cd -- "$output_dir" && pwd)
archive_name="system-watch-proxmox-${version}-linux-x64.tar.gz"
archive="$output_dir/$archive_name"
if [[ -e "$archive" || -e "$archive.sha256" ]]; then
  echo 'release archive already exists; use a different version or output directory' >&2
  exit 1
fi
stage=$(mktemp -d)
trap 'rm -rf -- "$stage"' EXIT
mkdir -p "$stage/package/bin" "$stage/package/assets" "$stage/worker" "$stage/manager"
for project in Worker Management; do
  case "$project" in Worker) destination=worker;; Management) destination=manager;; esac
  dotnet publish "$repo_root/src/SystemWatch.$project/SystemWatch.$project.csproj" \
    -c Release -m:1 -p:UseSharedCompilation=false -r linux-x64 --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none \
    -o "$stage/$destination"
done
install -m 0700 "$stage/worker/SystemWatch.Worker" "$stage/package/assets/SystemWatch.Worker"
install -m 0700 "$stage/manager/SystemWatch.Management" "$stage/package/bin/SystemWatch.Management"
install -m 0644 "$repo_root/integration/proxmox/system-watch-ui.js" "$stage/package/assets/system-watch-ui.js"
install -m 0644 "$repo_root/integration/proxmox/system-watch-proxmox.service" "$stage/package/assets/system-watch-proxmox.service"
worker_hash=$(sha256sum "$stage/package/assets/SystemWatch.Worker" | cut -d' ' -f1)
ui_hash=$(sha256sum "$stage/package/assets/system-watch-ui.js" | cut -d' ' -f1)
unit_hash=$(sha256sum "$stage/package/assets/system-watch-proxmox.service" | cut -d' ' -f1)
cat > "$stage/package/assets/release-inputs.json" <<EOF
{
  "format": 1,
  "version": "$version",
  "sourceRevision": "$revision",
  "workerSha256": "$worker_hash",
  "uiSha256": "$ui_hash",
  "unitSha256": "$unit_hash"
}
EOF
cp -R "$repo_root/profiles" "$repo_root/docs" "$stage/package/"
cp "$repo_root/README.md" "$repo_root/AGENTS.md" "$repo_root/LICENSE" "$repo_root/THIRD-PARTY-NOTICES.md" "$stage/package/"
cp "$dotnet_root/LICENSE.txt" "$stage/package/DOTNET-LICENSE.txt"
cp "$dotnet_root/ThirdPartyNotices.txt" "$stage/package/DOTNET-THIRD-PARTY-NOTICES.txt"
{
  printf 'version=%s\nsource_revision=%s\ntarget=linux-x64\n' "$version" "$revision"
  printf 'dotnet_sdk=%s\n' "$(dotnet --version)"
  printf 'worker_sha256=%s\nui_sha256=%s\nunit_template_sha256=%s\n' "$worker_hash" "$ui_hash" "$unit_hash"
} > "$stage/package/BUILD-MANIFEST.txt"
(
  cd "$stage/package"
  find . -type f ! -name SHA256SUMS -print0 | sort -z | xargs -0 sha256sum > SHA256SUMS
  sha256sum -c SHA256SUMS >/dev/null
)
tar -C "$stage/package" -czf "$archive" .
(cd "$output_dir" && sha256sum "$archive_name" > "$archive_name.sha256")
echo "Package: $archive"
cat "$archive.sha256"
