"""Prepare only redistributable, pinned tools/notices for the Arclo EXE resource."""
from pathlib import Path
import argparse
import hashlib
import json
import os
import zipfile

ROOT = Path(__file__).resolve().parents[1]


def prepare(version="0.34.0", runtime_version="9.0.18", tools_directory=None):
    tools = Path(tools_directory) if tools_directory else ROOT / "artifacts/bundled-tools/26.04"
    cache = Path(os.environ.get("NUGET_PACKAGES", Path.home() / ".nuget/packages"))
    output = ROOT / "artifacts/single-exe" / version
    output.mkdir(parents=True, exist_ok=True)
    files = {}
    for name in ("7zr.exe", "LICENSE.txt", "ORIGIN.json", "7ZIP-EXTRA-README.txt", "7ZIP-EXTRA-LICENSE.txt"):
        path = tools / "7zip" / name
        if not path.is_file():
            raise FileNotFoundError(path)
        data = path.read_bytes()
        if name == "7zr.exe":
            digest = hashlib.sha256(data).hexdigest()
            if digest != "256feca8e274e5da655e2a284fabafd9f554365eb164862089dacd4e8276d282":
                raise ValueError("7zr.exe differs from the verified unmodified upstream 26.04 executable")
        elif name == "7ZIP-EXTRA-README.txt":
            if b'you can use 7zr.exe as "public domain" code' not in data:
                raise ValueError("Official public-domain notice for 7zr.exe is missing")
        elif name == "LICENSE.txt":
            # This explanatory note belongs to Arclo; preserve original upstream files separately.
            data = data.replace(b"Orbit", b"Arclo")
        files["7zip/" + name] = data

    for package in ("microsoft.netcore.app.runtime.win-x64", "microsoft.windowsdesktop.app.runtime.win-x64"):
        runtime = cache / package / runtime_version
        found = False
        for name in ("LICENSE", "LICENSE.TXT", "THIRD-PARTY-NOTICES.TXT"):
            path = runtime / name
            if path.is_file():
                files[f"licenses/{package}/{name}"] = path.read_bytes()
                if name.startswith("LICENSE"):
                    found = True
        if not found:
            raise ValueError(f"Runtime license is missing: {runtime}")

    files["licenses/INCLUDED-COMPONENTS.txt"] = (
        "Arclo includes the Microsoft .NET and Windows Desktop runtimes under their included notices, "
        "and unmodified 7zr.exe 26.04 as public-domain code.\n"
        "7zr.exe: https://github.com/ip7z/7zip/releases/download/26.04/7zr.exe\n"
        "7zr source: https://github.com/ip7z/7zip/tree/26.04\n"
        "Official 7-Zip Extra README and license are retained beside 7zr.exe. "
        "No other 7-Zip executable or DLL is included.\n"
        "External processing tools are obtained separately from their publishers.\n"
    ).encode("utf-8")

    manifest = {"schema": 1, "version": version, "files": [
        {"path": name, "bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()}
        for name, data in sorted(files.items())
    ]}
    manifest_bytes = (json.dumps(manifest, indent=2) + "\n").encode("utf-8")
    resource = output / "Arclo.BundledTools.zip"
    with zipfile.ZipFile(resource, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for name, data in sorted(files.items()):
            info = zipfile.ZipInfo(name, (2026, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            archive.writestr(info, data)
        manifest_info = zipfile.ZipInfo("manifest.json", (2026, 1, 1, 0, 0, 0))
        manifest_info.compress_type = zipfile.ZIP_DEFLATED
        archive.writestr(manifest_info, manifest_bytes)
    (output / "manifest.json").write_bytes(manifest_bytes)
    with zipfile.ZipFile(resource) as archive:
        if archive.testzip() is not None:
            raise ValueError("Embedded payload CRC failure")
        expected = set(files) | {"manifest.json"}
        if set(archive.namelist()) != expected:
            raise ValueError("Unexpected embedded payload member")
        for record in manifest["files"]:
            data = archive.read(record["path"])
            if hashlib.sha256(data).hexdigest() != record["sha256"]:
                raise ValueError("Embedded payload digest mismatch")
    result = {"resource": str(resource), "sha256": hashlib.sha256(resource.read_bytes()).hexdigest(),
              "bytes": resource.stat().st_size, "files": len(files), "includedTools": ["7zr 26.04"],
              "runtimeVersion": runtime_version, "excludedTools": ["ImageMagick", "FFmpeg", "Poppler", "qpdf", "Tesseract", "yt-dlp", "Deno"]}
    (output / "payload-result.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(json.dumps(result))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--version", default="0.34.0")
    parser.add_argument("--runtime-version", default="9.0.18")
    parser.add_argument("--tools-directory")
    args = parser.parse_args()
    prepare(args.version, args.runtime_version, args.tools_directory)
