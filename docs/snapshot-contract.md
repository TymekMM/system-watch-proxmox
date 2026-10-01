# System-Watch snapshot contract

Status: 1.0.0-draft.2 — reviewable design, not a released API or collector.
The example is synthetic, not a live reading from a monitored node. The schema uses JSON
Schema draft 2020-12. No backend transport or Proxmox changes are included.

## Scope and representation

Top-level sections: snapshot, system, health, sources, temperatures, fans,
devices, zfs. All schema properties are required; unavailable values are null.
Empty lists mean no entries in this snapshot; consult inventory/source state
before interpreting them as absence. No failed read is converted to zero.
Load averages are ordered 1, 5, 15 minutes. Times are UTC RFC3339 with Z suffix.
Bytes and potentially large cumulative counters are unsigned decimal strings
so JavaScript cannot silently lose precision. Rates, temperatures, percentages,
RPM, ages and durations are JSON numbers. Percentages are 0–100.

## Identity and relationships

IDs are opaque and unique within each entity array. source_id, device_id,
temperature_ids, pool_ids and hottest_temperature_id must resolve. Pool/vdev
GUIDs use string representation. Vdev IDs are unique across the snapshot;
parent links stay within a pool and must be acyclic with one root.
Physical disks use a persistent WWN or serial plus a collision-safe namespace;
fall back to a session identity when no reliable hardware identity exists.
Never use /dev/sdX or hwmonN as a persistent identity. hwmon sensors use the
resolved device identity plus sensor channel; labels are presentation only.
Each physical disk appears once. Partitions used by ZFS map back to that disk;
the vdev name retains the partition path. Multiple pools may reference one disk.
Pool membership is unknown if ZFS inventory fails. Only confirmed nonmembers
appear in Other disks. Nonmember does not imply unused or safe to modify.
Controllers passed through to a VM are outside host collection scope.

## Readings and freshness

observed_at is the last successful observation, not the last attempted read.
age_seconds is measured from that observation using monotonic time within the
collector instance; wall-clock jumps must not make old readings fresh.
A fresh reading requires a value, an observation, and age <= stale_after_seconds.
A retained value after that limit is stale. No successful reading is unavailable:
value, observed_at and age_seconds are null. An unavailable reading cannot be ok.
last_attempt_status and reason describe failed/skipped attempts independently
of cached value freshness. An expected standby skip does not make its entire
source faulty. Do not persist cached readings across collector restarts in v1.
Initial proposed stale limits: HBA 6s, IPMI 15s, SMART 30s, hwmon/system/ZFS 3s.
These are explicit policy defaults, not properties of the existing monitor.
The example uses 30s for its illustrative readings; collector configuration
will supply source-appropriate limits. The browser also watches snapshot age:
freeze/stale indication when polling fails or snapshots stop advancing; never
leave old green status looking live. instance_id plus sequence detect restarts
and repeated snapshots. Publish each complete snapshot atomically.

## Temperature and health policy

Threshold comparisons are inclusive: >= critical means critical; otherwise
>= warning means warning. Below configured limits means ok. Thresholds absent,
missing values, and stale/unavailable readings mean unknown. A stale value may
still be displayed with its age, but cannot claim current thermal health.
Critical must be >= warning when both are defined. Signed and fractional
Celsius values are representable; do not retain the shell parser's unsigned
integer limitation in the new contract.

| Category | Warning C | Critical C |
| --- | ---: | ---: |
| CPU, k10temp | 75 | 90 |
| Motherboard, card side | 55 | 70 |
| Memory | 70 | 84 |
| Network | 70 | 85 |
| HBA | 75 | 85 |
| HDD / SATA or SAS SSD | 45 | 55 |
| NVMe | 65 | 75 |

Per-device overrides are supported by the contract. Unclassified sensors have
no invented thresholds and therefore unknown health. Every relevant hwmon
NVMe channel may be exposed separately, alongside SMART composite temperature.
Collectors deduplicate physical device queries; multiple distinct sensors
remain separate. Hottest means highest fresh Celsius value across all exposed
temperature sensors, regardless of category or severity. A cooler HDD can be
critical while the hottest CPU is healthy. Equal temperatures break ties by ID.

health.system retains legacy-v3-temperature-scope: IPMI CPU, motherboard,
card side, HBA and available memory channels; legacy_system_member marks inputs.
NIC, CCD, disk and fan health are shown separately. Worst known fresh severity
wins. If none are known, state is unknown. If some expected readings are missing
or stale, coverage is partial; even an ok result must display partial coverage.
Optional unpopulated DIMM channels are not failures. Retain discovered expected
sensors through transient failures so disappearance cannot silently restore
complete coverage. The collector must distinguish unavailable discovery from
successful discovery of no optional sensors. This improves failure visibility
without broadening the legacy thermal summary scope.

Discover fans from IPMI RPM records. Map recognized IPMI normal/warning/critical
status to health; unknown status remains unknown. RPM alone does not establish
fan health, and zero RPM alone does not establish failure. Preserve raw status.

## All-disk collection and standby

Discover host-visible physical HDDs/SSDs independently of ZFS. Deduplicate aliases
and partitions; exclude loop, optical and purely logical block devices from
physical-disk polling. Unresolved devices remain explicitly unknown. Refresh
inventory separately from temperatures (proposed 60s and 10s respectively).
Use a supported non-waking query path. If wake avoidance cannot be established
for a transport/controller, skip by default with a reason; do not silently wake
it. No SMART tests, repairs or drive power changes are performed. A sleeping
non-ZFS disk remains in inventory with its cached stale temperature or null.

## ZFS

Preserve raw pool and vdev state strings even when unrecognized. Proposed
severity: ONLINE/AVAIL -> ok, DEGRADED -> warning; FAULTED/UNAVAIL/SUSPENDED/
REMOVED -> critical; OFFLINE -> warning; unknown states -> unknown. Positive
error counters or permanent errors raise health to at least warning. The UI
can highlight positive counters red independently of aggregate severity.
Never downgrade a faulted pool to a generic degraded state.
Global ZFS summary uses worst known pool severity with coverage tracking. A
successful inventory with no pools yields unknown with reason no_pools, not
ALL ONLINE. Failed discovery yields unknown or partial coverage if retained
known pool data exists. pool_count is null on failed discovery; otherwise it
matches the number of pools. Stale pool data cannot claim current health.

Preserve topology and separate allocation classes (data, special, log, cache,
spare, dedup). Never sum parent and child counters together. An absent or
unparseable counter stays null. Device/vdev errors can be displayed directly;
there is no ambiguous aggregate error count in this initial contract.
Capacity uses allocated pool bytes / pool size, not dataset available space.
Fragmentation unsupported/unavailable is null. Preserve observed capacity
percentage rather than requiring equality with rounded human-readable sizes.

scan.type and scan.state are independent. A completed resilver is not a running
scrub. Progress is nullable and comes from the source, not guessed from an
unrelated byte count. Distinguish scan and issue rates. ETA is an estimate.
Keep completed/canceled operation information until the source replaces it.
No operation: type/state none and all other scan fields null. Unknown parsing:
unknown state and raw_text, rather than invented progress or completion.
Permanent errors distinguish none, present and unknown; count can be null even
when present. Do not expose affected file paths in the initial API.

## Validation and evolution

The schema validates shapes, required fields, enums, bounds and number formats.
A semantic validator must additionally enforce cross-references, unique IDs,
acyclic topology, threshold ordering, freshness/value consistency, membership
symmetry, pool counts, health aggregation and scan-state relationships. It must
also enforce UTC Z timestamps. These cannot be inferred from shape validation.
Collection timestamps may differ within a snapshot due to independent caches.

Strict producer validation rejects unknown fields. Future consumers should
ignore additive fields in supported major versions; breaking changes require
a new major version. The existing schema identifier is retained for compatibility; any future breaking change requires a new major schema version.

## Observed IPMI metadata and disk usage (draft.2)

`device.usage` is independent of ZFS membership: `system` identifies the
Proxmox boot disk even when it is outside ZFS; `other` requires a positive
classification; `unknown` is appropriate when the disk has no verified role.
No partition, mount or VM configuration match does not prove a disk unused.

Each temperature preserves IPMI's raw `reported_status` and six reported
hardware thresholds in `source_thresholds_celsius`, when available. The
dashboard's `warning_celsius` / `critical_celsius` retain the collector
policy; source thresholds do not silently override it. Raw `na` means no
reading or status from IPMI, not a temperature of zero. A null source threshold
object means this source did not expose IPMI-style threshold metadata; a
non-null object with null members represents individually unreported limits.

Fans likewise preserve `reported_status` and `source_thresholds_rpm`.
The BMC reports `na` for unpopulated fan headers. Discover all IPMI fan
headers, but distinguish expected connected fans from optional headers: a
previously reporting fan becoming unavailable is a loss of coverage; an
unpopulated header is not by itself a failed fan. Do not derive alarm state
from RPM alone when BMC status is available.

The IPMI threshold order is lower nonrecoverable, lower critical, lower
noncritical, upper noncritical, upper critical, upper nonrecoverable. Thresholds
and raw statuses are metadata, not a second policy engine. At the observed
sample, motherboard had an upper noncritical threshold of 50 C, CPU 95 C;
the legacy software warning temperatures are 55 C and 75 C respectively.

The 4 host-visible Seagate ST91000640NS disks outside ZFS returned active/idle
and 31–34 C via `smartctl -j -d sat -n standby,3 -A`; the response's
`temperature.current` was used. This worked for those four devices but does not
prove the standby guard will avoid wakeups on every controller. For NVMe,
expose each available hwmon Composite and Sensor N channel at millidegree
precision; the system NVMe returned three channels in this observation.
No production-drive serial numbers belong in public fixtures.
