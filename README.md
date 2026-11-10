# Luma IPTV

A native Windows IPTV player with a dark teal interface, an adaptive sidebar, integrated VLC playback, and Xtream Codes API support.

## Run

Extract the entire **LumaIPTV-Windows-x64.zip** archive, open the `LumaIPTV` folder, and double-click **LumaIPTV.exe**. Keep the application and its accompanying files together. The package includes .NET and VLC; neither needs a separate installation. Intended for 64-bit Windows 10/11 PCs.

Enter the server URL (including `http://` or `https://`, and the port if provided), username, and password issued by your IPTV provider. For example: `https://provider.example:8080`. Do not paste an M3U URL or a URL containing your login parameters. The app also accepts a base URL ending in `/player_api.php`.

Use **Explore the demo** to browse sample titles without an account. The demo is a UI preview; it includes no television streams. No subscription or channels are supplied with the application.

## Watching

- Browse **Live TV**, **Movies**, or **Series**. Selecting a playable title starts playback; selecting a series opens its episodes.
- Search by name and filter by category or season. The heart saves a favorite. The sidebar favorites button filters the current library, rather than combining every media type.
- The player supports pause, stop, volume, and seeking when the provider's stream allows it. If a live stream fails, change **MPEG-TS** to **HLS** or vice versa and press play again.
- **F11** toggles fullscreen; **Escape** exits it. The space bar toggles playback when focus is outside a text field or control that uses it.
- Program guide information appears when the provider supplies it. Times use local time when the API returns Unix timestamps.
- The sidebar collapses into icons in smaller windows. Hover an icon to see its label.
- **Sign out** stops playback and removes the saved account. Favorites remain associated with that server and username.

## Account storage

Login requests go directly from your PC to your IPTV provider. This app has no analytics or external account service. If **Remember this account** is unchecked, credentials stay in memory for the session. If checked, Windows DPAPI encrypts them for your Windows user account in `%LOCALAPPDATA%\LumaIPTV\account.dat`. Favorites use separate files in the same directory. HTTP server addresses send credentials and content over an unencrypted connection; use your provider's HTTPS address when available.

## Validation and limits

The release passed 22 automated API, encryption, and native playback checks, plus 11 UI navigation/layout checks. Tests cover sample provider responses, reserved characters in credentials, rejected logins, cancellation, episodes, guide text, Windows encryption, native media decoding, pause/resume/seek/stop, search, category filtering, favorites, series navigation, compact layout, and fullscreen. Login and dashboard layouts were visually inspected.

Your provider account and actual IPTV streams have not been tested because credentials were not supplied. Xtream implementations vary; providers may impose connection limits, block certain clients, or expose unavailable streams. DRM playback, recording, catch-up TV, audio/subtitle track selection, and automatic updates are not implemented in this first version. The executable is not code-signed.

## Build from source

Install the .NET 10 SDK for Windows, then run in this folder:

```powershell
dotnet publish LumaIPTV.csproj -c Release -r win-x64 --self-contained true -o publish
```

Dependencies are pinned in the project: LibVLCSharp.WPF 3.10.1 and VideoLAN.LibVLC.Windows 3.0.23.1. NuGet downloads are required on the first build. Source files remain editable; the native playback libraries are dynamically linked and replaceable.

Run automated checks on the published app:

```powershell
.\publish\LumaIPTV.exe --self-test "$PWD\test-results.txt"
.\publish\LumaIPTV.exe --ui-test "$PWD"
```

The UI test opens a window briefly, exercises the demo, and writes `login.png`, `dashboard.png`, `compact.png`, and `ui-results.txt` to the requested existing directory. Tests do not require provider credentials or change saved account files. For a demo-only launch, use `--demo`.

## Components

- `MainWindow.xaml` / `.xaml.cs`: native WPF layout, browsing, playback, and navigation.
- `Services.cs`: Xtream API client, account encryption, and favorites persistence.
- `SelfTest.cs`: deterministic provider fixtures and native media smoke tests.
- `App.xaml`: shared visual theme.

See `THIRD-PARTY-NOTICES.md` and the `licenses` directory for dependency attribution and licensing.
