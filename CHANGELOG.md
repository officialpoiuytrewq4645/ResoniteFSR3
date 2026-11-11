# Changelog

## 0.1.0 (alpha)

First public alpha.

- FSR 3.1 temporal anti-aliasing replaces Resonite's TAA option, in desktop and VR (single-pass stereo, per-eye contexts).
- Live settings in the dashboard (Settings → Mods → Resonite FSR 3.1) via BepisModSettings.
- Auto-generated reactive mask for transparents.
- TreatNoMotionAsCamera: fixes smearing on NoMotion renderers.
- Optional patched vrperfkit add-on for VR performance (FSR 1 upscaling), configurable from the dashboard.
- FSR debug view for diagnosing motion vectors / depth / masks.
- Experimental FSR 3.1 VR upscaling (Quality > Upscaling): renders at a lower resolution, FSR 3.1 rebuilds the full
  image, and the add-on's submit hook hands it to SteamVR.
- Very experimental eye-tracked foveated rendering (Foveated Rendering > Mode = QuadViews), with gaze from SteamVR,
  Resonite's own eye tracking or Baballonia, and a built-in benchmark.
- vrperfkit add-on: now required for VR upscaling; its own FSR 1 upscaling is off by default.
