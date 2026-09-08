# Unity Pipeline Extensions

Small, standalone authoring extensions for the official `com.unity.pipeline` package.

The package currently provides:

- `safe_add_component` — analyzer-safe component creation using `TypeCache`, deterministic full/short-name matching, Unity Undo, and structured component `ObjectRef` output.
- `pipeline_extensions_status` — version, editor, scene, authoring, capability, and test-session diagnostics.
- `begin_test_session` — creates and verifies an isolated temporary scene and scratch directory.
- `end_test_session` — restores the original active scene and cleans up temporary assets.
- `pipeline_self_test` — validates ObjectRef round-tripping, safe component creation, transforms, serialized data, scratch assets, and cleanup.
- `project_audit_start`, `project_audit_status`, `project_audit_results`, `project_audit_dispose` — optional structured Project Auditor analysis with bounded snapshots and pagination.

Commands are discovered through the official Pipeline package's existing `[CliCommand]` mechanism. The package has no external service requirement and can be used directly through the Unity Pipeline CLI.

## Requirements

- Unity 6000.5 or later
- `com.unity.pipeline` version `0.6.0-exp.1`

The Pipeline dependency is declared in `package.json` and is resolved by Unity Package Manager.

## Install

In **Window > Package Management > Package Manager**, select **+ > Install package from git URL** and enter:

```text
https://github.com/Ju1ians/unity-pipeline-extensions.git#v0.5.0
```

The version tag pins a reproducible revision; Unity records the resolved commit in
`packages-lock.json`. During local development, use **Add package from disk** and
select this repository's `package.json`.

### Migrating from the standalone Auditor package

Version 0.5.0 includes the former `com.julianketter.unity-pipeline-project-auditor`
package as an internal module. **Do not install both packages together:** they
contain the same Auditor assembly and command names.

Close the Editor first. In one edit to `Packages/manifest.json`, remove
`com.julianketter.unity-pipeline-project-auditor` from `dependencies` and update
`com.julianketter.unity-pipeline-extensions` to the Git URL above. Remove the old
Auditor package from `testables` if present (use the Extensions package name if
you want its tests). Reopen Unity and let UPM regenerate the lock file. Preserve
copies of manifest and lock files first. If another package depends on the old
Auditor package, update that dependency before migrating.

Gateway's consolidated-package installer automates this manifest migration with
an external rollback transaction. It does not remove unrelated dependencies or
edit external package repositories.

### Optional Auditor module

Auditor remains isolated under `Modules/ProjectAuditor` with its own Editor
assembly and public-API adapter. The package compiles without Project Auditor;
its audit commands return `unavailable` when the API or required rules are absent.
Other extension commands remain usable. Install Unity's matching Auditor/rules
when analysis is needed; these are not forced package dependencies.

The four command names and `unity-pipeline-project-audit/v1` response schema are
unchanged. See [Auditor module documentation](Modules/ProjectAuditor/README.md).
No Gateway dependency or role enforcement is included in this Unity package.

## Use independently

For example, after installing the official Pipeline CLI:

```powershell
unity command safe_add_component --target '{"hierarchyPath":"/Example"}' --type UnityEngine.Rigidbody --project-path C:\path\to\project
```

Run `unity command pipeline_extensions_status` to inspect the installed extension identity and advertised capabilities.
