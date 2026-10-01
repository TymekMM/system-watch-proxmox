# Security

The worker runs as root to read hardware and writes a private cached snapshot. The Proxmox API bridge requires authenticated access with node Sys.Audit permission. The installer modifies vendor files only when original/candidate hashes and scoped anchors match.

Report sensitive vulnerabilities privately to TymekMM@gmail.com. Do not post credentials, exploit details against a live host, or raw private snapshots in a public issue. There is no guaranteed response time; this is a community-maintained project.

No telemetry, donation checks, licensing server, or cloud service is required to run System Watch. Ordinary Proxmox services and operator-installed hardware utilities retain their own behavior.
