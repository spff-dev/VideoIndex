# VideoIndex

A lightweight web app to **index, browse, and tag your local video library**.  
It scans one or more folders (“**roots**”), extracts media metadata with `ffprobe`, generates thumbnails with `ffmpeg`, and stores everything in a **SQLite** database. A modern UI lets you filter, search, and edit details.

> **Stack**: .NET 8 (Razor Pages + Minimal APIs), EF Core (SQLite), SignalR, FFmpeg.

---

## Features

- **Roots**: choose folders to index; each is scanned recursively.
- **Metadata**: size, duration, resolution, bitrate, codec, frame rate, audio format/channels.
- **Thumbnails**: picked from brighter frames (simple luma heuristic) and stored as JPEGs.
- **Library UI**: grid with **filters** (performers, orientation, source type), **search** (AND by default, `or` supported), and **sorting**.
- **Media Details**: inline player/thumbnail toggle, open in default player, reveal in Explorer, and full tag editing.
- **Live Scan**: progress and logs via SignalR; degree-of-parallelism and thumbnail options.
- **APIs**: browse, update metadata, stream, and scan via HTTP endpoints.

---

## Requirements

- **Windows** (current build targets Windows: `[assembly: SupportedOSPlatform("windows")]`).
- **.NET 8 SDK**  
  <https://dotnet.microsoft.com/download>
- **FFmpeg** (must include both `ffmpeg` and `ffprobe` on your `PATH`)  
  <https://ffmpeg.org/download.html> (or `choco install ffmpeg` on Windows).

---

## Project Structure

```
<repo root>/
├─ VideoIndex.Core/        # EF Core models, DbContext
├─ VideoIndex.Web/         # Razor Pages app + Minimal APIs + SignalR hub
│  ├─ Pages/
│  │  ├─ Library/Index.*   # Library grid UI + mini-scan
│  │  ├─ Media/Details.*   # Media details page
│  │  ├─ Roots/*           # Roots admin page
│  │  └─ Scan/*            # Dedicated live-scan page
│  ├─ Pages/Shared/_TopNav.cshtml, _TopNavEnd.cshtml
│  └─ wwwroot/css/         # app.css, tags.css, site.css
└─ README.md
```

---

## Configuration

Use a **SQLite connection string** named `VideoIndex`. Example `appsettings.Development.json` in **VideoIndex.Web**:

```jsonc
{
  "ConnectionStrings": {
    "VideoIndex": "Data Source=./videoindex.db"
  }
}
```

You can also set it via env var:

```
ConnectionStrings__VideoIndex=Data Source=./videoindex.db
```

> The database file will be created if it doesn’t exist. Ensure the process can write to the folder.

FFmpeg/FFprobe are discovered via your **PATH**. Verify:

```
ffmpeg -version
ffprobe -version
```

---

## Quick Start

```bash
# from the repository root
dotnet restore
dotnet build

# run the web app
dotnet run --project VideoIndex.Web
# or with hot reload during development
dotnet watch --project VideoIndex.Web run
```

Open the URL shown in the console (Kestrel) and you should see the **Library**.  
If `/` doesn’t redirect, go to `/library` directly.

---

## Using the App

### 1) Add Roots

- Navigate to **/roots** to add one or more folders you want to index.
- Each root represents a top-level folder; scanning is recursive.

### 2) Scan

- Go to **/scan** for a focused live-scan UI.

During a scan:

- Metadata is read with `ffprobe`.
- Thumbnails are generated with `ffmpeg` (if enabled).
- Live progress/logs stream via SignalR.

### 3) Browse & Filter

- The **Library** grid supports filtering by **performer count** (Solo/Duo/Group), **orientation** (Gay/Straight/Bi/FtM/MtF), and **source type** (Studio, OnlyFans, Amateur, etc.).
- **Search tips**:
  - Space-separated terms are **AND** by default.
  - Use `or` (case-insensitive) to create OR clauses, e.g. `dean 2020 or mike studio`.

### 4) Edit Media Details

- Open an item → `/media/{id}`.
- Toggle thumbnail/video. Open in default player or reveal in Explorer.
- Edit **performer count**, **year**, **orientation**, **source types**, **studio**, **source user**, **other tags**, and **performers**.
- Save sends a `PUT /api/media/{id}` with the updated fields.

---

## API Overview (for power users)

- **Roots**

  - `GET /api/roots` – list all roots
  - `POST /api/roots` – add a root
  - `GET /api/roots/{id}/scan-dry-run` – quick count of discoverable files
  - `POST /api/roots/{id}/scan-live?scanId=<guid>&thumbs=true&regenAll=false&dop=8` – run a live scan (SignalR updates)

- **Media**

  - `GET /api/media/browse?skip=0&take=36&source=Studio&orientation=Gay&sort=updated_desc&q=term`
  - `GET /api/media/{id}/thumb` – latest stored thumbnail
  - `GET /api/media/{id}/stream` – range-enabled file streaming (browser support depends on codecs)
  - `PUT /api/media/{id}` – update metadata (see `UpdateMediaDto` in `Program.cs`)

- **Meta helpers**

  - `GET /api/meta/distinct?field=studio|performers|orientation|other|type`

- **Utilities**
  - `POST /api/media/{id}/open` – open in OS default player
  - `POST /api/media/{id}/reveal` – show in Explorer (Windows)

> SignalR hub: `/hubs/scan` – the UI joins `scan:{scanId}` groups to receive progress events.

---

## Development Notes

- Global styles live in **`wwwroot/css/app.css`** and **`wwwroot/css/tags.css`**.  
  The nav and the page wrapper are defined in `Pages/Shared/_TopNav.cshtml` and `_TopNavEnd.cshtml`.
- If you changed or removed the default Razor layout, ensure each page includes the top‑nav partial so the `.page` wrapper styling applies.
- Media playback relies on browser support for the file’s codecs. Use **Open in player** for formats your browser can’t handle.

---

## Git & Repository Tips

- Initialize the repository at the **parent** folder that contains **both** `VideoIndex.Core` and `VideoIndex.Web`:
  ```bash
  git init -b main
  git add .
  git commit -m "Initial commit"
  ```
- Optional: create a solution at the repo root:
  ```bash
  dotnet new sln -n VideoIndex
  dotnet sln add VideoIndex.Core/VideoIndex.Core.csproj
  dotnet sln add VideoIndex.Web/VideoIndex.Web.csproj
  ```

---

## Troubleshooting

- **“ffprobe failed / ffmpeg failed”**: Ensure both are installed and on your `PATH`.
- **No thumbnails**: Check the “Generate thumbnails” toggle when scanning; verify ffmpeg availability.
- **Playback errors**: Some codecs/containers don’t play natively in browsers—use **Open in player**.
- **Database path**: Verify the SQLite connection string location is writable by the app.
