# DshGUI

DeepSeek Harness（`dsh`）的 Windows WPF 启动器：把 `dsh web` 的界面装进一个原生窗口——双击打开、不用浏览器，并补上浏览器做不到的原生能力（系统托盘、完成通知、全局快捷键、开机自启、权限提醒等）。

## 技术栈

| 层 | 技术 |
|---|---|
| 界面 | WPF（.NET 10，`net10.0-windows`） |
| 内嵌内容 | WebView2（Edge 的 Chromium 内核，渲染 dsh 的 Web UI） |
| 架构 | MVVM（`ViewModelBase` + `RelayCommand`，无第三方框架） |
| 运行时依赖 | 系统自带的 WebView2 Runtime（Win10/11 默认有）与 Node.js（运行 dsh 需要）；无其他大体积依赖 |

## 界面

<div align="center">
  <img src="docs/light.png" width="46%" alt="浅色模式" />
  <img src="docs/dark.png" width="46%" alt="深色模式" />
</div>

## 工作原理

1. 启动时检测 dsh 是否已安装（在 PATH 里找 `dsh.cmd`）或已在运行（轮询 `http://127.0.0.1:3080`）。
2. 未装 Node.js/npm → 加载面板提示「下载 Node.js + 重新检测」。
3. 有 Node.js 但未装 dsh → 加载面板内选择 npm 镜像源（官方 / 国内镜像），一键 `npm install -g @deepseek-ai/dsh --registry <源>`，安装日志实时滚动。
4. 已装 → 以子进程拉起 `dsh web`，轮询端口就绪，启动期间面板显示日志与计时。
5. WebView2 打开该地址。
6. WPF 与页面通过 WebView2 的 JS ↔ C# 桥接通信：注入脚本监听 **① agent 运行状态**（`data-state="ongoing"` / `data-running`）、**② 深色主题**（`data-ds-dark-theme`）、**③ 当前会话标题**、**④ 权限审批**（`data-approval-key`）。

## 已实现功能

- **原生窗口外观**：灰色外轮廓、圆角、阴影；`WindowChrome` 保留拖拽/缩放/贴边/双击最大化。
- **加载面板**：启动/安装期间显示状态文字、不确定进度条、已用计时与滚动日志，不再空白等待。
- **安装引导（内嵌，不弹窗）**：未安装时在面板内选择镜像源 → 立即安装 → 日志滚动 → 自动启动。
- **npm 镜像源**：官方源 / 国内镜像（npmmirror）可选，安装、更新、检查更新三处共用。
- **Node.js 前置检测**：未装 Node.js 时提示下载官网 + 重新检测。
- **设置面板（内嵌抽屉）**：右侧抽屉替代弹窗（主题 / 镜像源 / 忙完通知 / 开机自启 / 全局快捷键录制）。
- **系统托盘**：`×` 缩到托盘（后台继续跑）、`─` 最小化到任务栏；托盘右键菜单「打开 / 检查更新 / 退出」。
- **忙完通知**：agent 空闲时弹右下角 Toast（**仅最小化/托盘时**），带会话标题，点击回主窗口。
- **权限审批提醒**：dsh 请求权限时，若窗口不在前台则右下角弹**持久** Toast，点击唤起窗口，审批解决后自动消失。
- **深色/浅色主题**（跟随系统/浅色/深色）：壳标题栏 ↔ 网页主题联动（壳为主，网页内手动切换时壳跟随）。
- **全局快捷键**（可录制自定义，默认关）：显示/隐藏窗口。
- **开机自启**：写 `HKCU\...\Run` 注册表项。
- **检查更新**：比对 npm registry 的最新版，一键 `npm install -g`。
- **记住状态**：窗口大小/位置/是否最大化 + 上次主题。
- **窗口置顶**（标题栏图钉）。
- **标题栏实时显示当前会话名**。
- **应用图标 / 托盘图标**（dsh 鲸鱼 logo，多尺寸 `.ico`）。
- **单实例**：二次启动打开已有窗口。

## 使用指南

### 首次启动
1. 双击运行 DshGUI。
2. 程序自动检测 Node.js 与 dsh：
   - 未装 Node.js → 点「下载 Node.js」前往官网装 LTS 版，装完点「重新检测」。
   - 未装 dsh → 在面板里选镜像源（默认官方，网络慢可选国内镜像），点「立即安装」，等日志滚动到「安装完成」后自动进入界面。

### 日常使用
- 关闭按钮 `×` 缩到系统托盘（后台继续运行）；托盘右键菜单可「打开 / 检查更新 / 退出」。
- 标题栏图钉可让窗口始终置顶。
- 当前会话名会实时显示在标题栏。

### 通知与权限
- **忙完通知**：agent 空闲且窗口最小化/在托盘时，右下角弹提示，点击回主窗口。
- **权限审批**：dsh 请求权限且窗口不在前台时，右下角弹持久提示，点击回到窗口处理；批准后提示自动消失。

### 设置
- 点标题栏齿轮打开右侧抽屉，可设置：主题（跟随系统/浅色/深色）、npm 镜像源、忙完通知、开机自启、全局快捷键（点「录制」后按组合键）。

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
    │   └── SettingsViewModel.cs     设置面板（含快捷键录制）
    ├── Views/
    │   ├── MainWindow.xaml(.cs)     无边框壳 + 标题栏 + 加载面板 + WebView2 + Win32 钩子
    │   ├── SettingsView.xaml(.cs)   内嵌设置抽屉
    │   └── ToastWindow.xaml(.cs)    右下角通知
    ├── Services/
    │   ├── DshService.cs            dsh/Node 检测、安装（镜像源+进度）、拉起、端口等待、日志
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

- WPF 自身配置存 `%LOCALAPPDATA%\DshGUI\settings.json`，与 dsh 的 `~/.dsh` 隔离。
- 会话标题、运行状态、主题、权限审批靠读取 dsh 页面的 DOM 信号；dsh 是开发者预览版，若其前端结构变动，这些信号可能失效（不会崩）。
- 全局快捷键默认关闭；录制后即时生效。
- 端口固定 3080，被占用时会提示 + 写日志（暂未做自动换端口）。
- 本项目为非官方项目；DeepSeek 及相关商标归其权利人所有。
