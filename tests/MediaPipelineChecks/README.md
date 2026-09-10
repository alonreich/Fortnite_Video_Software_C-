# Local media regression checks

Run from the workspace root. No internet or additional test packages are required.

```powershell
dotnet restore tests/MediaPipelineChecks/MediaPipelineChecks.csproj --source binaries -p:NuGetAudit=false
dotnet run --project tests/MediaPipelineChecks/MediaPipelineChecks.csproj --no-restore
```

To resume only the GPU checks:

```powershell
dotnet run --project tests/MediaPipelineChecks/MediaPipelineChecks.csproj --no-restore -- --gpu-only
```

The GPU checks require a working NVIDIA GPU and the bundled FFmpeg CUDA/NVENC filters. They run actual exports, not just argument comparisons. The FFV1 test deliberately fails GPU video processing, then verifies one compatibility retry with NVENC retained. Intel preset parsing is checked separately; Intel hardware encoding requires a supported Intel device.

Generated media, logs and temporary state stay under `artifacts/<timestamp>`. Existing user media/settings are not changed. The harness verifies numerical audio levels, crop recovery, regional formatting, complete exports and GPU route selection. It does not automate desktop window interaction or publish NativeAOT.

To verify the native build separately, refresh cached build references first so the runtime and native compiler versions match. These commands use local packages only:

```powershell
dotnet restore src/FortniteVideoSoftware.App/FortniteVideoSoftware.App.csproj --source binaries -r win-x64 -p:NuGetAudit=false
dotnet publish src/FortniteVideoSoftware.App/FortniteVideoSoftware.App.csproj --no-restore -c Release -r win-x64 -p:NuGetAudit=false -o tests/MediaPipelineChecks/artifacts/native-publish
& tests/MediaPipelineChecks/NativeSmoke.ps1 -Executable tests/MediaPipelineChecks/artifacts/native-publish/FortniteVideoSoftware.App.exe
```

Run the smoke test only after a successful publish. It checks JSON state saving and crop-backup recovery through the native executable, with separate temporary state inside the workspace. It does not launch the installer or desktop windows. Publishing requires the local NativeAOT toolchain and cached packages.

The resident video path currently supports NVIDIA decode/encode with frame timing, cuts, frozen thumbnail intros, concatenation and exact fixed Lanczos scaling. Unsupported effects retain their original software graph and GPU encoding when available. Compatibility decoding goes directly to system RAM instead of decoding on GPU, downloading, then uploading again. Audio processing remains on CPU. Graphs are rebuilt per attempt, so a fallback never inherits CUDA-only filters or pixel formats.
