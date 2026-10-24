# Build Arclo

Use Windows x64 with the .NET SDK specified in `global.json`, Python 3, and PowerShell 7.

From the repository root:

```powershell
pwsh -NoProfile -File scripts/build-single-exe.ps1
```

The result is a single file:

```text
release/Arclo-0.34.0-win-x64-single/Arclo.exe
```

The build restores the required .NET packages and downloads the pinned 7zr component from its publisher. Internet access is needed for the first build. Its runtime and license notices are embedded in the EXE.

Additional processing tools are connected through **Settings → Components** in the app; they are not required to compile Arclo.

The application and processing code are in `src/`. English and Russian text is stored in [Strings.resx](src/Orbit.Core/Localization/Strings.resx) and [Strings.ru.resx](src/Orbit.Core/Localization/Strings.ru.resx).
