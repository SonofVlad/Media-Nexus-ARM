# Media Nexus ARM

<p align="center">
  <img src="assets/Media-Nexus-ARM.png" alt="Media Nexus ARM logo" width="256">
</p>

Media Nexus ARM is a Windows automatic ripping machine for processing movies, TV series, music CDs, and audiobooks across multiple optical drives. Video discs are handled by MakeMKV, while audio CDs can be imported through iTunes.

## Features

- Detects connected optical drives and remembers the drives selected by the user
- Runs independent MakeMKV jobs across multiple drives
- Provides Movie, TV Series, Music, and Book modes
- Displays per-drive status and a master MakeMKV progress bar
- Supports configurable window and column sizes
- Supports local, external, mapped, and UNC network output folders
- Separates output into Movies, TV Series, Music, Audiobooks, and Logs
- Automatically ejects discs after success or failure
- Uses distinct Windows sounds for successful and failed jobs
- Retains MakeMKV logs for failed video jobs
- Includes an embedded multi-resolution Windows application icon

## Requirements

- Windows 10 or Windows 11
- [MakeMKV](https://www.makemkv.com/) installed in its standard Program Files location
- iTunes for Windows when using Music or Book mode
- One or more optical drives

## Download and run

1. Download `Media-Nexus-ARM.exe` from this repository.
2. Install MakeMKV and, if needed, iTunes.
3. Run `Media-Nexus-ARM.exe`.
4. Select the optical drives Media Nexus ARM should manage.
5. Choose an output folder. Local disks, external disks, mapped drives, and UNC paths are supported.
6. Select Movie, TV Series, Music, or Book for each drive—or use a Change All button.
7. Insert the discs.

No installer or automatic-start entry is created.

## iTunes configuration

For Music and Book modes, configure iTunes before starting:

1. Open **Edit > Preferences > General**.
2. Set **When a CD is inserted** to **Import CD and Eject**.
3. Open **Import Settings** and select **MP3 Encoder** with the desired quality.

iTunes audio jobs are processed one at a time. After iTunes ejects a disc, Media Nexus ARM copies newly imported MP3 files into the selected output folder. The files remain in the iTunes library.

## Output layout

```text
<selected output>/
├── Movies/
├── TV Series/
├── Music/
├── Audiobooks/
└── Logs/
```

Each disc receives its own timestamped folder.

## Current title-selection behavior

### Movie mode

Movie mode currently asks MakeMKV to copy all titles at least 10 minutes long. Depending on the disc, this can include alternate cuts or substantial bonus features.

### TV Series mode

TV mode scans title durations and keeps titles at least 10 minutes long. When at least three qualifying titles are found, it excludes the longest title as a likely **Play All** playlist only if:

- the longest title is at least 1.8 times the median qualifying duration; and
- the longest title is at least 1.4 times the duration of the second-longest title.

If title parsing fails, the program falls back to copying all titles that satisfy MakeMKV's 10-minute minimum.

These are heuristics. Optical discs do not consistently identify movies, episodes, bonus material, alternate cuts, or deliberately obfuscated playlists.

## Progress reporting

MakeMKV robot-mode `PRGV:current,total,max` messages drive one master progress bar in each drive's Status column. For TV discs copied title-by-title, progress is combined into whole-disc progress.

## Settings and privacy

Drive selections, output location, and layout preferences are stored for the current Windows user under:

```text
HKEY_CURRENT_USER\Software\DiscRipper
```

Media Nexus ARM does not contain user-specific paths, NAS names, credentials, API tokens, telemetry, or automatic network reporting. MakeMKV and iTunes remain subject to their own licenses and behavior.

## Build from source

The repository includes a PowerShell build script that uses the C# compiler bundled with the Windows .NET Framework:

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

The resulting executable is written to `dist\Media-Nexus-ARM.exe`.

## Known limitations

- A physical-disc end-to-end test is recommended before loading several drives.
- The Microsoft Store edition of iTunes does not expose the legacy COM automation interface, so audio completion is detected through disc ejection and newly created MP3 files.
- Completely accurate automatic movie and episode identification is not possible for every authored or obfuscated disc.
- The program currently retains detailed MakeMKV logs only for failed video jobs.

Only rip media you own or are legally authorized to copy. Laws and license terms vary by location.
