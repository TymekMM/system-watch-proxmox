# Updates

The small Proxmox bridge loads the separate GUI asset. UI/worker updates therefore do not repatch Proxmox files. Use the recipe currently recorded in the installation manifest and a manager compatible with it. After successful upgrade repair, use the new recipe. Repair preserves the installed UI and worker versions; update them separately when desired.

Run as root. Set `release_dir`, `recipe`, and `node_name` as in the installation guide. Download and verify the candidate package first. Its `SHA256SUMS` provides the expected asset hashes.

## GUI only

```bash
candidate=/absolute/path/to/new/assets/system-watch-ui.js
candidate_hash=REPLACE_WITH_RELEASE_UI_SHA256
"$release_dir/bin/SystemWatch.Management" plan ui-update   --profile "$recipe" --asset "$candidate" --sha256 "$candidate_hash" --expected-host "$node_name"
```

After reviewing the plan, run the same arguments with `ui-update` instead of `plan ui-update`. Only the UI asset and private manifest change; no service restart. Hard-refresh the browser afterward. A previous verified GUI asset can be restored using the same command with its hash.

## Worker only

```bash
candidate=/absolute/path/to/new/assets/SystemWatch.Worker
candidate_hash=REPLACE_WITH_RELEASE_WORKER_SHA256
"$release_dir/bin/SystemWatch.Management" plan worker-update   --profile "$recipe" --asset "$candidate" --sha256 "$candidate_hash" --expected-host "$node_name"
```

After reviewing the plan, use `worker-update` instead of `plan worker-update`. The project service stops, its existing worker slot and manifest update, and the service restarts. Verification checks a new worker instance and API access. Proxmox services and GUI remain unchanged.

If the update fails, the manager attempts to restore the previous worker and manifest and restart the previous service. Retain the console output if restoration is incomplete. Power loss between file publications still requires supervised inspection; updates have no automatic reboot recovery.

Never substitute a locally computed hash for the published expected hash merely to make a failed verification pass.
