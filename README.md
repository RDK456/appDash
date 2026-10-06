# AppDash

See every app on your PC or phone: what's installed, what you actually use, how much space it takes, and what can be updated.

## Windows

- Installed apps (as in Settings > Apps) and portable apps (found by scanning your drives), with sizes, categories, repair and uninstall
- Usage from Windows launch history: used often, sometimes, rarely, plus uninstalling several rarely used apps at once
- Updates through winget and Chocolatey, with one-click install of either
- Disks: space by file type (videos, archives, documents…) with the largest files, open, Recycle Bin or permanent delete
- Light and dark mode; updates itself with small delta packages

Download `AppDashApp-win-Setup.exe` from [Releases](../../releases/latest). Needs Windows 10/11; Setup installs the .NET 8 Desktop Runtime if missing.

## Android

- Your apps and system apps with sizes, screen time, last use, uninstall and Play Store links
- Phone storage by file type with the largest files, trash or delete
- Asks for Usage access and All files access, and explains why

Download `AppDash-<version>.apk` from [Releases](../../releases/latest) (Android 11+). The app checks this repo's latest release for updates.

## Build a release

Windows (needs the .NET 8 SDK and `dotnet tool install -g vpk`):

```powershell
.\build-release.ps1 -Version 1.2.0 -Feed https://github.com/RDK456/appDash/releases/latest/download
```

Android (needs the Android SDK and JDK 17):

```powershell
.\android\build-release.ps1 -Version 1.2.0 -Feed https://github.com/RDK456/appDash/releases/latest/download/latest.json
```

The first Android build creates `android/appdash-release.jks` and `android/keystore.properties`. They are git-ignored; back them up, because every future update must be signed with the same key.

Then upload `releases\*` (Windows) and `android\releases\AppDash-<version>.apk` + `latest.json` to a new GitHub release.

`AppDash.exe --selftest` runs the Windows logic checks; `android\gradlew.bat -p android testReleaseUnitTest` runs the Android ones.
