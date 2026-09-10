# Integrated two-way audio

Pico Bridge can send headset microphone audio to the robot speaker and play robot
microphone audio in the headset while tracking stays in the same foreground XR
application. Do not launch the separate `com.example.picoaudiobridge` app during
this workflow.

1. Install the integrated APK using `adb install -r pico-bridge-audio.apk`. Keep
   the existing package `com.picobridge.app` and project signing key; this allows
   an update without clearing application data. Do not uninstall to work around
   a signing mismatch; build with the matching key instead.
2. Start Teleopit on the robot computer with `input=pico4 audio.enabled=true`,
   retaining the hardware options of your working deployment. Its native audio
   helpers must have been built using `scripts/setup/setup_audio_bridge.py`.
   Stop and disable the old robot `pico-to-g1.service` and `robot-to-pico.service`
   before enabling this worker.
3. Open Pico Bridge and connect to Teleopit. The audio destination reuses this
   connection's server address; there is no second IP setting.
4. Select **Start audio** in the panel and grant microphone permission.
   **TX** counts sent microphone packets; **RX** counts received robot packets.
   An increasing TX alone does not prove the robot speaker is playing audio.
5. **Mute mic** sends silence while robot audio continues. **Stop audio** releases
   the microphone, speaker, and UDP sockets. Retry an audio error with
   **Stop audio → Start audio**.

Audio defaults to off on each launch. Pausing or leaving the XR app stops audio;
resuming restarts it when audio was enabled and tracking is connected. Losing
the motion connection also stops audio and reconnecting restores it if enabled.
Keeping Pico Bridge in the foreground avoids switching between two XR apps.

The wire format is 16 kHz mono PCM16LE, 320 bytes per 10 ms. Headset microphone
packets go to UDP 50001 on the Teleopit host. Robot microphone packets arrive at
UDP 50002 on the headset. Receive packets must come from the connected host.
Audio I/O runs on native worker threads with a bounded playback queue; the Unity
tracking loop only manages state. The existing motion and video protocols are
unchanged. Audio is optional and errors are displayed in its own status line.

## Development and validation

`Assets/Plugins/Android/PicoAudioService.java` adapts the provided
`pico_audio_bridge` PCM service using platform Java APIs. Runtime consent and
lifecycle are managed by `PicoAudioController`. The microphone/media-playback
foreground service and permissions are declared in the Android manifest.
Service initialization/cleanup are serialized across rapid stop/start calls.
Audio foreground-service errors are caught so they cannot crash tracking.

The panel's audio buttons and references are serialized by
`PicoBridgeSceneUiTemplate`, not created at runtime. Rebuild the panel and APK via
`PicoBridge.Editor.PicoBridgeStereoBuild.RebuildPrefabAndBuildApk` in the pinned
Unity editor, or use `build-apk.bat` as described in [Build & Install](build-and-install.md).

Run `PicoBridge.Editor.PicoBridgeAudioSmoke.Run` in Unity batch mode to verify the
serialized audio controls and real TCP idle/EOF handling. Receive timeouts
(`TimedOut` and Android's `WouldBlock`) are transient; EOF and other socket errors
still close the connection. Editor checks do not replace headset testing: verify
tracking, both audio directions, mute, stop/start, sleep/resume, reconnect, and
camera preview on the target device before a motion-enabled robot trial.

Platform references: [Unity Java source plugins](https://docs.unity.cn/Manual/android-java-and-kotlin-plugins-create.html)
and [Android foreground service declarations](https://developer.android.com/develop/background-work/services/fgs/declare).
