# 编译与安装（任意电脑）

目标：在团队任何一台 Windows 电脑上，从 clone 仓库到把 APK 装进头显，全流程不需要本机之外的分发物（U 盘、私有密钥等）。所有机器构建出的 APK 使用**同一签名**（`keystores/picobridge.jks`），可直接 `adb install -r` 覆盖升级已安装版本，不丢头盔上的标定数据。

## 一次性环境准备

1. **Unity Hub**：从 [unity.cn](https://unity.cn) 下载（国内直连，不需要代理）。
2. **许可证**：Hub 里登录 Unity 账号，激活 Personal（免费）许可。
3. **编辑器**：Hub → Installs → Install Editor → 选 **2022.3.62f3c1**（版本由 `ProjectSettings/ProjectVersion.txt` 锁定），模块勾选 **Android Build Support**（连同 OpenJDK、Android SDK & NDK Tools 一起装）。

装完这一步你就同时拥有了 `adb`（Unity 自带，路径见下文），不需要再单独下载 Android platform-tools（dl.google.com 国内不可达）。

4. **Clone 仓库**：

```bash
git clone -b feat/stereo-fpv https://github.com/FAST-CERN/pico-bridge.git
```

## 编译

在仓库根目录双击或执行：

```bat
build-apk.bat
```

脚本会按顺序查找 Unity：环境变量 `PICOBRIDGE_UNITY`（可指向任意 Unity.exe）→ `C:\Program Files\Unity\Hub\Editor\2022.3.62f3c1` → 本组工作站路径。产物与日志：

- APK：`Builds\pico-bridge-stereo-fpv.apk`
- 日志：`Builds\build-apk.log`（失败先看这里）

首次构建会导入生成 `Library/`，耗时明显偏长属正常。也可以先用 Unity 打开一次项目再跑脚本。

等效手动命令（路径按机器调整）：

```bash
"<Unity.exe 路径>" -batchmode -quit -projectPath <仓库路径> \
  -executeMethod PicoBridge.Editor.PicoBridgeStereoBuild.RebuildPrefabAndBuildApk \
  -picoBridgeBuildPath <输出.apk> -logFile <日志>
```

## 安装到头显

1. 头显开机，USB 连接电脑（头显系统里 USB 调试需保持开启）。
2. 首次连接新电脑时，**戴上头显**，在弹出的"是否允许此电脑进行 USB 调试"里点允许（每台电脑弹一次）。
3. 安装（`-r` 覆盖升级，签名一致所以不会冲突）：

```bash
"<Unity 安装路径>\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe" install -r Builds\pico-bridge-stereo-fpv.apk
```

## 故障对照

| 症状 | 原因与处理 |
| --- | --- |
| `INSTALL_FAILED_UPDATE_INCOMPATIBLE` / signatures do not match | 装的 APK 不是本仓库批量构建产物（或来自其他签名的旧构建）。用 `build-apk.bat` 重新构建；确认走的是 `PicoBridgeStereoBuild.RebuildPrefabAndBuildApk` 入口——它统一用仓库密钥签名，Unity 编辑器手动 Build 不保证。 |
| `adb devices` 显示 `unauthorized` | 戴上头显点掉授权弹窗。 |
| `adb devices` 列表为空 | 换 USB 线/口；头显系统里确认 USB 调试开启。 |
| batchmode 报 license 错误 | Unity Hub 未登录或许可未激活，先打开 Hub 登录。 |
| IL2CPP / NDK 报错 | 安装编辑器时没勾全 Android Build Support（含 OpenJDK / SDK & NDK），Hub → Installs → Add Modules 补装。 |
| Hub 下载慢 | 用 unity.cn 的国内版 Hub。 |

## 关于签名密钥

`keystores/picobridge.jks` 是团队统一签名密钥，随仓库分发（仓库为公开 fork，密钥即公开——这是实验室工具的既定取舍：它只能用于冒充本应用的更新，不涉及任何服务端凭据）。它是项目最初 debug 构建所用密钥的副本，因此与**所有已部署头显上的存量安装**签名一致，可原地覆盖升级。Android 调试密钥凭据（store/key 口令均为 `android`，别名 `androiddebugkey`）写在 `Assets/Scripts/PicoBridge/Editor/PicoBridgeBuild.cs` 里，构建时自动应用，无需手工配置。
