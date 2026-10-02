# AI assistant guide: System Watch for Proxmox VE

This file helps AI assistants operate and develop this repository. Read
[installation](docs/installation.md), [updates](docs/updates.md), and
[troubleshooting](docs/troubleshooting.md) before suggesting host changes.
Use one logical command group at a time and inspect its result before continuing.
For readers new to Linux, use the [beginner tutorial](docs/beginners-guide.md).
Explain where each command runs, restore variables after reconnecting, distinguish
representative output from live results, and stop after a failed check. Select an
actual release from the Releases page; do not assume the latest-stable download
endpoint includes prereleases.

## Operating rules

- Work only on the node explicitly selected by the operator. Do not choose a
  production node from examples or assume another node has the same build.
- Run host commands as root. Build and test on a development machine; the
  self-contained release needs no .NET SDK or Node.js on the Proxmox host.
- Check the package against the expected checksum from the reviewed release,
  then check its internal `SHA256SUMS`. Checksums are integrity checks, not
  publisher signatures. Never replace an expected hash with a calculated one
  just to bypass a mismatch.
- Use absolute recipe paths for `--profile` and `--previous-profile`. Inspect the actual validation error before treating a status failure as vendor drift.
- Select the recipe using `pveversion -v`. Exact original and candidate hashes
  and unique scoped anchors must also match. Never bypass these checks.
- Run a read-only plan before install or update. Explain which services restart.
  Installation/removal restart Proxmox services; GUI updates restart none;
  worker updates restart only System Watch.
- Keep the package, recipe, manifest, and original backups. Do not edit or delete
  the manifest to make an operation pass. Do not run competing operations.
- `file-complete` means recognized files, not independent service/API/browser
  verification. Never claim a browser check from CLI output.
- Missing IPMI/HBA hardware or optional tools can legitimately produce unknown
  or unavailable readings. Never hide stale readings by marking them healthy.
- Never expose the root-only snapshot through an unauthenticated web server.
- Do not send credentials, full snapshots, serial numbers, or private host data
  to public issue trackers. Ask for sanitized diagnostic excerpts.

## Verify and extract a release

On the target node, in the directory containing the downloaded archive and
checksum file. Substitute the actual reviewed release version:

```bash
sha256sum -c system-watch-proxmox-1.0.0-linux-x64.tar.gz.sha256
release_dir=$(mktemp -d /root/system-watch-release.XXXXXX)
tar -xzf system-watch-proxmox-1.0.0-linux-x64.tar.gz -C "$release_dir"
(cd "$release_dir" && sha256sum -c SHA256SUMS)
cat "$release_dir/BUILD-MANIFEST.txt"
```

Stop if a check fails. Run command groups in a shell with `set -euo pipefail`,
so a failed verification cannot silently continue into a mutation.

## Select the host and recipe

Keep these variables in the same root shell. Replace `node-example` with the
operator's chosen node, and select the actual reviewed recipe:

```bash
set -euo pipefail
node_name=node-example
test "$(hostname -s)" = "$node_name"
pveversion -v
recipe="$release_dir/profiles/pve-manager-9.2.20.json"
manager="$release_dir/bin/SystemWatch.Management"
```

Use `pve-manager-9.2.10.json` for its reviewed build. A recipe filename or version
number alone is not compatibility evidence. A new build may need a new recipe.
If reconnecting, explicitly set `release_dir` to the retained package directory.

## Plan and install

```bash
"$manager" plan install \
  --profile "$recipe" --directory "$release_dir/assets" \
  --expected-host "$node_name"
```

Review the plan with the operator before the mutation:

```bash
"$manager" install \
  --profile "$recipe" --directory "$release_dir/assets" \
  --expected-host "$node_name"
"$manager" status --profile "$recipe" --expected-host "$node_name"
```

Both originals must be backed up and verified before installed targets change.
The installer verifies the worker and API; ask the operator to hard-refresh the
node Summary page and inspect panels, scrolling, and display settings.

## Status and diagnostics (read-only)

```bash
"$manager" status --profile "$recipe" --expected-host "$node_name"
systemctl status system-watch-proxmox --no-pager
systemctl show system-watch-proxmox \
  -p ActiveState -p ExecStart -p FragmentPath -p UnitFileState
journalctl -u system-watch-proxmox -n 100 --no-pager
pvesh get "/nodes/$node_name/system-watch" --output-format json
```

The API output may contain private inventory. Inspect locally; sanitize before
sharing. `pvesh` runs with local root privileges and does not prove a browser
user has access. The installer separately checks unauthenticated HTTP denial.
Display preferences are browser-local, per node; they do not disable probes.

## GUI update

Obtain the expected UI hash from the verified candidate release. Set an absolute
candidate path and replace the placeholder with that expected hash:

```bash
candidate=/absolute/path/to/new/assets/system-watch-ui.js
candidate_hash=REPLACE_WITH_REVIEWED_UI_SHA256
"$manager" plan ui-update \
  --profile "$recipe" --asset "$candidate" --sha256 "$candidate_hash" \
  --expected-host "$node_name"
```

After reviewing the plan:

```bash
"$manager" ui-update \
  --profile "$recipe" --asset "$candidate" --sha256 "$candidate_hash" \
  --expected-host "$node_name"
```

Only the UI asset and manifest change. Hard-refresh the browser. A retained,
verified previous UI can be restored with the same command and its expected hash.

## Worker update

```bash
candidate=/absolute/path/to/new/assets/SystemWatch.Worker
candidate_hash=REPLACE_WITH_REVIEWED_WORKER_SHA256
"$manager" plan worker-update \
  --profile "$recipe" --asset "$candidate" --sha256 "$candidate_hash" \
  --expected-host "$node_name"
```

After reviewing the plan:

```bash
"$manager" worker-update \
  --profile "$recipe" --asset "$candidate" --sha256 "$candidate_hash" \
  --expected-host "$node_name"
"$manager" status --profile "$recipe" --expected-host "$node_name"
```

The manager verifies a new worker instance and API access. On failure it attempts
restoration of the previous worker and manifest. Inspect the actual result;
rollback is not guaranteed after power loss or unknown external changes.

## Remove / restore an incomplete installation

Use the recipe matching the installed manifest:

```bash
"$manager" status --profile "$recipe" --expected-host "$node_name"
"$manager" remove --profile "$recipe" --expected-host "$node_name"
"$manager" status --profile "$recipe" --expected-host "$node_name"
```

There is no `plan remove` command. Removal verifies installed bytes and backups,
restores recognized originals, and retains backups. Unknown hashes or missing
backups require investigation, not forced deletion or manual overwriting.

## Proxmox upgrades and overwritten hooks

The normal route is removal before upgrading `pve-manager`, then installation
with a reviewed recipe for the new build. Keep old backups and package evidence
separate from any future installation state.

For a supervised recovery experiment, first preserve the current installation
and record vendor/project hashes, the manifest, unit/drop-ins, package version,
and a VM backup or snapshot suitable for restoring the test VM. Agree on the
recovery boundary with the operator before upgrading.

After an upgrade:

1. Inspect `pveversion -v`, current vendor hashes, service configuration, and
   installed state without mutation. One hook may have changed while another
   remained; do not assume all vendor files were replaced.
2. Treat old original backups as belonging to the old build. Never restore them
   over new vendor files. A failed `remove` or drifted status is a reason to stop.
3. Compare affected files with the exact upgraded vendor packages. Review a new
   recipe offline if required, including scoped anchors and whole-file candidate
   hashes. Do not simply change `originalSha256` to match unknown patched bytes.
4. Plan a supervised cleanup of the old project installation while preserving
   its evidence and the upgraded vendor files. Use the reviewed repair command below where its guards accept the state.
   Do not claim that `install` repairs occupied or drifted installations.
5. Once a clean target with the correct new originals is established, review a
   fresh install plan, install, and verify services, API, and browser.

## Upgrade repair commands

Read [repair](docs/repair.md). Both absolute recipe paths are required; the
previous recipe must match the installed manifest exactly:

```bash
previous_recipe=/absolute/path/to/installed-release/profiles/pve-manager-9.2.10.json
new_recipe=/absolute/path/to/new-release/profiles/pve-manager-9.2.21.json
"$manager" plan repair \
  --profile "$new_recipe" --previous-profile "$previous_recipe" \
  --expected-host "$node_name"
```

After reviewing that plan:

```bash
"$manager" repair \
  --profile "$new_recipe" --previous-profile "$previous_recipe" \
  --expected-host "$node_name"
"$manager" status --profile "$new_recipe" --expected-host "$node_name"
```

Keep the printed recovery-evidence directory. Repair preserves project files,
refreshes the original backups, and restarts only the affected Proxmox services.
After success, use the new recipe for future operations. Power-loss recovery is
manual; do not promise automatic repair of arbitrary drift.

## Development and release preparation

```bash
bash build/test.sh
bash build/package-release.sh 1.0.0-preview.1 /absolute/output/directory
```

Tests require .NET 10 and Node.js on the development machine. Packaging requires
reviewed committed source and an unused archive name. Preserve runtime license
notices. New C# classes should have individual files. Keep changes focused;
update relevant tests/docs without introducing an automatic recovery framework.
Do not publish private Git history when preparing a separate public repository.
