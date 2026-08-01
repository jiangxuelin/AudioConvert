# MSIX Packaging Guide

This repository includes a Windows Application Packaging Project under `AudioConvert.Package`.

## Before Building Store Packages

Install these Visual Studio components first if they are not already installed:

- MSIX Packaging Tools
- Windows Application Packaging Project support
- A Windows 10 or Windows 11 SDK that includes app packaging tools

1. Open `AudioConvert.sln` in Visual Studio 2022.
2. Right-click `AudioConvert.Package`.
3. Select **Publish > Associate App with the Store**.
4. Sign in with the personal developer account.
5. Select the app name you already reserved.
6. Let Visual Studio update `Package.appxmanifest` with the Store identity and publisher values.

The checked-in manifest uses local placeholder identity values only so the project can exist before Store association.

## Create the Upload Package

1. Set configuration to `Release`.
2. Right-click `AudioConvert.Package`.
3. Select **Publish > Create App Packages**.
4. Choose **Microsoft Store using a new app name** or the existing associated app.
5. Generate the `.msixupload` package.
6. Run Windows App Certification Kit when prompted.
7. Upload the generated `.msixupload` file in Partner Center.

You can also run:

```powershell
.\scripts\Build-StorePackage.ps1 -Configuration Release
```

The script checks whether the required DesktopBridge/MSIX build targets are installed before trying to build the Store package.

## Required Manual Checks

- Confirm the bundled `ffmpeg.exe` license and build configuration.
- Confirm redistribution permission for EasyHook and all compatibility DLLs.
- Confirm redistribution permission for `kgm.mask`.
- Host the privacy policy draft at a public URL and paste it into Partner Center.
- Keep the Store listing wording focused on lawful local conversion and avoid implying affiliation with third-party music services.
