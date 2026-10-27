# iOS Port — Assessment

_Assessed 2026-10-27. Not started; this is a reference for what a port would take._

## Current state (blockers)

- **Graphics:** desktop OpenGL 4.1 Core (`Engine.Platform/GameWindow.cs`), 84 shaders at
  `#version 410 core`. iOS has no desktop GL — only Metal and deprecated OpenGL ES 3.0.
- **Windowing/input:** Silk.NET Windowing + Input (GLFW), keyboard/mouse only (~21 keys), ImGui HUD.
- **Audio:** Silk.NET OpenAL + `Silk.NET.OpenAL.Soft.Native` (likely no iOS binaries).
- **Runtime:** plain `net8.0`; JSON config (`tuning.json`, `position.json`) written next to the
  executable; HTTP discovery client; background threads for terrain baking / star paging.
- `Engine.Core`, `Game.Universe`, `Game.Systems` should port almost unchanged.

## Work required

### 1. Graphics backend (largest item)

| Option | Effort | Notes |
|---|---|---|
| **A. ANGLE (GLES 3.0 on Metal)** | Smallest | Keep the GL renderer; port shaders `410 core` → `300 es` (precision qualifiers, drop desktop-only bits). `gl_VertexID`, `texelFetch`, vertex texture fetch are all in ES 3.0. Risks: `Rgba32f` render targets in the terrain bake (needs `EXT_color_buffer_float` or switch to half-float), `DepthComponent24`, `ReadPixels`. |
| **B. WebGPU (wgpu-native / Silk.NET.WebGPU)** | Large | One rewrite (shaders → WGSL), native Metal on iOS + macOS, Vulkan/DX12 elsewhere. Also gets macOS off deprecated GL. |
| **C. Native Metal** | Largest | Best iOS performance, but a second renderer to maintain. |

Recommendation: start with **A** as a proof of concept; consider **B** for a single modern backend.

### 2. iOS app project
- New head project (e.g. `Game.iOS`) targeting **`net10.0-ios`** — .NET 8 iOS support has ended and
  Apple requires a current Xcode SDK. Libraries can stay `net8.0` or multi-target.
- Xcode, Apple Developer account, signing/provisioning, Info.plist, icons, launch screen.
- No JIT on iOS (AOT only): move `System.Text.Json` to source-generated serializers so trimming
  doesn't break config/discovery types.

### 3. Windowing and lifecycle
Silk.NET's SDL backend supports iOS (less mature than desktop), or host a Metal/ANGLE surface in a
`UIViewController` directly. Handle suspend/resume, GPU context loss in background, Retina scaling,
safe areas.

### 4. Touch input
Everything assumes keyboard/mouse (free-fly, rover, galaxy map, ImGui). Needs on-screen sticks or
gestures (pinch for speed, drag to look), possibly GameController framework support, and touch-
friendly ImGui with larger hit targets. A design job, not just plumbing.

### 5. Audio
Build OpenAL Soft for iOS as a static lib, use Apple's deprecated (still present) OpenAL framework,
or move to AVAudioEngine.

### 6. Files and networking
- Config files → app sandbox Documents/Library, not beside the executable.
- Discovery server must be HTTPS (App Transport Security) or need an exception.

### 7. Mobile performance
Volumetric clouds, atmosphere ray-march, GPU terrain bakes and 32-bit float targets are heavy for
phone GPUs/battery. Plan for sub-native render scale, quality presets, half-float targets, smaller
per-frame bake budgets, and thermal throttling.

### 8. CI
Add a macOS runner job to `.github/workflows/release.yml` with signing secrets that builds an `.ipa`
and uploads to TestFlight.

## Suggested order
1. Abstract the GL context/window; run the existing renderer on **macOS via ANGLE** with ES 3.0
   shaders (proves the shader port without touching iOS).
2. Add `Game.iOS`, get it rendering, fix file paths.
3. Touch controls + ImGui fixes.
4. Audio, performance tuning, TestFlight CI.

Most of the cost is in steps 1 (graphics) and 4 (touch input).
