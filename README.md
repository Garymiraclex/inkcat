# 墨团 InkCat

一只住在 macOS 桌面上、跟着你一起打字的狸花猫。原生 Swift，没有第三方依赖，不联网。

*A tabby cat that types along with you on the macOS desktop. Native Swift, no dependencies, no network.*

![待机、打字、捂眼、打盹](docs/preview.png)

## 能做什么

- **打字**：两只手按标准指法分工，爪子落到你按的那个键，键帽亮起、冒出气泡，快捷键显示为组合键
- **鼠标**：跟手移动，左右键和滚轮都有反应
- **神态**：眨眼、甩尾、打盹；打得快会眯眼、冒汗、微微抖动
- **输密码**：系统开启密码保护（安全输入）时捂眼，不显示任何按键
- **隐私模式**（默认开启）：只跟着敲击节奏动，不读取按键，也不需要权限

右键猫或点菜单栏图标可以调大小、锁定位置、切换隐私模式。「设置与演示」里可以逐个预览和开关动作。

## 安装

需要 macOS 13 以上和 Xcode 命令行工具（`xcode-select --install`）。

```bash
git clone https://github.com/Garymiraclex/inkcat.git
cd inkcat
./build.sh
open InkCat.app
```

只在一台 Apple 芯片、macOS 26 的 Mac 上测试过。

## 隐私

| | 隐私模式（默认） | 关闭隐私模式 |
|---|---|---|
| 权限 | 不需要 | 需要「输入监控」 |
| 知道什么 | 什么时候敲、敲了几下 | 具体是哪个键 |
| 屏幕上显示 | 随机落点的敲击 | 按了什么键 |

- **不联网**：开启 App Sandbox，没有网络权限，代码里也没有网络调用。
- **不记录**：按键不写入文件，只用来驱动动画；偏好设置里只有各项设置和窗口位置。
- **隐藏、锁屏、切换用户时**停止感知键盘（锁屏和切换用户没有在真机上验证）。

需要知道的限制：

- 隐私模式下，别人看屏幕仍能看出打字的节奏和大概次数。
- 关闭隐私模式后，按键会直接显示在屏幕上，录屏和投屏时请留意。
- 捂眼依赖系统的安全输入。一般的密码框会触发，ssh 会话、部分终端和编辑器、远程桌面不会。
- 鼠标的移动、点击和滚轮始终会显示，不需要权限。

### 关闭隐私模式

取消勾选「隐私模式」，在系统弹窗里允许，然后重开墨团。

应用用的是本地临时签名，**每次重新编译，系统都会把它当成新程序**，之前的授权随之失效。此时在「系统设置 → 隐私与安全性 → 输入监控」里用「−」删掉旧的墨团，重开，再关闭隐私模式即可。

## 代码结构

| 文件 | 内容 |
|---|---|
| `Sources/main.swift` | 窗口、菜单，以及决定键盘输入从哪来 |
| `Sources/InputMonitor.swift` | 读取按键和鼠标（只读） |
| `Sources/KeyCounter.swift` | 隐私模式：只数系统累计的按键次数 |
| `Sources/CatView.swift` | 猫的图层和动作 |
| `Sources/Keyboard.swift` | 键位、指法和透视坐标 |
| `Sources/Settings.swift`、`Demo.swift` | 设置窗口与动作演示 |
| `art/cut.swift` | 把 `art/frames/` 的整帧图拆成 `Assets/` 里的活动部件 |

重新拆图：

```bash
swift art/cut.swift art/frames Assets
```

## 关于

个人 vibe coding 项目：代码由 Claude Code 编写，原画由 Gemini 生成，部分动作参考了 Gemini 做的另一版，我负责提想法和挑毛病。

灵感来自 [KuroCat](https://github.com/qianyiai/kurocat) 这类桌面打字猫，代码和美术都是独立制作的。

## 许可证

- 代码：[MIT](LICENSE)
- 图片（`Assets/`、`art/frames/`、`docs/`）：[保留所有权利](LICENSE-ART.md)，只允许为编译和个人运行墨团而使用

*Code is MIT. Images are all rights reserved and not covered by MIT.*
