<p align="center">
  <img src="MCDownloadManager/app.ico" width="96" alt="MC Download Manager icon">
</p>

<h1 align="center">MC Download Manager for Windows</h1>

<p align="center">
  A fast, open-source download manager for Windows 10/11.<br>
  Captures Chrome, Edge, Brave and Firefox downloads, accelerates them with up to 16 parallel connections and downloads videos from web pages.
</p>

<p align="center">
  <a href="README.tr.md">🇹🇷 Türkçe</a> · <a href="https://github.com/azadkarakus/mc-download-manager-windows/releases">Releases</a> · <a href="#building-from-source">Build</a>
</p>

---

This is the Windows version of [MC Download Manager](https://github.com/mstcvk/mc-download-manager) by Mesut Çevik, originally written for macOS.
It is a separate C# / WPF implementation that keeps the same browser extension and the same local bridge API, so both apps behave the same way.

The interface is in **English** and **Turkish** (follows the Windows display language) and follows the system light/dark theme.

## Features

- **Multi-connection downloads**: files are split into HTTP `Range` requests (8 connections by default, 1–16 configurable).
  Falls back to a single stream when the server doesn't support ranges and backs off on `429`/`503` throttling.
- **Pause / resume**: segment state is saved to disk, so downloads continue where they left off, even after a restart.
- **Browser integration**: the browser download is cancelled and handed to the app together with cookies, `Referer` and `User-Agent`.
  The file types to capture are configurable.
- **Video downloads**: `<video>`, `.m3u8` and `.mpd` streams on the page are detected, and "Download video from this page" uses yt-dlp with ffmpeg merging.
  The default output is MP4 up to 1080p; container and quality are configurable.
- **Clipboard**: copy a link and the app asks whether to download it. Ctrl+V pastes links into the main window and Ctrl+N adds a new download.
- **Windows UI**: Windows 11 (Fluent) design, categories in the sidebar, a sortable list with search,
  a per-download window with a segment bar, a system tray icon and notifications.
- **Runs in the system tray**: optionally starts with Windows, so browser downloads are captured without opening the app.
  Closing the window keeps it running; quit from the tray icon.

## Installation

> Signed installers will be published on [Releases](https://github.com/azadkarakus/mc-download-manager-windows/releases). Until then, [build from source](#building-from-source).

### Browser extension

The app copies the extension to `%LOCALAPPDATA%\MC Download Manager\BrowserExtension` on every start (*Settings ▸ Browser* shows the folder).

- **Chrome / Edge / Brave:** open `chrome://extensions` (`edge://extensions` in Edge), turn on *Developer mode*, choose *Load unpacked* and select that folder.
- **Firefox:** *about:debugging ▸ This Firefox ▸ Load Temporary Add-on* and pick `manifest.json` from the folder.
  This is temporary until a Mozilla-signed build is available.

### Video tools

yt-dlp, ffmpeg and deno (which YouTube needs) are **not bundled**. On the first video download the app offers to install them from their official GitHub releases
([yt-dlp](https://github.com/yt-dlp/yt-dlp), [yt-dlp/FFmpeg-Builds](https://github.com/yt-dlp/FFmpeg-Builds), [deno](https://github.com/denoland/deno)).
Each file's SHA-256 checksum is verified before it is used, and you can update them from *Settings ▸ Video*.

## Security

- The bridge listens only on `127.0.0.1:48213` and only answers browser-extension origins (`chrome-extension://`, `moz-extension://`)
  with a loopback `Host`. Requests from ordinary web pages, including `Origin: null`, are rejected.
- Cookies and `Authorization` are sent only to the host the download was started for, never to a host reached through a redirect.
- Downloaded files get the *Mark of the Web* (`Zone.Identifier`) like browser downloads, so SmartScreen and Office still check them.
- Settings, the download list and cookie files stay in `%LOCALAPPDATA%\MC Download Manager\` and are readable only by your Windows user.

## Building from source

Requirements: Windows 10/11 and the [.NET 10 SDK](https://dotnet.microsoft.com/download) (`winget install Microsoft.DotNet.SDK.10`).

```powershell
dotnet build MCDownloadManager                               # debug build
dotnet run --project Tests                                   # engine and bridge self-check
dotnet publish MCDownloadManager -c Release -o publish       # self-contained build in publish\
```

## Project layout

```
MCDownloadManager/
  SegmentedDownloader.cs   multi-connection HTTP engine (Range, adaptive concurrency, IPv4/IPv6 racing)
  DownloadStore.cs         queue, persistence, speed measurement, yt-dlp jobs
  Tools.cs                 yt-dlp wrapper and tool installer (checksum verification)
  BridgeServer.cs          local HTTP bridge for the browser extension
  App.xaml.cs              tray icon, single instance, clipboard, start with Windows
  MainWindow / DetailWindow / SettingsWindow
Tests/                     self-check against a local test server
Extension/                 WebExtension (manifest v3), shared with the macOS app
```

### Bridge API

| Endpoint | Description |
|---|---|
| `GET /ping` | version, yt-dlp availability, active download count, captured file types |
| `POST /download` | `{url, filename?, referer?, userAgent?, cookieHeader?, cookies?[], kind: "file"\|"video", segments?}` |
| `GET /downloads` | status of the last 50 downloads |
| `POST /action` | `{id, action: "pause"\|"resume"\|"cancel"\|"remove"}` |

The API is the same as the macOS app's, so the same extension works with both.

## Contributing

Issues and pull requests are welcome. Strings live in `Models.cs` (`S` class) and `Extension/_locales/`.

## Credits and license

Based on [mstcvk/mc-download-manager](https://github.com/mstcvk/mc-download-manager) by Mesut Çevik. The browser extension and the bridge API come from that project.

The code is licensed under the MIT License (see `LICENSE`). yt-dlp (Unlicense), FFmpeg (GPL build from yt-dlp/FFmpeg-Builds) and deno (MIT)
are downloaded at runtime and are not part of this repository.
