@echo off
rem Build the pico-bridge Android APK with the shared repo keystore.
rem Docs: docs/zh/build-and-install.md
setlocal

set "REPO=%~dp0"
set "APK_OUT=%REPO%Builds\pico-bridge-stereo-fpv.apk"
set "LOG=%REPO%Builds\build-apk.log"

if not defined PICOBRIDGE_UNITY if exist "C:\Program Files\Unity\Hub\Editor\2022.3.62f3c1\Editor\Unity.exe" set "PICOBRIDGE_UNITY=C:\Program Files\Unity\Hub\Editor\2022.3.62f3c1\Editor\Unity.exe"
if not defined PICOBRIDGE_UNITY if exist "F:\Chufan_Rui\Unity\Hub\Editor\2022.3.62f3c1\Editor\Unity.exe" set "PICOBRIDGE_UNITY=F:\Chufan_Rui\Unity\Hub\Editor\2022.3.62f3c1\Editor\Unity.exe"
if not defined PICOBRIDGE_UNITY (
    echo [error] Unity 2022.3.62f3c1 not found.
    echo         Install it via Unity Hub ^(unity.cn^) with "Android Build Support",
    echo         or set PICOBRIDGE_UNITY to the full path of Unity.exe.
    exit /b 1
)

if not exist "%REPO%keystores\picobridge.jks" (
    echo [error] Missing keystores\picobridge.jks - clone is incomplete.
    exit /b 1
)

echo [build] Unity: %PICOBRIDGE_UNITY%
echo [build] APK  : %APK_OUT%
"%PICOBRIDGE_UNITY%" -batchmode -quit -projectPath "%REPO%." ^
    -executeMethod PicoBridge.Editor.PicoBridgeStereoBuild.RebuildPrefabAndBuildApk ^
    -picoBridgeBuildPath "%APK_OUT%" -logFile "%LOG%"
if errorlevel 1 (
    echo [error] Build failed - see %LOG%
    exit /b 1
)

echo [done] %APK_OUT%
echo [hint] Install to a connected headset:
echo   "%PICOBRIDGE_UNITY%\..\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe" install -r "%APK_OUT%"
