# Build & Install (any machine)

Goal: on any team Windows PC, go from `git clone` to an installed APK with nothing distributed outside the repo (no USB sticks, no private keys). Every machine builds APKs with the **same signature** (`keystores/picobridge.jks`), so `adb install -r` upgrades existing installs in place without wiping on-headset calibration data.

## One-time setup

1. **Unity Hub**: download from [unity.cn](https://unity.cn) (reachable from mainland China without a proxy).
2. **License**: sign in with a Unity account in Hub and activate the free Personal license.
3. **Editor**: Hub → Installs → Install Editor → pick **2022.3.62f3c1** (pinned by `ProjectSettings/ProjectVersion.txt`), with the **Android Build Support** module (include OpenJDK and Android SDK & NDK Tools).

This step also gives you `adb` (bundled with Unity, path below) — no separate Android platform-tools download needed (dl.google.com is unreachable from mainland China).

4. **Clone**:

```bash
git clone -b feat/stereo-fpv https://github.com/FAST-CERN/pico-bridge.git
```

## Build

Run from the repo root:

```bat
build-apk.bat
```

The script resolves Unity in order: `PICOBRIDGE_UNITY` env var (any Unity.exe path) → `C:\Program Files\Unity\Hub\Editor\2022.3.62f3c1` → the team workstation path. Outputs:

- APK: `Builds\pico-bridge-stereo-fpv.apk`
- Log: `Builds\build-apk.log` (check this first on failure)

The first build imports and generates `Library/` and takes noticeably longer. Opening the project once in the Unity editor before building is fine too.

Equivalent manual command (adjust paths):

```bash
"<path to Unity.exe>" -batchmode -quit -projectPath <repo> \
  -executeMethod PicoBridge.Editor.PicoBridgeStereoBuild.RebuildPrefabAndBuildApk \
  -picoBridgeBuildPath <out.apk> -logFile <log>
```

## Install to the headset

1. Power on the headset and connect it over USB (USB debugging stays enabled in the headset system settings).
2. On first connect to a new PC, **wear the headset** and accept the "allow USB debugging from this computer" prompt (once per computer).
3. Install (`-r` upgrades in place; signatures match so it never conflicts):

```bash
"<Unity install>\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe" install -r Builds\pico-bridge-stereo-fpv.apk
```

## Troubleshooting

| Symptom | Cause & fix |
| --- | --- |
| `INSTALL_FAILED_UPDATE_INCOMPATIBLE` / signatures do not match | The APK was not built from this repo's batch entry point (or is an old differently-signed build). Rebuild with `build-apk.bat`; the `PicoBridgeStereoBuild.RebuildPrefabAndBuildApk` entry point applies the shared keystore — a manual editor Build does not. |
| `adb devices` shows `unauthorized` | Wear the headset and accept the authorization prompt. |
| `adb devices` empty | Swap cable/port; confirm USB debugging is on in the headset settings. |
| batchmode license error | Unity Hub is not signed in / license not activated. |
| IL2CPP / NDK errors | Android Build Support was installed without OpenJDK / SDK & NDK. Hub → Installs → Add Modules. |
| Hub downloads are slow | Use the unity.cn Hub build. |

## About the signing key

`keystores/picobridge.jks` is the team-wide signing key, distributed with the repo (the repo is a public fork, so the key is public — an accepted trade-off for lab tooling: it can only impersonate updates of this one app and guards no server-side credentials). It is a copy of the key the original debug builds were signed with, so it matches **every existing deployed install** and upgrades them in place. The standard Android debug credentials (store/key password `android`, alias `androiddebugkey`) are applied automatically by `Assets/Scripts/PicoBridge/Editor/PicoBridgeBuild.cs`; no manual configuration is needed.
