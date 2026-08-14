# Media Nexus ARM

<p align="center">
  <img src="assets/Media-Nexus-ARM.png" alt="Media Nexus ARM logo" width="256">
</p>

Media Nexus ARM is a Windows automatic ripping machine for movies, TV series, music CDs, and audiobooks across multiple optical drives.

## Current features

- Manages any number of remembered optical drives, with independent status, progress, Stop, and Eject controls.
- Waits for an explicit Movie, TV Series, Music, or Audiobook choice; unused types can be hidden.
- Runs video and audio jobs concurrently while keeping the interface responsive.
- Selects high-confidence movie features automatically and prompts when a disc is ambiguous.
- Finds likely TV episode groups using runtime, chapter, playlist, and Play All analysis, with optional expected-episode count.
- Uses the editable disc name for movie and TV folders; movie files are renamed while TV filenames are preserved.
- Rips Music and Audiobooks through a managed fre:ac engine in ALAC, FLAC, or MP3.
- Uses MusicBrainz and Cover Art Archive for music tags/artwork, retaining unidentified rips under `Pending Metadata`.
- Includes configurable completion behavior, sounds, Light/Dark themes, layout controls, resolution presets, and 5% Ctrl+wheel zoom.
- Provides 30-day job logs, log-backed History, and recoverable staging files when post-processing fails.

## Requirements

- Windows 10 or Windows 11, x64
- [MakeMKV](https://www.makemkv.com/) installed in its standard Program Files location
- One or more optical drives
- Internet access for the first music rip and music metadata/artwork

No iTunes, MusicBrainz Picard, fre:ac installation, FFmpeg, Python, or API key is required. Media Nexus ARM stores its managed data under `%LOCALAPPDATA%\Media Nexus\ARM`, including a private portable fre:ac copy in `Tools\freac`.

## Download and run

1. Download `Media-Nexus-ARM.exe` from this repository or the latest release.
2. Install MakeMKV.
3. Run `Media-Nexus-ARM.exe`.
4. Select the optical drives to manage and an output folder.
5. Insert a disc and select its media type.

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

## Video handling

Media Nexus ARM uses MakeMKV title information without FFmpeg. Movie mode selects a likely main feature and asks for confirmation when uncertain. TV mode finds similarly authored episodes while excluding short extras and likely Play All titles.

These are conservative heuristics. Unusually authored or obfuscated discs can still require manual selection.

Movie mode creates `Movies\<Disc Name>\<Disc Name>.mkv`. TV mode creates `TV Series\<Disc Name>\` and preserves MakeMKV's filenames. The Disc field can be edited while ripping.

## Settings and privacy

Drive selections, output location, and layout preferences are stored for the current Windows user under `HKEY_CURRENT_USER\Software\DiscRipper`.

The executable contains no user-specific paths, credentials, tokens, telemetry, or automatic reporting. Network access is limited to downloading the managed fre:ac package and requesting MusicBrainz/Cover Art Archive data during music processing. MakeMKV and fre:ac remain subject to their own licenses and behavior.

## Build from source

Run:

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

The result is `dist\Media-Nexus-ARM.exe`. TagLibSharp is embedded in the executable by the build.

## Third-party components

- [fre:ac](https://www.freac.org/) is downloaded from its official release and managed separately at runtime.
- [TagLibSharp 2.3.0](https://github.com/mono/taglib-sharp) is embedded for M4A metadata and is licensed under LGPL-2.1-only. See `THIRD-PARTY-NOTICES.md`.

Only rip media you own or are legally authorized to copy. Laws and license terms vary by location.
