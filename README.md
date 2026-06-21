<div align="center">
  <img src="https://img.shields.io/badge/.NET-9.0-512BD4?style=for-the-badge&logo=dotnet&logoColor=white" alt=".NET 9.0" />
  <img src="https://img.shields.io/badge/Avalonia%20UI-11.x-purple?style=for-the-badge&logo=avalonia&logoColor=white" alt="Avalonia UI" />
  <img src="https://img.shields.io/badge/FFmpeg-Backend-green?style=for-the-badge&logo=ffmpeg&logoColor=white" alt="FFmpeg backend" />
  <img src="https://img.shields.io/badge/NativeAOT-Compiled-black?style=for-the-badge&logo=c&logoColor=white" alt="Native AOT" />
</div>

<br />

<h1 align="center">🎮 Fortnite Video Software</h1>
<p align="center">
  <strong>A high-performance, specialized video editing and processing pipeline built exclusively for gaming montage creation.</strong>
</p>

---

## ⚡ Overview

Fortnite Video Software is a professional-grade video editing application crafted in **C# 11** and **.NET 9**, with a lightning-fast native UI powered by **Avalonia UI**. It abstracts the complexity of advanced video manipulation (trimming, cropping, canvas resizing, music mixing, and hardware-accelerated rendering) behind an intuitive, specialized user interface.

This software was explicitly rewritten from a legacy Python application into a compiled, high-performance `.NET NativeAOT` executable—delivering zero-dependency deployment and incredibly fast startup times.

## 🚀 Key Features

*   **🎬 Precision Trimming:** Mark start and end points of a video effortlessly using an integrated `libmpv` playback engine.
*   **📱 Smart Mobile Canvas (Canvas Trick):** Instantly adapt standard 16:9 gaming footage into a sleek 1080x1920 portrait format tailored for TikTok, Shorts, and Reels, complete with customizable top overlay text.
*   **🎵 Audio Integration:** Seamlessly mix custom background music (`.mp3`, `.wav`, etc.) directly into your montages.
*   **🤖 Hardware Acceleration:** Built-in hardware scanner automatically detects and utilizes the optimal GPU encoder (NVIDIA NVENC, AMD AMF, or Intel QSV) for blistering render speeds.
*   **🖌️ Premium UI/UX:** A dark-themed, meticulously styled interface featuring dynamic metallic buttons, realistic 3D tactile feedback, and high-contrast styling.
*   **📦 Single-File Native Execution:** Packaged via .NET Native AOT into a single, highly optimized `.exe` (under `.\compiled\`) that requires zero external runtime dependencies.

## 🛠️ Technology Stack

*   **Core Logic:** C# 11 / .NET 9.0
*   **Frontend UI:** Avalonia UI (XAML)
*   **Media Playback Engine:** `libmpv-2` interop via safe C# bindings
*   **Render Engine:** Custom FFmpeg command orchestrator (`FilterBuilder`, `FfmpegWorker`)
*   **Build System:** NativeAOT Windows x64 targeting

## ⚙️ Building the Application

To ensure maximum performance and self-containment, the application uses an automated `build.cmd` script to produce a NativeAOT payload.

1. Ensure the .NET 9 SDK and Desktop Development C++ Workload (for AOT linking) are installed.
2. Run the build script in the root directory:
   ```cmd
   .\build.cmd
   ```
3. Your compiled, high-performance executable will be deployed directly to:
   ```
   .\compiled\FortniteVideoSoftware.exe
   ```

## 🎮 Usage Instructions

1. **Upload Video:** Click the `UPLOAD VIDEO` button to load your raw gameplay footage into the built-in MPV player.
2. **Trim Content:** Use the visual playback controls or `MARK START` / `MARK END` buttons to isolate your exact desired clip.
3. **Set Format:** Toggle between standard landscape or `Portrait (9:16)` for mobile targets. Add overlay text if desired!
4. **Mix Audio:** Click `ADD MUSIC` to select a track to layer over the gameplay.
5. **Process:** Hit `PROCESS` and let the hardware-accelerated pipeline render your optimized video directly to your output folder!

## 🛡️ License & Contact
*Developed by [alonreich](https://github.com/alonreich).*
