# CrosshairWin

一个面向 Windows 10 / Windows 11 的屏幕准星（Crosshair）覆盖层程序。
透明、置顶、鼠标穿透、不抢焦点，支持内置样式与自定义图片准星。

**它只是一个普通的桌面覆盖层程序**：不注入任何游戏进程、不读写游戏内存、不绕过反作弊。

---

## 快速开始（免安装）

### 方式 A：自包含版（推荐，无需安装 .NET）

1. 解压 `CrosshairWin-selfcontained-win-x64.zip`
2. 双击 `CrosshairWin.exe`

> 注意：**必须保留同目录下的 6 个 `.dll` 文件**。WPF 的原生库无法打包进单文件，
> 只复制 exe 会无法启动。请整个文件夹一起拷贝。

### 方式 B：精简版（需要 .NET 8 Desktop Runtime）

1. 先安装 [.NET 8 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/8.0)
2. 解压 `CrosshairWin-framework-dependent-win-x64.zip`
3. 双击 `CrosshairWin.exe`

| 版本 | 解压后大小 | 依赖 |
|---|---|---|
| 自包含版 | 约 161 MB | 无 |
| 精简版 | 约 23 MB | .NET 8 Desktop Runtime |

## 系统要求

- Windows 10 x64 1809 (17763) 及以上
- Windows 11 x64 21H2 及以上
- 支持高 DPI、多显示器、多屏不同缩放（PerMonitorV2）

---

## 使用方法

启动后准星默认出现在**主显示器正中心**，程序常驻系统托盘。

### 默认快捷键

| 快捷键 | 功能 |
|---|---|
| `F8` | 显示 / 隐藏准星 |
| `F9` | 循环切换准星样式（含图片预设） |
| `F10` | 打开设置窗口 |
| `Ctrl+Alt+Q` | 退出程序 |

快捷键均可在设置窗口中修改。

### 托盘菜单

右键托盘图标：显示/隐藏准星、切换准星样式、设置、开机自动启动、退出。
双击托盘图标可直接打开设置。

### 准星样式

内置：十字、点、圆、T 形、十字+中心点。
自定义图片：**把图片文件直接拖进设置窗口即可导入**。

支持格式：PNG（推荐，支持透明）、JPG、JPEG、BMP、GIF、TIFF、ICO。

图片可调：缩放方式（按像素宽度 / 按屏幕高度百分比）、旋转 0–360°、透明度、
水平/垂直偏移、保持宽高比、锁定比例、中心锚点、原色显示 / 单色着色。

---

## 文件位置

所有配置与导入的图片都保存在：

```
%AppData%\CrosshairWin\
├── config.json      # 配置（图片只保存相对路径）
├── Images\          # 导入的图片会被复制到这里
└── selftest.log     # 自检输出（执行 --selftest 时生成）
```

导入图片时会自动复制到 `Images\` 并生成唯一文件名，因此**原图移动或删除后不会失效**。
导出配置会把图片一起打包进 `.chwconfig` 文件，导入时自动恢复。

---

## 从源码构建

需要 .NET 8 SDK（或 Visual Studio 2022 17.8+，含「.NET 桌面开发」工作负载）。

```bash
git clone <this-repo>
cd CrosshairWin

dotnet restore
dotnet build -c Release

# 自包含单文件（无需目标机装 .NET）
dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true

# 精简版（体积小，需目标机装 .NET 8 Desktop Runtime）
dotnet publish -c Release -r win-x64 --self-contained false
```

也直接用 Visual Studio 2022 打开 `CrosshairWin.csproj` 编译运行。

## 自检

程序内置一套不依赖 UI 的自检，用于验证配置读写、图片导入/缓存/回退、
配置包导入导出等逻辑：

```bash
CrosshairWin.exe --selftest
```

通过时退出码为 `0`，失败为 `1`，结果同时写入 `%AppData%\CrosshairWin\selftest.log`。

---

## 项目结构

```
CrosshairWin/
├── CrosshairWin.csproj          # net8.0-windows10.0.17763.0 / WPF / x64
├── app.manifest                 # PerMonitorV2 DPI 感知
├── App.xaml(.cs)                # 启动编排、单实例、异常兜底
├── OverlayWindow.xaml(.cs)      # 准星覆盖层窗口
├── SettingsWindow.xaml(.cs)     # 设置窗口
├── Controls/CrosshairCanvas.cs  # 准星绘制控件
├── Dialogs/                     # 取色器、输入框
├── Interop/                     # Win32 P/Invoke、窗口样式
├── Models/AppConfig.cs          # 配置模型
├── Monitors/MonitorService.cs   # 显示器 / DPI 处理
├── Services/                    # 配置、图片、快捷键、托盘、自启、单实例
└── Tests/SelfTest.cs            # 自检
```

## 技术要点

- **透明穿透**：`WindowStyle=None` + `AllowsTransparency=True` + `Background=Transparent`，
  并在 HWND 上追加 `WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`。
- **不抢焦点**：`WS_EX_NOACTIVATE` + `ShowActivated=False`。
- **不出现在任务栏**：`WS_EX_TOOLWINDOW` + `ShowInTaskbar=False`。
- **高 DPI / 多屏**：`app.manifest` 声明 PerMonitorV2；按显示器分别换算物理像素与 DIP。
- **图片性能**：`BitmapImage` 使用 `BitmapCacheOption.OnLoad`（不锁文件）+ `Freeze()`（跨线程安全），
  并按「路径 + 修改时间」缓存。

## 已知限制

- **独占全屏（Exclusive Fullscreen）游戏**中覆盖层通常不可见，这是 Windows 合成器机制的限制，
  非本程序缺陷。请使用「无边框窗口」或「窗口化全屏」模式。
- 动画 GIF 只显示第一帧。如需播放动图需额外引入 `WpfAnimatedGif`。
- 自包含发布不是严格的单一 exe：`CrosshairWin.exe` 之外还有 6 个 WPF 原生 DLL，
  这是 .NET 单文件发布对 WPF 的既有约束。

## 许可证

MIT
