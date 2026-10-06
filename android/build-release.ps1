# Builds a signed AppDash APK into .\releases and writes latest.json, the feed installed copies check.
#
#   .\build-release.ps1 -Version 1.0.1 -Feed https://example.com/appdash/android/latest.json
#
# Upload releases\AppDash-<version>.apk and releases\latest.json to wherever -Feed points.
# The first run creates appdash-release.jks + keystore.properties. BACK THEM UP: Android only installs an
# update signed with the same key, so losing the key means users must uninstall to get new versions.
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [string]$Feed = '',
    [string]$JavaHome = 'C:\dlm-tools\jdk-17.0.20.1+1'   # Gradle 8.9 needs JDK 17-22; Android Studio's bundled JDK 25 is too new
)
# Native tools (keytool, Gradle) log progress on stderr, which Windows PowerShell 5.1 treats as a terminating
# error under 'Stop'. Judge them by exit code instead; file steps below use -ErrorAction Stop.
$ErrorActionPreference = 'Continue'
$root = $PSScriptRoot
$env:JAVA_HOME = $JavaHome

$props = Join-Path $root 'keystore.properties'
if (-not (Test-Path $props)) {
    # A key without its properties file is a leftover from an interrupted first run; nothing was signed with it.
    Remove-Item "$root\appdash-release.jks" -ErrorAction SilentlyContinue
    $pass = -join ((48..57) + (65..90) + (97..122) | Get-Random -Count 32 | ForEach-Object { [char]$_ })
    & "$JavaHome\bin\keytool.exe" -genkeypair -keystore "$root\appdash-release.jks" -alias appdash -keyalg RSA -keysize 4096 `
        -validity 10000 -storepass $pass -keypass $pass -dname 'CN=AppDash' 2>&1 | Out-Null
    if ($LASTEXITCODE) { throw "keytool failed ($LASTEXITCODE)" }
    [IO.File]::WriteAllText($props, "storeFile=appdash-release.jks`nstorePassword=$pass`nkeyAlias=appdash`nkeyPassword=$pass`n")
    Write-Host "Created signing key appdash-release.jks. Back it up together with keystore.properties."
}

& "$root\gradlew.bat" -p $root --console=plain testReleaseUnitTest assembleRelease "-PappVersion=$Version" "-PupdateFeed=$Feed"
if ($LASTEXITCODE) { throw "Gradle build failed ($LASTEXITCODE)" }

$out = Join-Path $root 'releases'
New-Item -ItemType Directory -Force $out -ErrorAction Stop | Out-Null
$apk = "AppDash-$Version.apk"
Copy-Item "$root\app\build\outputs\apk\release\app-release.apk" "$out\$apk" -Force -ErrorAction Stop

$v = $Version.Split('.') | ForEach-Object { [int]$_ }
$latest = [ordered]@{
    versionCode = $v[0] * 10000 + $v[1] * 100 + $v[2]   # same formula as app/build.gradle.kts
    versionName = $Version
    apk         = $apk                                  # relative to latest.json
    sha256      = (Get-FileHash "$out\$apk" -Algorithm SHA256).Hash.ToLower()
    size        = (Get-Item "$out\$apk").Length
}
[IO.File]::WriteAllText("$out\latest.json", ($latest | ConvertTo-Json))   # UTF-8 without BOM: org.json rejects a BOM
Get-ChildItem $out | ForEach-Object { '{0,-28} {1,8:N0} KB' -f $_.Name, ($_.Length / 1KB) }
