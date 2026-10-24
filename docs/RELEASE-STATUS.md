# Arclo release status

Updated: **2026-10-24**

Arclo is a working Windows file toolkit, previously developed as Orbit. Its radial workflow, local processing tools, task queue, visual trimming, presets, and two themes provide enough useful functionality for a first public preview. The first release should carry a preview label and clearly describe its current limits.

The public repository is [baronesses/Arclo](https://github.com/baronesses/Arclo). **A public download is not available yet.** The existing full portable ZIP is held until its native dependency redistribution material is complete.

## Validation evidence

The renamed **Arclo 0.32.0** build was rebuilt and checked on 2026-10-24:

- **899 automated portable checks across 22 isolated diagnostic modes**, including real audio/video trimming, image conversion, archives, queue controls, themes, and the current-file workflow.
- A separate **basic interface scenario passed**, including full-window and compact WebP conversion, compact crop, placement, and selection simulations.
- **408 checks in seven component/native test suites passed**: queue control (27), checksums (113), clipboard-save workflow (38), tray queue model (21), downloader (37), browser URL policy (104), and platform settings (68).
- Actual screenshots of the renamed executable were inspected in both themes. The original logo and SVG wordmark are preserved in the repository.

These checks used owned sample files, isolated settings, muted playback, and a substituted clipboard. Native downloader checks used local loopback fixtures. Browser URL policy checks used an injected platform. They do not establish live site compatibility, native address-bar compatibility in a real browser, or a physical Explorer hotkey test.

The full portable folder has been prepared for local testing. Its test outputs and user data are excluded from the ZIP. A working build and successful tests do not complete the dependency redistribution material described below.

## Before the public ZIP

1. Finish the matching source, license, and dependency inventory for the included FFmpeg and Poppler runtimes, or prepare and validate a different package that excludes the uncleared components. Details: [binary redistribution status](legal/REDISTRIBUTION-STATUS.md).
2. After any dependency replacement, rebuild and rerun the affected conversions and packaged-app checks. Generate a SHA-256 checksum for the final public archive.
3. Publish the validated download in GitHub Releases with preview notes and complete distribution material.

Arclo's own source-code license has not been selected. Preparing public documentation and original branding assets does not select a license or publish the application source code.

## Using a local preview

If a preview folder has been supplied directly, extract it completely and launch `Arclo.exe`. Keep its accompanying files and `tools` folder together. Exit an older running build through its tray menu before switching versions.

The interface is currently primarily Russian; the [English introduction](../README.md) and [Russian introduction](../README.ru.md) describe the same features. The build is unsigned, and an installer and automatic application updater are not included yet.
