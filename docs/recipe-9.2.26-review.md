# Proxmox VE 9.2.26 recipe review

This recipe targets the exact vendor files observed after upgrade to
`pve-manager` 9.2.26, running build `41a0e2e09b9e6f9d`, on the test VM.

| File | Original SHA256 | Patched SHA256 |
| --- | --- | --- |
| `/usr/share/perl5/PVE/API2/Nodes.pm` | `675bf4d79c690bfef2d7c38f7de74010dacf3a4e55ac1049fc50942f10ade8f9` | `e9b4e9af4003fd70c2bd8afff3211a726427748dc7d23e66605311cccf0b76cf` |
| `/usr/share/pve-manager/js/pvemanagerlib.js` | `532fcf0ae6298a190e239e2f538246ff716535dd14890f349687c3a57bd35efe` | `06760a25bd13b0025ada271cda00001b119e6e6c422705f0c3e3fb10160ac104` |

The 9.2.21 edits and both shared snippets apply unchanged. Each anchor
matched exactly once in its required scope. An offline reproduction of
the renderer matched the retained 9.2.21 candidates byte for byte, then
confirmed that the same lines were added or replaced for 9.2.26.
The full 9.2.26 JavaScript candidate passed `node --check`.

On 2026-11-09, a Proxmox upgrade from 9.2.21 to 9.2.26 on the test VM
replaced both patched vendor files. System Watch's worker stayed active
and the manifest still recorded 9.2.21.

The rc.2 manager's read-only repair plan rendered both expected candidate
hashes and validated the Perl candidate on the VM. Applying repair
refreshed the original backups and manifest, retained the recovery
evidence, and restored the two hooks. The manager verified the worker
and authenticated API; status reported `file-complete`, the worker
service was active, and the operator confirmed the panels were visible
again in the Proxmox GUI.

For this upgrade path, follow [upgrade repair](repair.md) with the exact
previous recipe recorded in the manifest and the new 9.2.26 recipe
alongside the unchanged shared snippets. This recipe works with the
existing rc.2 manager; it was added to source after that release.

Clean installation and removal on 9.2.26 have not been live-tested.
