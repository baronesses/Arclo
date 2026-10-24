<p align="center">
  <img src="docs/assets/arclo-banner.svg" width="900" alt="Arclo — your files, a shortcut away">
</p>
<p align="center"><strong>Your files. A shortcut away.</strong></p>
<p align="center">A native Windows file toolkit built around a radial menu at your cursor.</p>
<p align="center">
  <a href="docs/RELEASE-STATUS.md">Download & release status</a> ·
  <a href="README.ru.md">Русский</a> ·
  <a href="FUNCTIONALITY-RU.md">Function guide</a> ·
  <a href="https://github.com/baronesses/Arclo/issues">Feedback</a>
</p>

## Meet Arclo

Select a file in Explorer, press **Ctrl + Alt + Space**, and choose what to do. Convert a photo to WebP, extract audio from a video, trim a track, unpack an archive, or turn PDF pages into images without hunting through separate apps.

Arclo brings everyday file work into one compact workspace. Its menu adapts to your selection; previews, settings, progress, and results stay close to the file you are working with. For larger tasks, open the full window and queue a batch.

Previously developed as **Orbit**, Arclo carries forward the existing toolkit under a new name.

**Windows x64 · Portable preview · Local processing**

The public ZIP is not available yet. The application is being prepared for its first public preview; [release status](docs/RELEASE-STATUS.md) explains the remaining publication and packaging work.

## A look inside

<table>
  <tr>
    <td width="50%"><img src="docs/assets/screenshot-main.png" alt="Arclo full window in the Crimson Noir theme"><br><strong>One workspace for your files</strong></td>
    <td width="50%"><img src="docs/assets/screenshot-compact.png" alt="Arclo compact radial menu"><br><strong>A compact menu at the cursor</strong></td>
  </tr>
  <tr>
    <td><img src="docs/assets/screenshot-trim.png" alt="Arclo trim editor with A and B timeline boundaries"><br><strong>See the range you keep</strong></td>
    <td><img src="docs/assets/screenshot-light.png" alt="Arclo in the light theme"><br><strong>Glass or light, your choice</strong></td>
  </tr>
</table>

## What you can do

| Work with | Tools |
| --- | --- |
| **Images** | Convert to JPEG, WebP, or AVIF; compress, resize by pixels or percentage, crop, rotate, flip, add text or logo watermarks, remove supported metadata, build contact sheets, or assemble images into a PDF. |
| **Video** | Export MP4 or WebM, resize and compress, remove audio, extract a soundtrack, save PNG frames, create a short GIF, and trim a time range. |
| **Audio** | Convert MP3, WAV, FLAC, M4A/AAC, Opus, or Ogg/Vorbis; trim, adjust volume and fades, normalize loudness, edit supported tags, and extract embedded cover art. |
| **PDF & OCR** | Export pages as PNG/JPEG; merge, select, reorder, rotate, or split pages; extract existing text and embedded images; optimize PDF structure; recognize Russian and English text locally. |
| **Archives & folders** | Create ZIP, TAR, TAR.GZ, or 7z archives; browse and extract supported archives, including selected entries; create password-protected 7z archives. |
| **Everyday file tasks** | Generate and verify SHA-256 checksums, inspect file and media-stream details, and preview batch renaming with an undo option for the last successful rename in the current session. |
| **Links** | Download one accessible video or audio item as MP4 or MP3, then convert or trim the saved file. Paste a link manually or use the supported browser's visible address bar. |

Support depends on the actual file, codec, and operation. The app applies size, duration, page-count, and resource limits. See the [detailed function guide in Russian](FUNCTIONALITY-RU.md) for individual settings and limits.

## Small conveniences that add up

- **A menu at your cursor.** Select files or folders in Explorer or on the desktop, then call up the compact circle with a configurable shortcut.
- **Paste and continue.** In the open menu, Ctrl + V accepts a copied local file or a link. Convert, Save as, Copy, Trim, and More keep the current file in the center.
- **A visual trim timeline.** Drag the A/B boundaries, enter precise times, or use keyboard controls. Preview playback starts when you ask for it.
- **Progress you can follow.** The current operation shows progress around the circle; the task view provides status, cancellation, and results.
- **A manageable queue.** Pause the start of subsequent tasks, resume, cancel waiting tasks, or cancel all unfinished tasks. The active encoder keeps running when the queue is paused.
- **Presets and repeatable work.** Save compatible settings, pin favorites, repeat the last supported operation, or build a short image-processing chain.
- **Results within reach.** Open a result, reveal it in Explorer, copy the file or its path, and compare an image with its original side by side or with a divider.
- **Your preferred appearance.** Choose Liquid Glass in Crimson Noir or the light theme, change the circle size, reduce transparency, and reorder compatible sectors.
- **Tray and Explorer integration.** Keep background tasks running while the window is hidden. Startup and the Explorer context-menu entry are optional.

## Get started

These steps apply to a locally supplied preview. For a future public download, follow the [release status](docs/RELEASE-STATUS.md).

1. Extract the supplied Windows x64 preview ZIP's **entire folder** to a writable location.
2. Run **Arclo.exe**. Keep the accompanying `tools` folder and the other shipped files beside it. The portable package includes the .NET runtime and processing tools.
3. Select a file in Explorer or on the desktop and press **Ctrl + Alt + Space**. You can also choose files inside Arclo or drag them into the center of the circle.
4. Pick an action. New results are saved beside the originals or in the output folder you select in settings.
5. Open **More** for additional tools, or use the gear menu for tasks, appearance, settings, and exit.

Before switching from a running Orbit/Arclo build, exit it from its tray menu. Moving the portable folder may require re-enabling optional startup and Explorer integration from its new location.

| Shortcut | Action |
| --- | --- |
| Ctrl + Alt + Space | Show or hide the compact menu; configurable in settings |
| Ctrl + O / Ctrl + Shift + O | Choose files / folders |
| Ctrl + V | Load a copied local file or a link into the open menu |
| Ctrl + K | Search the available tools |
| Esc / right-click | Close the current card or hide the compact menu |
| Left / Right, Enter | Select and activate a radial action |

## Files and privacy

Image, media, PDF, OCR, archive, and checksum operations run locally. They do not require an account or a cloud upload. Link downloads require internet access and contact the requested service through the bundled downloader.

Conversions create new outputs and preserve the originals; existing outputs receive unique names. **Batch rename intentionally changes file names** after a preview. Keep backups for important work.

Settings, optional history, presets, and diagnostics are stored in `%LOCALAPPDATA%\OrbitToolkit`, retaining the previous application's data location. History and diagnostic logs may contain local paths and entered parameters. Archive passwords are not saved in history or presets. Downloads and conversions in the current-file workflow automatically copy the finished file to the clipboard; other result-copy actions are explicit.

The compact Liquid Glass theme briefly captures and blurs the area behind the menu in memory. This background snapshot is not written to disk and refreshes on the next invocation. Reduced-transparency and high-contrast settings use an opaque fallback.

## Preview status and limits

Arclo is ready for early feedback, with rough edges still expected. The desktop interface is currently primarily **Russian**, with some English action labels and Russian/English tool-search aliases. This repository's introduction is available in both languages; full English interface localization is not yet included.

- The portable build is currently unsigned; Windows may show an unknown-publisher or SmartScreen prompt.
- There is no installer or automatic application updater yet. The package targets Windows x64; Windows 10/11 are the intended desktop platforms.
- Downloads depend on the site, network, and bundled downloader version. Account login, protected streams, and whole-playlist downloads are not supported.
- Encrypted PDFs, password-protected ZIP files, self-extracting and multipart archives, and editing animated images are not supported.
- SVG, HEIC, RAW, and PSD are not advertised as supported image-editing formats. OCR produces plain text and needs proofreading.
- Media export may re-encode content and does not preserve every track, subtitle, chapter, or metadata field. Compression does not guarantee a smaller file.

## Feedback

Found a problem? [Open an issue](https://github.com/baronesses/Arclo/issues/new/choose) with the Arclo version, Windows version, action, input format, and the steps needed to reproduce it. A small non-private sample is especially useful. Bug report and feature request templates help capture the useful details. Review diagnostic output before sharing it because it may include local paths.

Arclo uses established processing tools including ImageMagick, FFmpeg, Poppler, qpdf, Tesseract, 7zr, yt-dlp, and Deno. Their provenance is recorded in [third-party sources](THIRD-PARTY-SOURCES.md). The [binary redistribution status](docs/legal/REDISTRIBUTION-STATUS.md) identifies the missing source and notice material that must be completed before the full portable package is published.
