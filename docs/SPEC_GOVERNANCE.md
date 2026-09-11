# SPECIFICATION GOVERNANCE: ZERO-LEAKAGE CONTRACT

## Code Mini-Map: Bound Source Files & Governance Symbols
| Source File Path | Key Classes, Records & Controls | Core Bound Methods, Properties & Symbols | Subsystem Domain Role |
| :--- | :--- | :--- | :--- |
| `docs/SPEC_GOVERNANCE.md` | Governance Protocol | `Zero-Leakage`, `Proof-of-Read`, `In-Code Sentinel` | Master Governance Contract |
| `docs/README.md` | Master Architectural Router | `7 North Star Invariants`, `Domain Routing Matrix` | Architectural Entry Point |
| `src/FortniteVideoSoftware.Core/Infrastructure/ApplicationPaths.cs` | `ApplicationPaths` | `ProgramDataRoot`, `RecoveryStateFile`, `SessionStateFile` | System Path Governance |
| `src/FortniteVideoSoftware.Core/Infrastructure/RecoveryManager.cs` | `RecoveryManager` | `SaveState`, `LoadState`, `CheckFault`, `IsSafeModeActive` | Session State & Fault Governance |
| `src/FortniteVideoSoftware.App/MainWindow.axaml.cs` | `MainWindow` | `SaveRecoveryState`, `RestoreRecoveryStateAsync`, `CheckFault` | Main Process Governance Root |

---

## 1. Absolute Stop-and-Read Mandate
Developers and autonomous AI agents are strictly forbidden from inspecting, generating, refactoring, or modifying code in any source file (`.cs`, `.axaml`, `.cmd`, `.ps1`) without first loading, reading, and citing its mapped specification file from `docs/`. Speculative code modifications without active specification verification are classified as critical system defects.

## 2. Mid-Session Context Boundary Rule
If a debugging session or feature implementation crosses from its initial domain into another (e.g., an FFmpeg export task requiring adjustments to `OutputTimeline.cs`, audio mastering filters, Avalonia UI styles, or disk recovery serialization), EXECUTION MUST HALT IMMEDIATELY.
* Conversational memory, assumed defaults, or heuristic extrapolations are strictly prohibited.
* The agent must load and parse the governing `docs/0X_*.md` file before inspecting or touching logic in that secondary domain.
* Cross-domain modifications executed without verifying the target domain's governing specification will be rejected and reverted.

## 3. Mandatory Proof-of-Read Protocol
Every code edit proposal, refactor plan, or architectural implementation block submitted by an AI agent must begin with the following three-line verification header:
```text
[SPEC VERIFIED]: docs/0X_<DOMAIN_SPEC_NAME>.md
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