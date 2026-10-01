# Troubleshooting

## Missing or grey readings

Inspect `systemctl status system-watch-proxmox --no-pager` and `journalctl -u system-watch-proxmox -n 100 --no-pager`. Missing hardware/tools are expected on some hosts. Stale readings are marked unknown; hiding a source in the GUI does not stop its collector.

`ipmitool` alone does not create a BMC. Local IPMI needs supported hardware and a working `/dev/ipmi*` interface. Do not force kernel configuration for hardware that is absent. The HBA probe is an example module for supported `perccli` output, not a universal controller reader.

## Recipe path and validation errors

Use an absolute path with `--profile` (and `--previous-profile` for repair). A relative recipe path is rejected explicitly. On a clean host, status reports the underlying recipe validation error, including an unknown original or a candidate mismatch; do not assume every validation failure means the vendor files changed.

## Unknown vendor hash

Check `pveversion -v`. An unreviewed build or previous patch is refused. Do not change the recipe hash to bypass the refusal. Compare original files with the matching vendor package and review a new recipe offline.

## Status says file-complete but the panel is absent

Verify the service is active, inspect a recent snapshot, and hard-refresh the browser. The cached API requires Proxmox authentication and node `Sys.Audit` permission. Snapshot freshness, file permissions, and node identity are checked by the bridge. Never expose the root-only snapshot through a separate unauthenticated web server.

## Interrupted install/update or changed installed file

Run `status` with the original recipe. Preserve `/var/lib/system-watch-proxmox` and the output. `remove` restores only recognized candidates using matching verified originals. A manually edited file or missing backup requires investigation; do not blindly overwrite it or delete the manifest.

## Proxmox upgrades

Prefer removing System Watch before upgrading `pve-manager`, then install with a reviewed recipe for the new build. Vendor upgrades may replace the hooks. If an upgrade already happened, retained backups belong to the previous build; restoring them over upgraded files is unsafe. Use [guarded repair](repair.md) if a reviewed new recipe and intact recorded project files are available. There is no automatic compatibility guarantee or package-manager hook.

## Repaired panels still show the previous GUI layout

Repair reapplies vendor hooks and preserves the installed UI asset. It does not
install a new GUI just because the repair package contains one. Use a reviewed
UI-only update with the new manifest's recipe, then hard-refresh. Browser-saved
preferences remain per node; the settings window layout comes from the UI asset.

## Report a problem

Use GitHub issues for reproducible bugs. Include System Watch version, `pve-manager` version, command output, and relevant source states. Remove hostnames, addresses, serial numbers, tokens, and other private data before uploading logs or snapshots. Use the security contact for sensitive reports.
