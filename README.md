# Media Nexus ARM

<p align="center">
  <img src="assets/Media-Nexus-ARM.png" alt="Media Nexus ARM logo" width="256">
</p>

Media Nexus ARM is a Windows automatic ripping machine for movies, TV series, music CDs, and audiobooks across multiple optical drives.

## Current v0.2 features

- Remembers any number of user-selected optical drives; tested around a seven-drive layout
- Automatically distinguishes audio CDs, likely movies, and likely TV discs
- Keeps a per-drive manual Movie / TV Series / Music / Book override
- Selects a probable main feature for movies instead of copying every title
- Clusters similarly timed episode titles while excluding likely Play All and short extras
- Runs independent MakeMKV and audio jobs concurrently
- Downloads and validates the official portable fre:ac 1.1.7 engine on first audio rip
- Rips audio CDs to lossless ALAC `.m4a`; iTunes is no longer used
- Calculates MusicBrainz Disc IDs directly from the physical CD TOC
- Retrieves MusicBrainz release/track metadata and Cover Art Archive artwork
- Embeds tags and artwork, then organizes music as `Music\Artist\Album (Year)\01 - Track.m4a`
- Preserves successful audio under `Pending Metadata` when identification is unavailable
- Shows independent status and progress per drive and ejects every completed/failed disc
- Produces per-job logs and retains recoverable staging files when final processing fails

## Requirements

- Windows 10 or Windows 11, x64
- [MakeMKV](https://www.makemkv.com/) installed in its standard Program Files location
- One or more optical drives
- Internet access for the first music rip and music metadata/artwork

No iTunes, MusicBrainz Picard, fre:ac installation, FFmpeg, Python, or API key is required. Media Nexus ARM manages a private portable fre:ac copy under `%LOCALAPPDATA%\Media Nexus ARM\Data\Tools\freac`.

## Download and run

1. Download `Media-Nexus-ARM.exe` from this repository or the latest release.
2. Install MakeMKV.
3. Run `Media-Nexus-ARM.exe`.
4. Select the optical drives to manage and an output folder.
5. Leave each drive on **Auto** for automatic detection, or select a media type as an override.
6. Insert discs.

The application is portable and does not create an installer or automatic-start entry.

## Output layout

```text
<selected output>/
|-- Movies/
|-- TV Series/
|-- Music/
|-- Audiobooks/
|-- Pending Metadata/
|-- Staging/
`-- Logs/
```

MusicBrainz or artwork failure does not discard a successful extraction. Unidentified albums are retained in `Pending Metadata`; incomplete post-processing remains in `Staging` for recovery.

## Automatic video selection

Media Nexus ARM uses MakeMKV's title information without FFmpeg/ffprobe. A clearly dominant feature-length title is treated as a movie. A group of two or more similarly timed 15-90 minute titles is treated as probable TV episodes; short extras and a combined Play All title are excluded from the rip selection. Low-confidence discs stop at **Needs identification** so the user can choose an override instead of accepting a silent guess.

These are conservative heuristics. Unusually authored or obfuscated discs can still require manual selection.

## Settings and privacy

Drive selections, output location, and layout preferences are stored for the current Windows user under `HKEY_CURRENT_USER\Software\DiscRipper`.

The executable contains no user-specific paths, credentials, tokens, telemetry, or automatic reporting. Network access is limited to downloading the managed fre:ac package and requesting MusicBrainz/Cover Art Archive data during music processing. MakeMKV and fre:ac remain subject to their own licenses and behavior.

## Build from source

Run:

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

The result is `dist\Media-Nexus-ARM.exe`. TagLibSharp is embedded in the executable by the build.

## Roadmap

The local IMDb dataset, high-confidence Plex movie naming, and assisted TV season/episode naming described for the larger v0.2 upgrade remain the next video-metadata milestone. The current build improves video classification/title selection but does not claim IMDb-based naming yet.

## Third-party components

- [fre:ac](https://www.freac.org/) is downloaded from its official release and managed separately at runtime.
- [TagLibSharp 2.3.0](https://github.com/mono/taglib-sharp) is embedded for M4A metadata and is licensed under LGPL-2.1-only. See `THIRD-PARTY-NOTICES.md`.

Only rip media you own or are legally authorized to copy. Laws and license terms vary by location.
