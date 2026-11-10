# Third-party components

Luma uses unmodified, dynamically linked components from these projects:

| Component | Version | License and upstream source |
| --- | --- | --- |
| LibVLCSharp / LibVLCSharp.WPF | 3.10.1 | LGPL-2.1-or-later — https://code.videolan.org/videolan/LibVLCSharp |
| VideoLAN.LibVLC.Windows | 3.0.23.1 | LGPL-2.1-or-later — https://code.videolan.org/videolan/libvlc-nuget |
| .NET runtime / Windows Desktop runtime | Bundled .NET 10 | MIT and component notices — https://github.com/dotnet/runtime and https://github.com/dotnet/wpf |

Copyright belongs to the respective authors and contributors. The application is not affiliated with VideoLAN or an IPTV provider. No third-party code was modified.

The native VideoLAN files are in `libvlc/win-x64` and may be replaced with ABI-compatible versions. LibVLCSharp assemblies are alongside the application and may also be replaced. Debugging modifications to these LGPL libraries is not restricted by this application.

VideoLAN source and build instructions are available at the repositories above. VLC 3.0.23 source archives: https://download.videolan.org/pub/videolan/vlc/3.0.23/ . The NuGet packaging source describes the bundled native build and dependencies. Preserve licensing notices when redistributing those components.

Full license text and .NET third-party notices accompany the application in `licenses`.
