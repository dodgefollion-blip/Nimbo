# Nimbo

Nimbo is a Windows x64 memory utility with a custom near-black interface.

## Features

- Live physical memory, commit-limit, and system file-cache readings.
- **Clean RAM** trims accessible process working sets only. It does not delete files or change the system file cache.
- Optional cleanup of the documented Windows system file-cache working set.
- Optional cleanup of files in the current user's `%TEMP%` directory. This permanently deletes eligible files without using the Recycle Bin; busy, inaccessible, and linked items are skipped. This action requires confirmation and never runs automatically.
- Threshold and interval automation for memory cleanup, a local history, and a tray indicator.

The app does not modify the Windows standby list. Windows may reuse or reload trimmed pages, so more available memory or a speed increase is not guaranteed. No process names are stored in the history.

## Download and run

Download `Nimbo.exe` from the latest GitHub Release and open it. The release is a self-contained Windows x64 build; a separate .NET installation is not required.

## Build from source

Install the .NET 8 SDK, then run:

```powershell
dotnet build .\Nimbo.csproj -c Release
dotnet publish .\Nimbo.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:SatelliteResourceLanguages=ru
```

## Local data

Settings and the last 200 cleanup records are stored in `%LOCALAPPDATA%\Nimbo`.
