<h1 align="center">Jellyfin VOD Plugin</h1>

## About

Get a virtual library of VOD channel/playlist with videos synched, cached and streamed on demand via yt-dlp.


## Build & Installation Process

1. Clone this repository

2. Ensure you have .NET Core SDK setup and installed

3. Build the plugin with following command:

```bash
dotnet publish --configuration Release --output bin
```

4. Place the resulting `Jellyfin.Plugin.VOD.dll` file in a folder called `plugins/` inside your Jellyfin installation / data directory.

Repository: https://raw.githubusercontent.com/bakeneko/jellyfin-plugin-vod/main/manifest.json