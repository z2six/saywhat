# SayWhat? artwork

Approved identity: concept 01, with the question-mark dot optically shifted left beneath its stem. The public display name is `SayWhat?`; Windows filenames omit the question mark.

- `SayWhat.ico`: Windows executable, installer and tray icon. Nine PNG-backed resolutions: 16, 20, 24, 32, 40, 48, 64, 128 and 256 px.
- `saywhat-icon-256.png`: square coral/ivory icon with transparent outer corners, also used for the installer header.
- `saywhat-mark.png`: the same approved coral icon tile for compact application and subtitle marks.
- `saywhat-lockup.png`: the approved symbol and wordmark, ivory/coral on a dark sidebar background. This asset is intentionally opaque; defective transparent wordmark trials were not shipped.

The app embeds the artwork, caches frozen decoded images, and gives each tray owner an independent disposable icon. Loading artwork does not start models or audio capture.

The raster artwork was prepared using the built-in image-generation tool, referencing the approved proofsheet. It is not a claim of finished SVG artwork. Mechanical icon format conversion preserves alpha and does not redraw or recolor the logo.

To reproduce the ICO from its included public 256px source:

```powershell
.\Export-WindowsIcons.ps1 -SourcePng .\saywhat-icon-256.png
```

The format exporter writes only artwork files in its chosen output directory and optionally an icon-size proofsheet. It does not launch the app, models, installers, network requests, or cleanup operations.
