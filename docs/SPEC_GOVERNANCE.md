# SPECIFICATION GOVERNANCE: ZERO-LEAKAGE CONTRACT

## Code Mini-Map: Bound Source Files & Governance Symbols
| Source File Path | Key Classes, Records & Controls | Core Bound Methods, Properties & Symbols | Subsystem Domain Role |
| :--- | :--- | :--- | :--- |
| `docs/SPEC_GOVERNANCE.md` | Governance Protocol | `Zero-Leakage`, `Proof-of-Read`, `In-Code Sentinel` | Master Governance Contract |
| `docs/README.md` | Master Architectural Router | `7 North Star Invariants`, `Domain Routing` | Architectural Entry Point |
| `docs/INDEX.md` | Flat Symbol & File Lookup | `Symbol -> Spec Section`, `Stable {#ANCHOR} ids`, `CO-GOVERNED marks` | Zero-Cost Routing Lookup |
| `src/FortniteVideoSoftware.Core/Infrastructure/ApplicationPaths.cs` | `ApplicationPaths` | `ProgramDataRoot`, `RecoveryStateFile`, `SessionStateFile` | System Path Governance |
| `src/FortniteVideoSoftware.Core/Infrastructure/RecoveryManager.cs` | `RecoveryManager` | `SaveState`, `LoadState`, `CheckFault`, `IsSafeModeActive` | Session State & Fault Governance |
| `src/FortniteVideoSoftware.App/MainWindow.axaml.cs` | `MainWindow` | `SaveRecoveryState`, `RestoreRecoveryStateAsync`, `CheckFault` | Main Process Governance Root |

---

## 0. Routing (do this first, it is nearly free)
Do NOT read every spec. Route once, read one.
* Know the FILE -> `docs/README.md` §3.
* Know only a SYMBOL, CONSTANT or ENGINEERING TAG -> grep `docs/INDEX.md`.
* Landed on a row marked `⚠` -> that file is CO-GOVERNED; read every spec the row names.

## 1. Absolute Stop-and-Read Mandate
Developers and autonomous AI agents are strictly forbidden from inspecting, generating, refactoring, or modifying code in any source file (`.cs`, `.axaml`, `.cmd`, `.ps1`) without first loading, reading, and citing its mapped specification file from `docs/`. Speculative code modifications without active specification verification are classified as critical system defects.

## 2. Mid-Session Context Boundary Rule
If a debugging session or feature implementation crosses from its initial domain into another (e.g., an FFmpeg export task requiring adjustments to `OutputTimeline.cs`, audio mastering filters, Avalonia UI styles, or disk recovery serialization), EXECUTION MUST HALT IMMEDIATELY.
* Conversational memory, assumed defaults, or heuristic extrapolations are strictly prohibited.
* **CO-GOVERNED FILES:** A source file listed in the Code Mini-Map of MORE THAN ONE spec (marked `⚠ CO-GOVERNED BY` on its row, and `⚠` in `docs/INDEX.md`) is bound by ALL of them simultaneously. Reading one of them is NOT compliance. `MainWindow.axaml.cs`, `GranularSpeedEditorWindow.axaml.cs`, `VoiceOverWindow.axaml.cs`, `MusicWizardWindow.axaml.cs`, `PhoneFrameMockup.axaml.cs`, `FluidVolumeSlider.cs` and `WindowBoundsHelper.cs` are the known multi-domain files.
* The agent must load and parse the governing `docs/0X_*.md` file before inspecting or touching logic in that secondary domain.
* Cross-domain modifications executed without verifying the target domain's governing specification will be rejected and reverted.

## 3. Mandatory Proof-of-Read Protocol
Every code edit proposal, refactor plan, or architectural implementation block submitted by an AI agent must begin with the following three-line verification header:
```text
[SPEC VERIFIED]: docs/0X_<DOMAIN_SPEC_NAME>.md#<STABLE-ANCHOR>   (anchor, e.g. FFM-BINPATH — never a bare section number)
[INVARIANT QUOTED]: <Exact formula, mathematical constraint, threshold, or boundary rule quoted from that spec>
[SCOPE CLAMP]: <Explicit confirmation of the precise method, struct, or file altered with zero outside side-effects>
```

## 4. In-Code Source Sentinel
Every production C# and AXAML code-behind source file across `src/` must carry the following immutable contract block at Line 1:
```csharp
// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/0X_<MAPPED_SPEC>.md
// Invariants, constants, and threading models must match spec bit-for-bit.
```

## 5. Specification Invariant Audit Verification
When audits or tests reveal discrepancies between text documentation and active C# implementation:
1. Ground truth resides in mathematical laws, thread safety boundaries, and hardware protection safeguards.
2. Obsolete workflows (e.g., legacy checkmark-based zoom models) must be purged from documentation and replaced with live production invariants (e.g., ZOOMLIVE continuous gestures).
3. Critical unwritten defenses present in source code (e.g., WASAPI 3-second preview clock aborts, render thread OpenGL deadlock mitigations, sub-pixel pointer capture releases, atomic file replacement safeguards) must be codified directly into the governing specifications.