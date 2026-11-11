ResoniteFSR3 - optional vrperfkit add-on (VR performance)
==========================================================

AI-GENERATED SOFTWARE: I made the patches to vrperfkit with an AI coding assistant (Claude).
I directed it and tested it in-game. - poiuytrewq4645

Source of this build: https://github.com/officialpoiuytrewq4645/VRPerfKit_RSF-ResoniteFSR3
A patched build of VRPerfKit_RSF (https://github.com/RavenSystem/VRPerfKit_RSF), itself a fork of
fholger's vrperfkit (https://github.com/fholger/vrperfkit). Stock vrperfkit/RSF does nothing in Resonite:
it hooks the wrong OpenVR compositor interface (Resonite's Unity 2019 renderer submits through
IVRCompositor_022) and uses the wrong method slots for newer OpenVR versions. This build hooks every
interface version correctly.

What it does for ResoniteFSR3: hands the mod's full-resolution FSR 3.1 output to SteamVR when the eyes are
submitted. ResoniteFSR3's FSR 3.1 VR upscaling and foveated rendering NEED this add-on; native anti-aliasing works
without it.

It can also do vrperfkit's original FSR 1 upscaling at submit. That ships OFF: FSR 3.1 upscaling looks and runs
better, and the two should not be stacked.

INSTALL (manual - mod managers cannot install this):
  Copy dxgi.dll and vrperfkit_RSF.yml into:
      <Resonite>\Renderer\        (next to Renderite.Renderer.exe)

CONFIGURE:
  With ResoniteFSR3 installed: Settings -> Mods -> Resonite FSR 3.1 -> "vrperfkit (FSR 1 VR upscaling)".
  Or edit vrperfkit_RSF.yml directly. Either way, changes apply the NEXT TIME Resonite starts.
  renderScale is the percentage of pixels rendered (60 = ~77% per axis).

UNINSTALL:
  Delete dxgi.dll and vrperfkit_RSF.yml from <Resonite>\Renderer\.

Note: the in-game debug circle hotkeys (Ctrl+F1..F9) are enabled in the default yml; set
hotkeys: enabled: false if they conflict with anything.

License: MIT (see LICENSE-vrperfkit.txt).
