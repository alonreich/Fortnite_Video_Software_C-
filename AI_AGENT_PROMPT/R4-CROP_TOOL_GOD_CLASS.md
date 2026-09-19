> **STATUS: OPEN.** Refactoring plan. Nothing here has been applied.

# TASK SPECIFICATION: R4 - CROP_TOOL_GOD_CLASS

## 1. TARGET CONTEXT & SCOPE
- File Path: `src/FortniteVideoSoftware.App/CropToolWindow.axaml.cs`
- Target Range: whole file — **6,710 lines, 135 fields, 172 methods, 35 event subscriptions.** Worst methods: `WireEvents` 311 (line 560), `PositionRolePopup` 171, `BuildMaskOverlayUi` 171, `RunMagicWandAsync` 134, `SaveConfigAsync` 133, `OnKeyDown` 133, `CreateItem` 132, `SaveAndReturnAsync` 120.
- Defect Classification: Modernization Bottleneck (single-responsibility violation)

## 2. PROBLEM ANALYSIS & ROOT CAUSE
- Flaw Mechanism: measured clusters by reference count in this one file:
  ```
    Role 365 | Snapshot 290 | Canvas 202 | Zoom 201 | Drag 201 | Handle 138
    Hud 96 | Mask 90 | Config 90 | Timer 63 | Undo 33 | IpcClient 25
  ```
  One class holds: the HUD role taxonomy and its popup positioning, frame snapshot capture (spawning ffmpeg and writing PNGs to temp), a magic-wand image-segmentation routine, mask overlay UI construction, a resize-handle drag state machine, a zoom viewport, cross-process config persistence, and an undo stack. The magic wand and the role popup have nothing to do with each other yet share a 135-field surface.
- ⚠️ `docs/INDEX.md` binds this file to the crop/HUD domain and it persists `CropConfigStore` state consumed by a SEPARATE PROCESS. A regression here is not contained to one window — it corrupts the config the Main App reads back.
- Target Pattern: same collaborator-extraction playbook as R2. `HudRoleCatalog` (the `RoleByKey` static already exists — grow it into the owner), `SnapshotService` (ffmpeg invocation + temp PNG lifecycle), `MagicWandSegmenter` (pure image op, belongs in `Core/Media` next to `HudImageOps`), `MaskOverlayBuilder`, `CropHandleDragController`, `CropViewport`.

## 3. GUARDED LOGIC & INVARIANT PRESERVATION
- CRITICAL: Do NOT delete, bypass, or discard existing validation rules, legacy fallbacks, or edge-case handling present in this block.
- Explicitly Guarded Behavior:
  - `CropConfigStore`'s on-disk shape and its backup/quarantine path are a CROSS-PROCESS contract. A config written by the refactored build must still load in an older build and vice versa. Do not reshape the JSON.
  - `HudConfig.ZDefaults` layering order — the z-order of HUD elements is calibrated, not alphabetical.
  - Snapshot temp files (`crop_snapshot_*.png`, `crop_canvas_trick_*.png`, `crop_item_*.png`) are GUID-named in `_paths.TempDirectory`; keep the naming and keep them out of user folders.
  - Pointer-capture release on handle drags, and the suppression of rebuilds while a handle drag is live.
  - The `run-ui` self-relaunch at line ~5471 and the `ReleaseLockOnly()` handoff semantics — this window participates in the process-handoff protocol; breaking it makes the next launch report a phantom crash.
- Public Interface Parity: constructor, result payload and the handoff entry points stay identical.

## 4. STEP-BY-STEP REFACTORING INSTRUCTIONS
1. Pin behaviour first: a scripted run (open → snapshot → magic wand → adjust two handles → save) and the resulting `CropConfigStore` JSON as the oracle.
2. Extract in this order, one per commit: `MagicWandSegmenter` (pure, move to `Core/Media`, unit-testable immediately) → `SnapshotService` → `HudRoleCatalog` → `MaskOverlayBuilder` → `CropHandleDragController` → `CropViewport`.
3. Break `WireEvents` (311 lines) up as each collaborator takes its own wiring.
4. Every `+=` gets a matching `-=` on close; there are 35 today.
5. Do NOT change `SaveConfigAsync`'s write protocol — it already uses the atomic path.

## 5. DEFINITION OF DONE & VERIFICATION
- Compilation: zero new warnings.
- Behavioral: oracle JSON byte-identical; old-build config loads in new build and new-build config loads in old build.
- Structural: no type above 1,200 lines, no method above 120 lines, window's own private field count below 50.
- Performance: magic wand and snapshot latency no worse than baseline.
