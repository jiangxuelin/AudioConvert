# Third-Party Notices

This application includes or interoperates with the following third-party components. Before publishing to Microsoft Store, verify that the listed binaries match the licenses shown here and keep this notice available from the app, your website, or the Store listing support materials.

## FFmpeg

- Component: `AudioConvert/Tools/ffmpeg.exe`
- Project site: https://ffmpeg.org/
- Legal information: https://ffmpeg.org/legal.html
- License: FFmpeg builds are commonly distributed under LGPL 2.1 or later; builds that enable GPL components are subject to GPL terms. The publisher must verify the exact build configuration of the bundled `ffmpeg.exe`.
- Use in this app: local audio conversion, compression, trimming, and merging.
- Notice: FFmpeg is a trademark of Fabrice Bellard, originator of the FFmpeg project. This application is not affiliated with or endorsed by the FFmpeg project.

Recommended publisher action before submission:

1. Confirm whether the bundled `ffmpeg.exe` is LGPL-only or GPL-enabled.
2. Keep the exact FFmpeg source offer/build information available to users if required by the applicable license.
3. If the current binary source is uncertain, replace it with a known compliant build and retain its license/readme files.

## EasyHook

- Component: `ThirdParty/EasyHook`
- Project site: https://easyhook.github.io/
- Repository: https://github.com/EasyHook/EasyHook
- Use in this app: full-trust local interoperability component used by the music-platform conversion workflow.
- Publisher action required: verify the exact EasyHook version and include the upstream license text that shipped with that version.

## Microsoft Compatibility Assemblies

Components:

- `ThirdParty/Compat/System.Buffers.dll`
- `ThirdParty/Compat/System.Memory.dll`
- `ThirdParty/Compat/System.Numerics.Vectors.dll`
- `ThirdParty/Compat/System.Runtime.CompilerServices.Unsafe.dll`

Use in this app: .NET Framework compatibility libraries required by the application at runtime.

Publisher action required: verify the NuGet package versions these binaries came from and include the corresponding Microsoft license notices.

## Runtime Data Files

- Component: `AudioConvert/Tools/kgm.mask`
- Use in this app: local music-format conversion support data.
- Publisher action required: verify ownership, redistribution permission, and policy fit before Microsoft Store submission.

## Music Platform Interoperability

This app includes local conversion workflows for user-selected audio files from supported music platforms. These workflows must only be used for files the user owns or is otherwise legally allowed to process. The publisher should not market the app as bypassing subscription, access control, copyright protection, or digital rights management, and should keep supporting documentation focused on lawful personal-file conversion.

