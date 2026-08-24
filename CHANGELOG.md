# Changelog

## 0.3.3 / safe-component-v1 - 2026-08-25

- Store scene handles as raw unsigned data and use the Unity 6000.5
  `SceneHandle.GetRawData()` API without breaking Unity 6000.3 compilation.
- Preserve the existing exact-handle cleanup and restoration behavior across
  both supported Unity Editor API shapes.

## 0.3.2 / safe-component-v1 - 2026-08-24

- Resolve the active scratch scene by exact handle or path before cleanup.
- Restrict legacy name fallback to a unique loaded-scene match so a pre-existing
  scene with the same filename is never discarded.

## 0.3.1 / safe-component-v1 - 2026-08-23

- Add the missing Unity `.meta` file for `LICENSE.md` so Git installs remain console-clean during asset refreshes.

## 0.3.0 / safe-component-v1 - 2026-08-23

- Make the package a standalone extension of the official `com.unity.pipeline` package.
- Rename the analyzer-safe component command to `safe_add_component`.
- Replace product-specific identity fields with a generic extension identity and capability declaration.
- Keep installation and direct CLI use independent of any external application.

## 0.2.0 - 2026-08-23

- Add analyzer-safe component creation with `TypeCache`, deterministic full and unique short-name matching, Unity Undo, and component `ObjectRef` output.
- Exercise the analyzer-safe component path in `pipeline_self_test`.

## 0.1.5 - 2026-08-22

- Update compatibility metadata for the validated package stack.
- Move `PipelineExtensionsScratchAsset` into its own same-name source file so Unity resolves its `MonoScript` without warnings.

## 0.1.4 - 2026-08-22

- Explicitly create and verify the per-session scratch directory and temporary scene.
- Persist structured bootstrap diagnostics under `pipeline_extensions_status.testSession.lastBootstrap`.

## 0.1.3 - 2026-08-22

- Use Pipeline's scene commands during temporary test-session setup.

## 0.1.2 - 2026-08-22

- Correct temporary scene activation and reacquisition on Unity 6000.3.

## 0.1.1 - 2026-08-22

- Correct Unity 6000.3 compilation compatibility and package diagnostics.

## 0.1.0

- Initial package with environment diagnostics, isolated test sessions, and a deterministic self-test.
