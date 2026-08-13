# DshGUI

DeepSeek Harness（`dsh`）的 Windows WPF启动器：把 `dsh web` 的界面装进一个原生窗口——双击打开、不用浏览器，并补上浏览器做不到的原生能力（系统托盘、完成通知、全局快捷键、开机自启等）。

## 技术栈

| 层 | 技术 |
|---|---|
| 界面 | WPF（.NET 10，`net10.0-windows`） |
| 内嵌内容 | WebView2（Edge 的 Chromium 内核，渲染 dsh 的 Web UI） |
| 架构 | MVVM（ `ViewModelBase` + `RelayCommand`，无第三方框架） |
| 运行时依赖 | 除系统自带的 WebView2 Runtime（Win10/11 默认有）外，无其他大体积依赖 |

## 工作原理

1. 启动时检测本机是否装了 `dsh`（在 PATH 里找 `dsh.cmd`）。
2. 没有 → 弹窗一键 `npm install -g @deepseek-ai/dsh`（未验证）。
3. 有 → 以子进程拉起 `dsh web`（默认 `http://127.0.0.1:3080`），轮询端口就绪。
4. WebView2 打开该地址。
5. WPF与页面之间通过 WebView2 的 JS ↔ C# 桥接通信：注入脚本监听 **① agent 运行状态**（`data-state="ongoing"`）与终端命令运行（`data-running`）、**② 深色主题**（`data-ds-dark-theme`）、**③ 当前会话标题**。

## 已实现功能

- **无边框窗口**：`WindowChrome` 保留拖拽/缩放/贴边/双击最大化。
- **系统托盘**：`×` 缩到托盘（后台继续跑）、`─` 最小化到任务栏；托盘右键菜单「打开 / 检查更新 / 退出」。
- **忙完通知**：agent 空闲时弹右下角 Toast（**仅最小化/托盘时**），带会话标题，点击回主窗口。
- **深色/浅色主题**（跟随系统/浅色/深色）：壳标题栏 ↔ 网页主题联动（壳为主，网页内手动切换时壳跟随）。
- **全局快捷键**（可录制自定义，默认关）：显示/隐藏窗口。
- **开机自启**：写 `HKCU\...\Run` 注册表项。（未验证）
- **检查更新**：比对 npm registry 的最新版，一键 `npm install -g`。（未验证）
- **记住状态**：窗口大小/位置/是否最大化 + 上次主题。
- **窗口置顶**（标题栏图钉）。
- **标题栏实时显示当前会话名**。
- **应用图标 / 托盘图标**（dsh 鲸鱼 logo，多尺寸 `.ico`）。
- **单实例**：二次启动打开已有窗口。

## 目录结构

```
DshGUI/
├── DshGUI.slnx
└── DshGUI/
    ├── DshGUI.csproj
    ├── App.xaml / App.xaml.cs       入口、单实例、托盘、退出
    ├── GlobalAliases.cs             消解 WinForms 与 WPF 的同名类型
    ├── Assets/App.ico               应用图标（由 dsh 的 favicon.svg 生成）
    ├── Models/AppSettings.cs        持久化设置模型
    ├── ViewModels/
    │   ├── ViewModelBase.cs         INotifyPropertyChanged 基类
    │   ├── MainViewModel.cs         主窗口状态
    │   └── SettingsViewModel.cs     设置弹窗（含快捷键录制）
    ├── Views/
    │   ├── MainWindow.xaml(.cs)     无边框壳 + 标题栏 + WebView2 + Win32 钩子
    │   ├── SettingsWindow.xaml(.cs) 设置弹窗
    │   └── ToastWindow.xaml(.cs)    右下角通知
    ├── Services/
    │   ├── DshService.cs            dsh 检测/安装/拉起/端口等待/日志
    │   ├── ThemeService.cs          主题解析 + WebView2 联动
    │   ├── TrayService.cs           托盘图标 + 右键菜单
    │   ├── NotificationService.cs   通知队列（右下角纵向堆叠）
    │   ├── UpdateService.cs         npm 版本检查（System.Version，忽略预发布后缀）
    │   ├── AutoStartService.cs      开机自启（注册表）
    │   ├── SingleInstanceService.cs 单实例（Mutex + 事件聚焦）
    │   └── SettingsService.cs       %LOCALAPPDATA%\DshGUI\settings.json 读写
    └── Infrastructure/
        ├── RelayCommand.cs          ICommand 实现
        └── IconHelper.cs            从 exe 提取图标
```

## 构建与运行

前置：.NET 10 SDK。

```powershell
cd DshGUI\DshGUI
dotnet build
dotnet run
```

## 说明与已知边界

- WPF自身配置存 `%LOCALAPPDATA%\DshGUI\settings.json`，与 dsh 的 `~/.dsh` 隔离。
- 会话标题、运行状态、主题靠读取 dsh 页面的 DOM 信号；dsh 是开发者预览版，若其前端结构变动，这些信号可能失效（不会崩）。
- 全局快捷键默认关闭；录制后即时生效。
- 端口固定 3080，被占用时会提示 + 写日志（暂未做自动换端口；未验证）。
- 本项目为非官方项目；DeepSeek 及相关商标归其权利人所有。
