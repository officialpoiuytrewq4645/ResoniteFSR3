# Foveated rendering test plan (experimental)

Goal: find which pathway skips peripheral pixels **reliably** (the mask actually survives) and **efficiently** (real
GPU time saved) in Resonite's deferred, single-pass-stereo Unity 2019 renderer, with FSR 3.1 rebuilding the image.

All automatic tests run in VR at **Upscaling = NativeAA** (foveation is disabled while FSR upscaling is active).

## Setup

1. In Baballonia, enable **Use DFR**. It sends gaze to `127.0.0.1:9020` (`/tracking/eye/LeftRightPitchYaw`),
   separate from Resonite's own Babble port 8888, so face tracking keeps working.
2. In Resonite: anti-aliasing **TAA**, ResoniteFSR3 **Upscaling = NativeAA**.
3. Settings → Mods → Resonite FSR 3.1 → **Foveated Rendering (experimental test)** → `Gaze = Baballonia`
   (or `Center` for fixed foveation).

## Automatic benchmark (T0–T6)

Turn **RunBenchmark** on, close the dashboard (the run starts 10 s later), hold your head still, and look at one spot
for ~60 s. Turn it off afterwards. Frames rendered while the dashboard is open or at reduced resolution are skipped
(the `skipped` column).

| Test | Mask injection | Fill |
|---|---|---|
| T0 | none (baseline) | � |
| T1 | command buffer at `BeforeGBuffer` | none |
| T2 | full-screen renderer in the G-buffer pass | none |
| T3 | command buffer | spatial |
| T4 | renderer | spatial |
| T5 | none (baseline again, checks drift) | � |

Measured per test (log + `Renderer\BepInEx\ResoniteFSR3-foveation-benchmark-*.csv`):

- **GPU time**: SteamVR compositor frame timing (`m_flTotalRenderGpuMs`), mean / median / p90, plus dropped frames.
- **Mask survival**: of the pixels the mask covered, the % whose depth was still the mask value after the scene
  rendered. ~100% = the injection pathway works; ~0% = Unity cleared or overwrote it (no real saving possible).
- **Saving vs baseline**: T0 mean − test mean.

How to read it: a pathway is only viable if survival is high **and** it saves GPU time. Fill modes should cost little;
check **visual quality** by setting `Mode`/`Fill` manually afterwards (T1/T2 with no fill show stale pixels in the holes,
which is expected). `MaskedReactive` trades ghosting (low) against a softer, blockier periphery (high).

Design notes from earlier runs: the mask must skip whole 2x2 quads (GPUs shade in quads, so per-pixel skipping saved
nothing) and must not write `SV_Depth` (it disables hierarchical-Z; the first working mask cost ~3 ms instead of saving).
A temporal fill (reprojected previous FSR output) was removed: feeding FSR its own output back caused stacked ghost copies.

## Manual tests

- **RSF radial density mask (vrperfkit)**: separate launch. Set ResoniteFSR3 `Enabled = false` (or pick SMAA), and in
  `vrperfkit_RSF.yml` set `upscaling: enabled: true` and `fixedFoveated: enabled: true, method: rdm`. Compare frame
  time/visuals by eye. This pathway fills holes only at submit, after Resonite's post-processing, so it can't be
  combined with FSR 3.1 as-is.
- **Variable rate shading**: not testable on AMD (DX11 VRS is NVIDIA-only).
- **Gaze tracking check**: with `Mode` set to a mask and `Fill = None`, the full-detail area (no holes) should follow
  your eyes. If it moves the wrong way, toggle `GazeInvertPitch` / `GazeInvertYaw`.

## VR jitter check (separate from foveation)

Unity 2019 refuses custom eye projections in single-pass stereo, so by default VR is rendered without sub-pixel jitter
(`VrJitter = Off`, FSR is told the jitter is zero). To test the experimental path: set `VrJitter = ShaderGlobals` and
`JitterScale = 30`. If the VR image visibly shakes, the injection works; set `JitterScale` back to 1 and compare
sharpness of fine detail (text, distant edges) against `Off`.

## Quad views (current approach)

The depth-mask pathways above cost more than they save, so the automatic benchmark now tests **quad views** instead:
the whole view renders at the Upscaling render scale and FSR 3.1 upscales it, and one extra camera per eye renders a
small full-resolution inset around the gaze point (`FoveatedInset.cs`), blended 1:1 over the upscaled image.

Manual use: `Upscaling` below NativeAA (e.g. Performance, or Custom with `CustomRenderScale`), `Mode = QuadViews`.
`QuadViewShowBorder` draws a red ring at the inset edge; `QuadViewInsetSize` / `QuadViewFeather` shape it;
`QuadViewFlipVertical` (Calibration) if the inset is upside down.

| Test | Render scale | Inset |
|---|---|---|
| T0 | 100% (native AA) | — |
| T1 | Upscaling setting (50% if that is NativeAA) | — |
| T2 | same as T1 | yes (quad views) |
| T3 | 100% again (drift check) | — |

Besides GPU time, the CSV has `frame_interval_mean_ms` (real time between frames). The inset cameras draw the scene two
more times, so quad views can lose on CPU what it gains on GPU: compare T2 against T1 on both columns, in a heavy world.
