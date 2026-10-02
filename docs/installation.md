# Installation

For a complete walkthrough with commands, example output and explanations, see the [beginner tutorial](beginners-guide.md).

## Requirements

- A reviewed Proxmox VE build, Linux x64, and root access on the monitored node.
- Standard Proxmox tools: `pveversion`, `pvesh`, Perl, `systemctl`, `zpool`, `lsblk`, `readlink`, `stat`, and `curl`.
- `smartmontools` for SMART readings. `ipmitool` is optional and requires working BMC/IPMI hardware and a kernel interface.
- Optional supported controller tool `/usr/local/sbin/perccli` for the example HBA module. It is not bundled.
- A clean installation target. Existing System Watch installations are refused; migration from earlier private releases is outside this public installer.

Missing optional probes produce unavailable/error states. Hide unwanted sections with the panel settings button. No hardware management actions are performed.

## Verify and extract the package

Download the archive and its checksum file from the same reviewed release. In the download directory:

```bash
sha256sum -c system-watch-proxmox-1.0.0-linux-x64.tar.gz.sha256
mkdir system-watch-release
tar -xzf system-watch-proxmox-1.0.0-linux-x64.tar.gz -C system-watch-release
cd system-watch-release
sha256sum -c SHA256SUMS
```

Use the actual package version in its filename (preview packages have preview version names). Archive checksums detect changed bytes; they are not digital signatures and do not independently authenticate the publisher. Keep the package directory, including its recipes and manager, for future updates/removal.

## Review first

Run as root, from the extracted package directory. For the reviewed 9.2.10 build:

```bash
release_dir=$(pwd -P)
node_name=$(hostname -s)
recipe="$release_dir/profiles/pve-manager-9.2.10.json"
"$release_dir/bin/SystemWatch.Management" plan install   --profile "$recipe" --directory "$release_dir/assets" --expected-host "$node_name"
```

Select `pve-manager-9.2.20.json` or `pve-manager-9.2.21.json` for the corresponding reviewed build. The 9.2.21 recipe has been live-tested through upgrade repair; a clean 9.2.21 installation remains to be tested. Review the target paths, original/candidate hashes, backups, and service actions. Unknown originals, occupied project paths, service drop-ins, or an existing installation block the plan. The plan reads files and checks Perl syntax but changes no installed files or services.

## Install

With the same shell variables after a successful review:

```bash
"$release_dir/bin/SystemWatch.Management" install   --profile "$recipe" --directory "$release_dir/assets" --expected-host "$node_name"
"$release_dir/bin/SystemWatch.Management" status   --profile "$recipe" --expected-host "$node_name"
```

The five actions are: validate; back up both originals; publish one private manifest and the five targets; start/reload services; verify a recent snapshot, authenticated API access, and unauthorized-request denial. Then hard-refresh the browser and inspect the node Summary page yourself.

`file-complete` status means recognized file hashes/modes; it does not independently verify running service health or browser rendering. Install verification is separate. Probes populate at different speeds; startup can briefly show unavailable readings.

Installed worker: `/opt/system-watch-proxmox/worker/SystemWatch.Worker`.
Service: `system-watch-proxmox.service`.
Snapshot: `/run/system-watch-proxmox/snapshot.json` (root-only).
Backups and manifest: `/var/lib/system-watch-proxmox` (root-only).

## Remove or restore an incomplete installation

```bash
"$release_dir/bin/SystemWatch.Management" remove   --profile "$recipe" --expected-host "$node_name"
```

Removal verifies recognized hashes and both backups, stops/disables the project service, restores the vendor originals, removes known project files, and reloads/restarts services. Original backups are retained. Unknown edits or missing backups block automatic removal; see troubleshooting. Failed installation attempts the same cleanup and reports remaining changes.

This is a supervised installer. There is no automatic reboot recovery. After an interruption, inspect `status`; do not run competing installer/update operations. After a Proxmox upgrade, do not restore a backup from an older package over newer vendor files.
