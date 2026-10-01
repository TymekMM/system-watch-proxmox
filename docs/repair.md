# Repair hooks after a Proxmox upgrade

An upgrade may replace the Proxmox files containing the hooks while retaining
System Watch's worker, UI asset, service unit, and old installation manifest.
Use `repair` to reapply hooks for a reviewed new build without reinstalling the
worker or changing browser settings.

The 9.2.10 → 9.2.21 repair route has been verified on a real host; see the
[validation record](recipe-9.2.21-review.md).

## Requirements

- The previous recipe must match the installed manifest byte for byte. Keep the
  original release directory; do not substitute an edited copy of its recipe.
- The new recipe must match the running `pve-manager` version and exact originals.
- Worker, UI, unit, and previous backups must still match the recorded manifest.
- The worker must be active, with the recorded unit loaded and no drop-ins.
- Each vendor file must be the new reviewed original, or a surviving recorded
  hook whose verified original backup matches the new recipe's original hash.
  Unknown bytes, missing files, and stale hooks on a different original are refused.

Fresh installations use `/opt/system-watch-proxmox/worker/SystemWatch.Worker`.
Repair also recognizes the two earlier manifest-recorded C# slots
`csharp-fb6ee16` and `csharp-bc265f7`; it keeps the existing path. Arbitrary worker
paths and legacy Python installations are refused.

## Review first

Run as root with an explicitly chosen node. Verify the candidate manager package
and recipes before use. Set absolute paths:

```bash
node_name=node-example
manager=/absolute/path/to/release/bin/SystemWatch.Management
previous_recipe=/absolute/path/to/installed-release/profiles/pve-manager-9.2.10.json
new_recipe=/absolute/path/to/new-release/profiles/pve-manager-9.2.21.json
"$manager" plan repair \
  --profile "$new_recipe" --previous-profile "$previous_recipe" \
  --expected-host "$node_name"
```

The plan validates the backend candidate with Perl and checks installed files,
backups, ownership, and the effective worker service. It writes no files and
restarts no services.

## Apply and check

After reviewing the plan:

```bash
"$manager" repair \
  --profile "$new_recipe" --previous-profile "$previous_recipe" \
  --expected-host "$node_name"
"$manager" status --profile "$new_recipe" --expected-host "$node_name"
```

Repair retains private recovery evidence under
`/var/lib/system-watch-proxmox-history/<unique-directory>` and prints the path.
It includes the old manifest/backups, pre-repair vendor bytes, new vendor
originals, and proposed manifest. Both new original backups and the manifest
are published before the candidate hooks. Each target uses atomic file
replacement with an expected predecessor hash.

The worker, UI asset and service unit remain untouched. `pvedaemon` and
`pveproxy` restart; then the active worker, fresh snapshot, API response and
unauthorized-request denial are verified. Hard-refresh the browser and inspect
Summary. Use the new recipe for subsequent status/update/removal operations.
Removal now restores the new build's originals, not the previous build's files.

Repair deliberately preserves the currently installed GUI version. New links,
layout changes, or other GUI improvements in the repair package do not appear
until you run a separate [UI-only update](updates.md). Browser-saved panel heights
and source visibility remain local to the browser/node.

## Failures and interruption

On a caught failure, repair attempts to restore exactly the pre-repair vendor
files, old backups, and old manifest. It never rolls Proxmox back to the previous
package version. It reports an incomplete restore and the evidence directory
if unknown bytes or another failure block restoration.

This is supervised repair, not a transaction journal or automatic reboot
recovery. Power loss between backup/manifest replacements may leave inconsistent
state requiring manual inspection using the retained evidence. Do not delete the
manifest, replace expected hashes, run `remove` with an old recipe after an
upgrade, or replay service restarts blindly. A refused operation is a reason to
inspect, not to bypass its checks.
