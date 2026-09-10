# 集成双向音频

Pico Bridge 可将头显麦克风声音发送到机器人扬声器，并在头显播放机器人麦克风
声音，同时动作追踪始终在同一个前台 XR 应用中运行。此流程中不要启动独立的
`com.example.picoaudiobridge` 应用。

1. 使用 `adb install -r pico-bridge-audio.apk` 安装合并版 APK。保留原包名
   `com.picobridge.app` 和项目签名密钥，即可在更新时保留应用数据。遇到签名
   不匹配时应使用匹配密钥重新构建，不要通过卸载绕过。
2. 在机器人电脑上以 `input=pico4 audio.enabled=true` 启动 Teleopit，并保留
   已验证部署中的硬件参数。先用 `scripts/setup/setup_audio_bridge.py` 构建
   原生音频程序。启用该工作进程之前，停止并禁用原来的机器人端
   `pico-to-g1.service` 和 `robot-to-pico.service`。
3. 打开 Pico Bridge，连接 Teleopit。音频复用该连接的服务器地址，无需配置
   第二个 IP。
4. 在面板选择 **Start audio** 并授予麦克风权限。**TX** 表示已发送的麦克风
   包数，**RX** 表示收到的机器人音频包数。仅 TX 增加不能证明机器人扬声器
   已成功播放声音。
5. **Mute mic** 发送静音，机器人声音仍可回传。**Stop audio** 释放麦克风、
   扬声器及 UDP 端口。音频报错后可通过 **Stop audio → Start audio** 重试。

每次启动应用时音频默认关闭。暂停或离开 XR 应用会停止音频；恢复时，如果此前
音频已开启且动作连接有效，音频会重新启动。动作连接断开也会停止音频，重新
连接后会按先前的开启状态恢复。保持 Pico Bridge 在前台即可避免在两个 XR
应用间切换。

传输格式为 16 kHz 单声道 PCM16LE，每 10 ms 发送 320 字节。头显麦克风发往
Teleopit 主机的 UDP 50001，机器人麦克风发往头显的 UDP 50002。接收包必须
来自当前连接的主机。音频读写在原生工作线程中执行，播放队列有容量上限；
Unity 动作追踪主循环只管理状态。原有动作和视频协议不变。音频是可选功能，
错误显示在独立的音频状态行中。

## 开发和验证

`Assets/Plugins/Android/PicoAudioService.java` 使用平台 Java API 改写了
用户提供的 `pico_audio_bridge` PCM 服务。运行时授权和生命周期由
`PicoAudioController` 管理。Android 清单声明了麦克风/媒体播放前台服务和
相关权限。快速停止、启动时，服务初始化和清理按顺序执行；音频前台服务
启动错误会被捕获，避免导致动作追踪崩溃。

面板音频按钮及引用由 `PicoBridgeSceneUiTemplate` 序列化，不在运行时创建。
使用项目固定的 Unity 编辑器执行
`PicoBridge.Editor.PicoBridgeStereoBuild.RebuildPrefabAndBuildApk` 重建面板和
APK，或按[构建与安装](build-and-install.md)使用 `build-apk.bat`。

在 Unity 批处理模式执行 `PicoBridge.Editor.PicoBridgeAudioSmoke.Run` 可检查
音频控件序列化及真实 TCP 空闲/EOF 行为。接收超时（`TimedOut` 和 Android 的
`WouldBlock`）视为暂时无数据；EOF 和其他套接字错误仍会关闭连接。编辑器检查
不能代替头显测试：在带运动输出的实机测试之前，应在目标设备检查追踪、双向
音频、静音、停止/启动、休眠/唤醒、重连及相机预览。

平台参考：[Unity Java 源码插件](https://docs.unity.cn/Manual/android-java-and-kotlin-plugins-create.html)
和 [Android 前台服务声明](https://developer.android.com/develop/background-work/services/fgs/declare)。
