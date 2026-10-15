<h1 align="center">Jellyfin VOD Plugin</h1>

<p align="center">
Follow YouTube, Twitch and other online channels from Jellyfin: their videos appear in a library
and are streamed on demand through <a href="https://github.com/yt-dlp/yt-dlp">yt-dlp</a>, with
nothing downloaded to disk.
</p>

> **Early release.** The plugin is used daily on one server but has not been tested widely yet.
> Expect rough edges, and please report what you find.

## What it does

- **Sources**: add a channel, a channel tab (`/videos`, `/streams`), a playlist or a single video.
  Tested with YouTube, Twitch and Arte; other sites yt-dlp supports may work.
- **Virtual library**: each source is published into a Jellyfin library you choose, either as a
  series (one episode per video, seasons by year) or as movies. Titles, descriptions, dates and
  artwork come from the source.
- **On-demand streaming**: playback resolves the video's HLS stream at that moment. No file is
  stored, so the library costs no disk space.
- **Quality versions**: a video offered in several qualities gets a "Version" menu — the best H.264
  (usually 1080p) and the best available (4K, 1440p, VP9/AV1/HEVC) when it differs. Stream
  descriptions are probed ahead of playback so Jellyfin can copy rather than transcode.
- **Audio and subtitle tracks**: multiple audio tracks and subtitles are kept, with their names
  (e.g. "français (confort audio)"), filtered by your preferred languages.
- **Lives**: a broadcast in progress is detected within minutes and playable as a live item; it
  turns into a regular video once the replay is available.
- **Scheduled sync**: each source is refreshed on its own interval; old videos can be capped by
  count or by age.

## Requirements

- **Jellyfin 12.1** or later.
- **yt-dlp**, reachable from the Jellyfin server process. The official Jellyfin image does not
  include it, nor Python: use the **standalone executable** from the
  [yt-dlp releases](https://github.com/yt-dlp/yt-dlp/releases/latest) — `yt-dlp_linux` on x86-64,
  `yt-dlp_linux_aarch64` on ARM64 — **not** the asset named plain `yt-dlp`, which is a Python
  script and fails with `env: 'python3': No such file or directory`. For example:
  ```bash
  curl -L -o /path/to/jellyfin/config/yt-dlp \
    https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp_linux
  chmod +x /path/to/jellyfin/config/yt-dlp
  ```
  then set **yt-dlp path** to where the container sees it, e.g. `/config/yt-dlp`. Keep it up to
  date (`/config/yt-dlp -U`): sites change often, and an outdated yt-dlp is the most common cause
  of failures.
- **A JavaScript runtime for YouTube.** yt-dlp needs one to solve YouTube's challenges.
  [Deno](https://github.com/denoland/deno/releases/latest) ships as a single executable inside
  `deno-x86_64-unknown-linux-gnu.zip` (or `deno-aarch64-unknown-linux-gnu.zip` on ARM64):
  ```bash
  curl -L -o /tmp/deno.zip \
    https://github.com/denoland/deno/releases/latest/download/deno-x86_64-unknown-linux-gnu.zip
  unzip -o /tmp/deno.zip -d /path/to/jellyfin/config/
  ```
  then set **JS runtime** to `deno:/config/deno`.
- **Optional — cookies.** A Netscape-format cookie file lets yt-dlp read age-restricted or
  members-only videos and helps against bot checks. A cookie file is as good as the account's
  password: **use a throwaway account**, never a personal one.

## Installation

1. In Jellyfin, open **Dashboard → Plugins → Repositories** and add:
   ```
   https://raw.githubusercontent.com/Bakeneko/jellyfin-plugin-vod/main/manifest.json
   ```
2. Install **VOD** from the catalogue and restart Jellyfin.

## Setup

1. **Create the target library.** Add a Jellyfin library pointing to an **empty** folder: a
   Shows library for series, a Movies library for movies, or a single Mixed library for both. Its
   name must match the plugin's **Series library** / **Movies library** settings (`VOD` by
   default). The plugin writes nothing there but a `.keep` marker; its items are virtual.
2. **Configure the plugin** (Dashboard → Plugins → VOD):
   - **yt-dlp path** and **JS runtime**, as above;
   - **Local base URL**: the address the Jellyfin server uses to reach itself, **from inside its
     own container** — usually `http://localhost:8096`, even when you browse Jellyfin on another
     port. Streams fail if this is wrong;
   - preferred audio and subtitle languages, and the library names.
3. **Add sources**, choosing for each one the mode (Series or Movies) and, optionally, how many
   videos to keep and for how long.
4. Run **VOD Sync** from **Dashboard → Scheduled Tasks**, or wait for its next run.

Scheduled tasks added by the plugin, all under the **VOD** category:

| Task | Default | Role |
|---|---|---|
| VOD Sync | hourly | refreshes each source once its own interval has elapsed (12 h by default) |
| VOD Live Probe | every 5 minutes | finds broadcasts in progress |
| VOD Resolution | every 15 minutes | resolves pending video metadata and pre-describes quality versions ahead of playback |

## Known limitations

- **Transcoding cost.** Codecs your client cannot play (VP9/AV1 on some TVs, for instance) are
  transcoded by Jellyfin like any other media: hardware acceleration is strongly recommended for
  4K versions.
- **First playback of a just-published video** may still be transcoded if the resolution wave
  has not yet run; description happens in the same wave as resolution.
- **A single-video source that becomes private** stays in the library; the source shows an error.
- **Live latency.** Lives are served from the live edge with the usual HLS delay.
- **The stream endpoint is not authenticated**, because ffmpeg calls it without a token. It only
  answers for known video ids, but anyone who can reach your server and knows one can make it run
  yt-dlp.
- Sites may throttle or block heavy use; the plugin paces its calls, but a large number of sources
  synced often will be noticed.

## Building from source

Requires the .NET 10 SDK.

```bash
dotnet test
dotnet publish Jellyfin.Plugin.VOD --configuration Release --output publish
```

Copy `publish/Jellyfin.Plugin.VOD.dll` into a `VOD` folder under your Jellyfin `plugins/` directory
and restart Jellyfin. Only that DLL is needed: SQLite and the Jellyfin assemblies come from the
server.

## License

[GPL-3.0](LICENSE)
