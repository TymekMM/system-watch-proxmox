# Proxmox 9.2.21 recipe review

Reviewed input: files copied from an upgraded node reporting
`pve-manager 9.2.21/4f6e0ac86f9e8c7f`.

| Target | Original SHA256 | Candidate SHA256 |
| --- | --- | --- |
| `Nodes.pm` | `df97e542b4494513ab50dc23b8a87fb1ba3f0a749b5f4cbfb1f3565e32791fc6` | `0b2421e30e9c6d0ad2ad93ec9773ab6add2813706f6b324ac0f0ffa390f88e5a` |
| `pvemanagerlib.js` | `9c76edc634ecc34260cfcb3c8374c37f5d1b8a665994abea7edfd40b4aed327c` | `210b3bd8abf49c5fe15537a3ed86cf8574aeb2daa8768ead420a43865e83894f` |

The existing 9.2.20 edit operations and shared snippets were reused unchanged.
The C# recipe renderer accepted every uniqueness/scoping guard and reproduced
both pinned candidate hashes. The frontend candidate passed `node --check`.
The backend is byte-identical to the already tested backend candidate.

## Live upgrade repair validation

On 2026-10-02, a real host was upgraded normally from the reviewed 9.2.10
build to 9.2.21 with System Watch left installed. No special pre-upgrade
installation archive was used as an input to repair.

- The backend was replaced with the reviewed unpatched original.
- The frontend was replaced with the new 9.2.21 original.
- The UI asset and active worker remained installed; the manifest still described
  the previous build.
- Preview.2's read-only repair plan validated the previous manifest/recipe,
  project files, backups, root ownership, effective service, and Perl candidate.
- Repair retained the old recovery evidence and published new original backups,
  updated the manifest, reapplied both hooks, and restarted Proxmox services.
- The active worker, fresh snapshot, authenticated API and unauthorized-request
  denial passed the manager's verification. The operator confirmed the browser
  panels worked after a hard refresh.
- A separate guarded UI-only update installed the latest source/donation links
  and enlarged display settings window, without service restarts.

The repair manager came from source revision
`7c2b6bf` in `1.0.0-preview.2`. The worker and UI were preserved during repair.
The subsequently installed UI hash was
`b4c9313fba039dda3fefa42d9028ed12655ec304d13c9102859acc30a40f438d`.

This validates the upgrade-repair route on the stated build. Clean installation
and live removal on 9.2.21 have not yet been tested; fixture tests cover removal
restoring upgraded originals. A different file hash still requires review.

After a vendor upgrade, an occupied installation cannot use the clean `install`
command. Do not use a previous-version `remove` operation to restore old vendor
files. Upgrade repair must verify the new originals, preserve project-owned
files, archive the previous manifest/backups, back up the new originals, and
publish the new hooks and manifest consistently. The new `plan repair` / `repair` commands implement this supervised path; see
[repair](repair.md). Older preview managers do not have these commands.
