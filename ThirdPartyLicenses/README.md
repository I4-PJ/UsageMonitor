# Supplemental dependency notices

The packaging scripts copy license and notice files from the exact runtime packages in the restored NuGet graph. They also include the .NET runtime's bundled notices.

These upstream notices supplement packages that omit a license file, and the embedded Inter font:

- `Avalonia.LICENSE.txt`: [Avalonia 11.1.0](https://github.com/AvaloniaUI/Avalonia/blob/11.1.0/licence.md).
- `MicroCom.LICENSE.txt`: [MicroCom](https://github.com/kekekeks/MicroCom/blob/76785efcafd91b5902fd19dd11145f6dd655b7b4/LICENSE), corresponding to the MIT license identified by MicroCom.Runtime 0.11.0.
- `Tmds.DBus.LICENSE.txt`: [Tmds.DBus 0.16.0](https://github.com/tmds/Tmds.DBus/blob/ee8c7b698b320c19c0e0e8acb05622d99ee8f157/COPYING).
- `Inter.OFL.txt`: [Inter's SIL Open Font License](https://github.com/rsms/inter/blob/v4.0/LICENSE.txt).

The collector rejects a runtime dependency with no license text or known fallback. Review these notices when upgrading dependencies.
