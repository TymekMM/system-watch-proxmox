# Development

Build on Linux with a .NET 10 SDK, Node.js for GUI tests, and standard shell tools. The production package is currently Linux x64 only. No Python runtime is required by the implementation, packaging, or retained tests.

```bash
dotnet build src/SystemWatch.Management -c Release
dotnet build src/SystemWatch.Worker -c Release
bash build/test.sh
```

Tests use offline fixtures and fake service commands; they do not install onto the development machine. Recipe fixtures cover ambiguity, unknown hashes, installation/removal, UI updates, worker updates, and restoration after failed startup. Independent-polling tests block slow sources while checking continued fresh publications.

## Package one release

Commit reviewed changes first; packaging refuses a dirty source tree. Choose the intended version, for example:

```bash
bash build/package-release.sh 1.0.0-preview.1
```

The script builds the current worker and manager, packages the current GUI/recipes/service template, writes `assets/release-inputs.json`, and produces internal and external SHA256 files. It records the source revision and SDK version. Binary hashes vary with builds; no private machine's binary hash is hard-coded into the installer. The manager compares asset bytes against the package's recorded hashes. Authenticate the downloaded package separately through its trusted release channel.

A source build of the manager without a release asset directory can still perform recipe review/status/removal; fresh installation needs the packaged assets and their manifest.

Self-contained publish includes native libraries in the executable for extraction; the worker uses its private runtime extraction directory. License/third-party files are included in the package. Provide a matching source archive alongside the binary release.

## Architecture

- `SystemWatch.Contracts`: typed snapshot models and semantic validation.
- `SystemWatch.Collector`: read-only probes, independent caches, assembly, and atomic snapshot publication.
- `SystemWatch.Worker`: explicit-host CLI and worker process.
- `SystemWatch.Management`: recipe renderer, supervised install/remove/upgrade repair, and guarded updates.
- `profiles`: exact version-specific edits and small API/UI hooks.
- `integration/proxmox`: the active GUI asset and C# service template.

## Public-release validation

The regression suite covers install/remove, GUI and worker updates, upgrade repair, rejection of unknown bytes, and restoration after simulated failures. Live preview installation and GUI updates were verified on the reviewed 9.2.20 build. Live upgrade repair from 9.2.10 to 9.2.21 and a subsequent GUI update were verified on a real host; see the [validation record](recipe-9.2.21-review.md).

The GitHub-source 1.0.0-rc.1 package was also removed and reinstalled on the reviewed 9.2.20 test VM. Both restored vendor originals matched the recipe hashes; status was clean with retained backups, and the operator confirmed the GUI after reinstallation. Clean installation and live removal on 9.2.21 remain untested. Recipe compatibility still requires exact hashes, not just a matching version number.

The public source excludes the historical terminal monitor. Its development history is retained separately from the GitHub release source.
