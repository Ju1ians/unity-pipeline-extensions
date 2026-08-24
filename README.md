# Unity Pipeline Extensions

Small, standalone authoring extensions for the official `com.unity.pipeline` package.

The package currently provides:

- `safe_add_component` — analyzer-safe component creation using `TypeCache`, deterministic full/short-name matching, Unity Undo, and structured component `ObjectRef` output.
- `pipeline_extensions_status` — version, editor, scene, authoring, capability, and test-session diagnostics.
- `begin_test_session` — creates and verifies an isolated temporary scene and scratch directory.
- `end_test_session` — restores the original active scene and cleans up temporary assets.
- `pipeline_self_test` — validates ObjectRef round-tripping, safe component creation, transforms, serialized data, scratch assets, and cleanup.

Commands are discovered through the official Pipeline package's existing `[CliCommand]` mechanism. The package has no external service requirement and can be used directly through the Unity Pipeline CLI.

## Requirements

- Unity 6000.3 or later
- `com.unity.pipeline` version `0.5.0-exp.1`

The Pipeline dependency is declared in `package.json` and is resolved by Unity Package Manager.

## Install

In **Window > Package Management > Package Manager**, select **+ > Install package from git URL** and enter:

```text
https://github.com/Ju1ians/unity-pipeline-extensions.git#v0.3.2
```

The version tag pins a reproducible revision; Unity records the resolved commit in
`packages-lock.json`. During local development, use **Add package from disk** and
select this repository's `package.json`.

## Use independently

For example, after installing the official Pipeline CLI:

```powershell
unity command safe_add_component --target '{"hierarchyPath":"/Example"}' --type UnityEngine.Rigidbody --project-path C:\path\to\project
```

Run `unity command pipeline_extensions_status` to inspect the installed extension identity and advertised capabilities.
