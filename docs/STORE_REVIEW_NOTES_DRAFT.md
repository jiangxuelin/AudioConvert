# Microsoft Store Review Notes Draft

Use this text in Partner Center certification notes after replacing TODO fields.

## App Summary

AudioConvert is a local Windows desktop audio conversion tool. Users select local audio files and output folders. The app processes files on the user's device and does not require account sign-in or cloud upload for its core features.

## Included Functionality

- Convert common local audio formats using bundled FFmpeg.
- Compress audio to MP3.
- Trim audio clips.
- Merge local audio files.
- Convert supported user-selected music-platform audio files when the user has the legal right to process those files.

## Full-Trust Components

This MSIX package is a full-trust desktop app. It includes helper executables and local interoperability components required by the conversion workflow:

- `AudioConvert.exe`
- `QQMusicDecryptRunner.exe`
- `QQMusicDecryptHook.dll`
- `KugouInfraRunner.exe`
- EasyHook runtime binaries
- `ffmpeg.exe`

These components are used locally on the user's device and are not designed to collect personal data or upload user files.

## Music-Platform Workflow Clarification

The music-platform conversion workflow is intended only for user-selected files that the user owns or is legally authorized to process. The app should not be used to bypass subscriptions, access restrictions, copyright protection, or digital rights management. The Store listing should avoid implying affiliation with or endorsement by any third-party music service.

## Privacy

The app processes selected files locally. It does not require account sign-in and does not intentionally collect, upload, sell, or share personal data.

Privacy policy URL: TODO: paste the hosted privacy policy URL

## Third-Party Components

The app includes FFmpeg and other third-party runtime components. Third-party notices are provided in `docs/THIRD_PARTY_NOTICES.md`.

## Test Instructions

1. Install the MSIX package.
2. Launch AudioConvert from the Start menu.
3. Select one or more local audio files.
4. Choose an output folder and conversion action.
5. Confirm that output files are created locally.

Optional music-platform workflow test requires locally available, legally authorized sample files and the corresponding installed platform client.

