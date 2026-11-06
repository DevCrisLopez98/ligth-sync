# ligth-sync

Cross-vendor RGB synchronization for an ASUS Aura Sync system with a Gigabyte RTX 5060 Ti Gaming OC 8G.

## Current milestone

The GPU is identified by GPU-Z as:

- NVIDIA device: `10DE:2D04`
- Gigabyte subsystem: `1458:4193`
- Model: `GV-N506TGAMING OC-8GD`

OpenRGB already contains the Gigabyte RGB Fusion 2 Blackwell controller used by sibling cards. This workspace vendors an OpenRGB checkout and adds the missing `0x4193` subsystem registration for the 8 GB Gaming OC variant.

## Important scope boundary

This does **not** make the GPU appear as a native device inside ASUS Armoury Crate. Armoury Crate does not expose a documented virtual Aura device interface for third-party hardware. The robust design is to use OpenRGB as the neutral hardware layer and build the synchronization service above its SDK.

## Validation plan

1. Build the patched OpenRGB checkout on Windows.
2. Detect the GPU through the Gigabyte RGB Fusion 2 Blackwell backend.
3. Set a static color before testing effects.
4. Add the ASUS motherboard, Kingston RGB memory, and ARGB headers.
5. Integrate the service through the OpenRGB SDK.

Do not run Armoury Crate, Gigabyte Control Center, and OpenRGB as simultaneous RGB owners during hardware validation.

## LightSync suite

`src/LightSync` is the Windows app that owns lighting. OpenRGB remains the hardware backend.

The first slice lists detected devices and runs two software effects on one clock:

- Solid cycle: every LED shares the same changing color. This is the smooth look currently running.
- Rainbow: addressable strips show a full spectrum every 20 LEDs, traveling outward from the connector. LED 0 stays on the same color as the GPU. Each RAM stick chases that incoming color on its own.
- Static: any color from a full hue/saturation spectrum, held on every LED. Dim it with the brightness slider.
- Music: optional. The selected solid or rainbow effect stays, and brightness follows the default playback device, including its Windows volume and mute. It follows output-device changes. The brightness slider applies only while music mode is off.

## Installer

`installer/build-installer.ps1` publishes a self-contained LightSync, copies the local patched OpenRGB build, and produces `installer/output/LightSync-Setup.exe`.

The OpenRGB checkout under `.vendor/OpenRGB` is not part of this repository and is not pushed upstream. The subsystem patch is `patches/openrgb-rtx5060ti-8g.patch`.

The installer can add a desktop shortcut, a Start menu entry, an optional taskbar pin, and an optional logon task. Closing the window hides LightSync in the tray. Quit is the tray item Salir.

Build:

```powershell
dotnet build src\LightSync\LightSync.csproj -c Release
```

The executable is `src\LightSync\bin\Release\net10.0-windows\LightSync.exe`. It asks for administrator rights so it can inhibit Aura and the SONiX DDR lighting service. On launch it loads the saved effect, stops the temporary Python clock, and becomes the only painter. On close it writes the last color as a hardware Static mode and leaves the OpenRGB server running, so the lights do not fall back to a hardware color cycle.
