# Binary distribution status

Reviewed on 2026-10-24 against the local 0.32.0 Windows x64 portable package.

**The existing full portable ZIP is not cleared for public distribution.** This is a packaging finding, separate from application functionality or runtime test results. The public project description and original branding assets can be published while the binary packaging is completed.

## Items requiring completion

| Component | Evidence in the local package | Remaining work |
| --- | --- | --- |
| FFmpeg 6.0 essentials, Gyan static GPLv3 build | `tools/ffmpeg/LICENSE`; `README.txt` identifies FFmpeg commit `ea3d24bbe3` and many statically linked external libraries | Preserve the exact corresponding source, dependency revisions, patches, and build configuration for the shipped build; make the complete source available alongside the binary with clear download directions. The FFmpeg repository snapshot alone does not establish the source of its static dependencies. |
| Poppler 26.07.0 and its native runtime | `tools/poppler/manifest.json` identifies `poppler=26.07.0=h6618ce5_3`; extraction provenance pins the two added utilities; the Poppler GPL notice is present | Recover exact package/build identities for the bundled dependency DLLs, include their notices, and make applicable corresponding sources and build recipes available. The small runtime manifest does not contain this dependency inventory. |

The existing `THIRD-PARTY-SOURCES.md` already identifies the incomplete Poppler redistribution material. No source archive or notice has been fabricated to mark that item complete.

Other components have provenance and notices, including ImageMagick, qpdf, Tesseract, 7zr, yt-dlp, Deno, and the .NET runtimes. Tesseract includes six upstream dependency source archives; yt-dlp includes its versioned upstream source archive. Their presence is useful evidence, but this review does not certify every transitive dependency or prove that unmodified upstream archives include each distributor's build patches. They must remain in the final package and be checked against its final inventory.

## Ways to complete the first public download

1. **Full portable package:** replace or reconstruct the native toolchain from pinned, reproducible inputs. Record every distributed native component, preserve required notices, source archives, distributor patches, and recipes, then publish source access directions beside the final ZIP. Rerun the packaged application checks because changing native tools can change behavior.
2. **Application-only package:** distribute Arclo and permitted .NET runtime files without the uncleared native tools. Provide explicit installation instructions or an opt-in downloader that obtains pinned tools directly from their original publishers. Verify the resulting setup and describe which features need those downloads. This is a different download experience from the existing self-contained toolkit.

Application-only packaging must exclude the uncleared native binaries from both its ZIP and GitHub repository. Simply moving them to another release asset or copying a license link does not resolve the missing material.

## Primary references

- [FFmpeg's licensing page](https://ffmpeg.org/legal.html) explains its license variants and calls for source matching the distributed binaries, build instructions, and review of external libraries.
- [GPLv3 in the pinned FFmpeg source](https://raw.githubusercontent.com/FFmpeg/FFmpeg/ea3d24bbe3/COPYING.GPLv3), sections 1 and 6, describes corresponding source and network distribution. Section 6(d) permits source on another server with clear directions, while the distributor remains responsible for its availability. Therefore a third-party link is not categorically forbidden; it still must supply the complete matching material.
- [Gyan's build documentation](https://www.gyan.dev/ffmpeg/builds/) identifies its static builds as GPLv3. Its changelog says external-library version listing was added on 2023-11-11; the packaged FFmpeg 6.0 README only lists library names.
- [Poppler upstream](https://poppler.freedesktop.org/) provides release sources. [The conda-forge Poppler feedstock](https://github.com/conda-forge/poppler-feedstock) provides packaging recipes; use the recipe embedded in, or matching, the exact distributed package instead of assuming the current default branch matches it.

This report does not select a public license for Arclo's own source code or branding.
