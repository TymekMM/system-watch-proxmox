# Proxmox patch recipes

Each JSON recipe describes a reviewed `pve-manager` version, two allowed vendor targets, exact original hashes, and ordered bounded edits. The adjacent Perl method and JavaScript bridge are part of the recipe. Keep their exact bytes and line endings unchanged unless reviewing new candidate hashes.

The first installer supports exactly these files:

- `/usr/share/perl5/PVE/API2/Nodes.pm`
- `/usr/share/pve-manager/js/pvemanagerlib.js`

Recipes are data, not shell scripts. They cannot authorize arbitrary target paths or execute installation commands. This initial implementation supports two targets; support for additional target types would require a reviewed installer change.

## Matching an insertion

`replace-once` replaces an exact anchor. `insert-before-once` inserts the permitted snippet before an exact anchor. Frontend edits can specify `region.start` and `region.end` to restrict searching to the Summary component. Both region boundaries must occur exactly once in the whole file, and the anchor exactly once inside that region. Missing or ambiguous boundaries/anchors cause refusal, never a best-effort patch.

`insert-before-region-start` places the external bridge before the component. These edits keep the hooks small; normal GUI updates only replace the external UI asset.

## What is candidateSha256?

`originalSha256` identifies the **entire original vendor file**. `candidateSha256` identifies the **entire resulting file after all edits**, not just the inserted snippet. SHA256 is a content digest: a one-byte difference changes the expected result. The installer verifies both ends, so a recipe cannot accidentally patch a different build or silently produce an unreviewed output.

This is an integrity check, not a digital signature. Obtain recipes from a trusted release and review changes.

## Reviewing another build

Work from pristine matching vendor files on a disposable test node. Copy a recipe, update its version/identity and scoped anchors, set `review` to `draft`, and set candidate hashes to `null`. The renderer accepts numeric `major.minor.patch` recipe versions; shipped recipes cover the reviewed 9.2.10, 9.2.20, and 9.2.21 builds.

```bash
./bin/SystemWatch.Management review-recipe   --file /absolute/path/to/draft.json --expected-host "$(hostname -s)"
```

Review output diffs, syntax, authentication, freshness, and browser integration. Record the returned whole-file candidate hashes and set `review` to `reference-reviewed` only after validation. Drafts never authorize installation. Run fixture tests and a read-only installation plan before a supervised trial.

The 9.2.10 and 9.2.20 original/candidate hashes are unchanged from the tested private release. The 9.2.21 recipe reuses the same edit rules with its own frontend original/candidate hashes; see [live validation](../docs/recipe-9.2.21-review.md). New recipes do not require recompiling the C# executable when they use the supported targets and edit operations.
