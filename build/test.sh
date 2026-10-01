#!/usr/bin/env bash
set -euo pipefail
repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
cd "$repo_root"
run() {
  dotnet build "tests/SystemWatch.$1.Smoke" -c Release -m:1 -p:UseSharedCompilation=false --nologo
  dotnet run --project "tests/SystemWatch.$1.Smoke" -c Release --no-build -- "${@:2}"
}
fixture=tests/fixtures/snapshot.example.json
sanitized=tests/fixtures/node-fixture-sanitized.json
zfs=tests/fixtures/node-fixture-zfs
for name in ManagementAssets PatchRecipes UnitCandidate CommandRunner SourceCache SmartTemperature SmartctlProbe NvmeHwmon HwmonProbe ZpoolList ZpoolStatus ZfsInventory; do
  run "$name"
done
for name in Contracts AtomicPublication ReadingAge Worker WorkerPolling; do
  run "$name" "$fixture"
done
for name in SnapshotAssembly HealthAggregation; do run "$name" "$fixture" "$sanitized"; done
for name in ZfsProbe ZfsVmReplay; do run "$name" "$zfs"; done
for name in IpmiSensor HbaRoc PlatformProbe; do run "$name" tests/fixtures/platform; done
run DiskTemperature tests/fixtures/disk-temperatures
run SystemProbe tests/fixtures/proc
run LinuxProbes tests/fixtures/proc tests/fixtures/platform tests/fixtures/disk-temperatures
run EndToEndReplay "$zfs" tests/fixtures/proc
node tests/test_system_watch_bridge.js
node tests/test_system_watch_ui_settings.js
