# Takos Toolkit

A portable, single-EXE toolbox for IT troubleshooting on Windows 10/11. No install and no .NET runtime needed.

## Tools

| Tool | What it does |
|---|---|
| **System Snapshot** | Device, OS, user, hardware, storage and network details on one page. **Copy for ticket** puts it all on the clipboard as plain text; click any value to copy just that value. |
| **Disk Space** | Scans a drive or folder and shows it as a treemap, like SpaceMonger. Double-click a folder to zoom in, use **Up** or the mouse back button to zoom out, and right-click for *Show in Explorer* or *Copy path*. The side panel lists the largest files, a breakdown by file type, and a color legend. |

## Command line

```
TakosToolkit.exe --disk [path]   Open Disk Space and scan the path (default: system drive)
```

## Build

Requires the .NET 10 SDK.

```
dotnet build                               # debug build
dotnet test                                # unit tests
dotnet publish src/TakosToolkit -c Release -o publish
```

`publish/TakosToolkit.exe` is the whole app (about 70 MB, self-contained and compressed).

## Layout

```
src/TakosToolkit/         WPF app (WPF-UI Fluent theme): window, pages, treemap control
src/TakosToolkit.Core/    UI-free logic: disk scanner, treemap layout, system info
tests/TakosToolkit.Tests/ xUnit tests for Core
```

## How Disk Space scans

| Situation | Method | Speed (example: 1.8M files, 1.25 TB) |
|---|---|---|
| Whole NTFS drive, running as admin | Reads the NTFS Master File Table directly | ~6 s |
| A folder, a network share, a non-NTFS drive, or no admin rights | Lists folders in parallel | ~20–50 s |

The app runs as the current user. **Restart as admin** reopens it elevated and rescans the same location using the fast method. If the MFT can't be read for any reason, the scan falls back to the folder walk, and the status line says why.

How sizes are counted:
- **Hard links** count once, under the file's first name, so the totals match real disk usage. Explorer and the folder walk count each link separately. Windows hard-links many files between WinSxS and System32, and some apps (game launchers, package caches) do the same, so their folders can look several GB smaller here than in Explorer.
- **Junctions and symlinks** are skipped so nothing is counted twice.
- **Cloud-only OneDrive files** count as 0 bytes because they take no local space.
- The MFT scan also shows space that the folder walk can't see: System Volume Information (restore points) and NTFS system files such as `$MFT`.
