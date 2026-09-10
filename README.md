# FlashBrowser for MacOS

> 在 macOS 12+ 上玩 4399 等站点的 Flash (SWF) 小游戏，无需安装 Adobe Flash Player。
>
> 基于 **Avalonia 11 + .NET 8 + CefGlue.Avalonia.ARM64 120 + Ruffle 0.6.0 (WASM)**。
>
> 设计灵感来自 [Mzying2001/CefFlashBrowser](https://github.com/Mzying2001/CefFlashBrowser)（Windows WPF / CefSharp / Pepper Flash 项目）。**独立从零实现，非 fork。**

## Acknowledgements

This project is **a from-scratch reimplementation**, built independently on **Avalonia 11 + CefGlue + Ruffle WASM**, inspired by [Mzying2001/CefFlashBrowser](https://github.com/Mzying2001/CefFlashBrowser) (Windows WPF + CefSharp + Pepper Flash).

**Not a fork.** Runtime engine differs (Ruffle WASM emulator vs Adobe PPAPI Flash), UI framework differs (.NET 8 + Avalonia 11 vs .NET Framework 4.6.2 + WPF), browser host differs (CefGlue 120 vs CefSharp.WinForms 84). The original CefFlashBrowser is Windows-only and does not merge macOS work.

The name intentionally drops the `Cef` prefix to make the runtime-engine distinction visible — `CefFlashBrowser` advertised "runs Flash via CEF + PPAPI"; this project runs Flash via Ruffle (a Rust→WASM emulator) and uses CEF only as a webview shell.

Reference relationship: the upstream `Mzying2001/CefFlashBrowser` is kept locally as a read-only source for studying the SOL/AMF parser architecture (`CefFlashBrowser.Sol/`) and the status-popup UX. No code is copied across.

---

## 与 Windows 原版的差异

| 维度 | Windows 原版 | macOS 版（本项目） |
|---|---|---|
| UI 框架 | WPF (.NET Framework 4.6.2) | Avalonia 11 (.NET 8) |
| 浏览器控件 | CefSharp.WinForms 84.4.10 | CefGlue.Avalonia.ARM64 120.x |
| Flash 引擎 | Pepper `pepflashplayer.dll` (PPAPI) | Ruffle 0.6.0 (Rust→WASM 仿真) |
| SOL 存档 | C++/CLI wrapper（AMF0/AMF3） | （v2+ 计划，按 PIVOT 报告作为差异化重点） |
| 单实例/IPC | Win32 WM_COPYDATA | （v2+ 计划） |

---

## 当前状态

> 🎯 **Phase 1（脚手架）已达成**：.NET 8 + Avalonia 11 + CefGlue 的完整集成在 macOS（Apple Silicon）上跑通，`bundle-mac.sh` 打包的 `.app` 可加载 https://www.4399.com/，日志中能看到 4399 主页的 `CONSOLE` 输出与 `ptlogin.3304399.net` 的请求。
>
> 🎯 **Phase 2（Ruffle 注入）已接通**：`ruffle://app/` 自定义 scheme 供给 `ruffle.js` + core chunk + WASM（`application/wasm` MIME + CORS）；`MainWindow.OnBrowserLoadEnd` 命中 `4399.com/flash/` 时注入 bootstrap JS；`polyfill()` 已能把 4399 的 `<object>/<embed>` 正确替换为 Ruffle 播放器（v0.6.0，wgpu-webgl renderer），并能提取到正确的 `main.swf` 路径。
>
> 🎯 **Phase 3a（CefGlue 早期注入）已实现**：`JavascriptContextCreated` 时机伪造 `navigator.plugins`，阻断 4399 的 `flashopen1.js` Flash 检测；JS 逻辑经 node 层验证通过（4399 的 `hasUsableFlash()` 返回 true）。GUI 级验证受环境内存限制暂不可行。

### ✅ Phase 1 详细
- .NET 8 + Avalonia 11 LTS，CefGlue.Avalonia.ARM64 120.6099.211（Apple-Silicon native lib，无 Rosetta）
- 单窗口 `MainWindow`（地址栏 + 后退/前进/刷新/主页 + DevTools）
- 状态栏 + 加载状态；默认主页 = `https://www.4399.com/`
- macOS `osx-arm64` self-contained publish 配置
- `bundle-mac.sh` 自动把 publish 输出打成 `.app` bundle

### ✅ Phase 2 详细
- `Ruffle/RuffleSchemeHandlerFactory.cs`：自定义 scheme `ruffle://app/` 供给 ruffle.js / core chunk / wasm，`.wasm` → `application/wasm`
- `Ruffle/RuffleInjector.cs`：`ShouldInject` 命中 `4399.com/flash/`，`BuildInjectionScript` 产生 bootstrap JS
- `Program.cs` 注册 `ruffle` CustomScheme（`IsCorsEnabled=true`、`IsFetchEnabled=true`）
- `MainWindow.OnBrowserLoadEnd` 命中即 `ExecuteJavaScript` 注入
- Ruffle assets 随构建复制（`Assets/Ruffle/**` → `CopyToOutputDirectory`）

### ⚠️ Phase 3（swfproxy + 单实例加载）—— 根因已定位，剩 player DOM 生命周期阻塞

**已实现**：
- `SwfProxySchemeHandlerFactory`：注册 `swfproxy://app/load?u=<url>`，服务端 `HttpClient`
  （带 `Referer: https://www.4399.com/` + 浏览器 UA）下载 SWF 再以
  `Access-Control-Allow-Origin: *` 回吐，解决跨域 CORS/防盗链。
- 注入脚本改为「手动单实例加载」：不调 `ruffle.polyfill()`（它会递归 iframe），
  而是手动 `createPlayer()` + 只加载主游戏 `main.swf`，保留 `pluginPolyfill()` 伪造
  `navigator.plugins`。

**验证证据**（临时把主页指向 `205551_4.htm` 抓日志）：
- `curl` 直连 `sda.4399.com/.../main.swf`（69MB）→ HTTP 200、`application/x-shockwave-flash`、魔数 `CWS` ✅
- 过滤检测 SWF 后，Ruffle 只创建单实例、直接加载 main.swf（不再出现 objtest/cell.swf）✅

**真正的根因（逐层定位，纠正了早期的「多实例」误判）**：
```
New Ruffle instance created          ← e.build() 完成
Ruffle instance destroyed.           ← 97ms 后，player 被从 DOM 移除触发 disconnectedCallback
Loading SWF file ...main.swf         ← load() 继续
Serious error ... reading 'stream_from'  ← this.instance 已 null
```
1. ~~多实例竞争~~（误判）——实为 4399 `flashopen1.js` 注入的 10×10 检测 SWF `objtest.swf`
   （藏在 `#testflashplayer`）干扰 → 已用 `removeTestFlashPlayers()` 过滤。
2. 跨域登录 iframe（`ptlogin.4399.com` 的 Flash 登录框）→ `polyfill()` 递归 iframe 失败销毁实例
   → 已用「手动单实例」绕过。
3. **player 元素在 `load()` 的 `async await ensureFreshInstance()` 间隙被 4399 脚本从 DOM 移除**
   → `disconnectedCallback` → `destroy()` → `this.instance = null` → `stream_from` 报 null。

### ✅ Phase 3a（CefGlue 早期注入）

**实现位置**：`RuffleInjector.BuildEarlyInjectionScript()` + `MainWindow.OnJavascriptContextCreated`。

- `MainWindow` 订阅 `AvaloniaCefBrowser.JavascriptContextCreated`（对应 CEF 渲染进程的
  `OnContextCreated`，**在页面脚本执行前**触发），主 frame 命中 `4399.com/flash/` 即注入早期脚本。
- 早期脚本在 `flashopen1.js` 执行前伪造 `navigator.plugins` / `navigator.mimeTypes`
  （复刻 Ruffle `pluginPolyfill` 的 `defineProperty` 做法），让 4399 的 `hasUsableFlash()`
  返回 `true`，从源头阻断 `showBlockFlash()` DOM 干预。
- 附带一个**只记录不修改**的 `MutationObserver`，当 `ruffle-player` 被移出 DOM 时打印
  `[Ruffle-early] ruffle-player REMOVED ...`，用于定位到底哪个脚本移除 player。

**已验证（node 层，GUI 因环境内存受限无法跑）**：
- 早期脚本 `node --check` 语法通过 ✅
- 行为断言：`navigator.plugins['Shockwave Flash']` truthy、`hasUsableFlash()` 返回 `true` ✅
  （复刻了 4399 `flashopen1.js` 的确切检测逻辑）

**剩余阻塞（未变）**：主游戏 `main.swf`（69MB）加载时 `stream_from` null ——
`load()` 的 `await ensureFreshInstance()` 两个 await 点让出事件循环期间，player 被从 DOM 移除
触发 `disconnectedCallback → destroy() → this.instance = null`。方向 3 已阻断「Flash 检测」这条
移除路径，但**尚未在 GUI 环境验证**是否还有其它脚本移除 player（MutationObserver 就是为此埋点）。

**下一步候选**：
1. 待 GUI 环境内存恢复后跑一次，用 MutationObserver 日志确认 player 是否仍被移除、被谁移除
2. data 路径：自己 fetch 字节 + `load({ data })`，完全脱离 DOM embed（注意补 flashvars + 内存峰值）
3. 升级 Ruffle：0.6.0 的 DOM 生命周期/多实例修复见 ruffle-rs/ruffle 后续 release

**环境限制**：本机空闲内存仅 ~19MB（`Pages free`），CEF 一启动即被 SIGKILL（exit 137，0 行日志），
GUI 级验证当前不可行；方向 3 的 JS 逻辑改用 node 环境验证。

### 📋 其它待办（Phase 3+）
- 4399 自身的 Flash 检测弹窗（`blockflashtip.html`）遮蔽——需 `pluginPolyfill` 时机或 DOM 干预
- 键盘映射 / 虚拟手柄（4399 部分小游戏需要）
- 收藏夹 / 历史 / 多标签
- **直接打开本地 SWF 文件**（跳过 4399 站点集成）—— Report PIVOT 推荐的差异化方向
- **SOL/AMF 存档编辑** —— 移植 `CefFlashBrowser.Sol/` 的 .cpp/.h 思路，与 Flare / Ruffle 桌面端拉开差距（参考 pivot report C8 差异化论点）
- 中文 Flash 站点适配层（sitelock 绕过代理、游戏元数据、本地默认字体替换）

---

## 运行

### 前置

- **macOS 12+**（CEF 120 要求 macOS 11+）
- **.NET 8 SDK**。若未装：
  ```bash
  curl -sSL https://raw.githubusercontent.com/dotnet/install-scripts/main/src/dotnet-install.sh \
    | bash /dev/stdin --channel 8.0 --install-dir ~/.dotnet --no-path
  export PATH="$HOME/.dotnet:$PATH"
  ```

### 命令

```bash
cd src/FlashBrowser-for-MacOS
dotnet restore
dotnet build -c Debug

# 发布 + 打 .app
dotnet publish -c Release -r osx-arm64 -o ../../publish
cd ../..
./bundle-mac.sh ./publish

# 启动
open ./dist-FlashBrowser-for-MacOS.app
```

### 已验证产物

仓库根目录 `dist-FlashBrowser-for-MacOS.app/` 是已构建好的可运行 `.app`：
- `Contents/MacOS/FlashBrowserForMacOS` — 启动器
- `Contents/MacOS/libcef.dylib` (177MB)、`libAvaloniaNative.dylib`、`libGLESv2.dylib` 等
- `Contents/MacOS/Resources/` — CEF 所有资源文件（icudtl.dat / v8 snapshot / pak / Info.plist）
- `Contents/MacOS/CefGlueBrowserProcess/` — CEF 子进程（subprocess launcher）
- `Contents/Info.plist` — 标准 macOS bundle manifest

> ⚠️ **已知警告**：`objc: Class ExtensionDropdownHandler is implemented in both libAvaloniaNative.dylib and libcef.dylib`
> 这是 Avalonia Native 与 CEF 都实现了 `NSToolbar` 的一个 Objective-C 类。
> 已知问题，不影响基本渲染（4399 主页可正常加载），但可能在深交互场景引发偶发崩溃。
> 见 [ask.csdn.net/questions/9021614](https://ask.csdn.net/questions/9021614) 了解完整讨论。

---

## 目录结构

```
FlashBrowser-for-MacOS/
├── FlashBrowser-for-MacOS.sln
├── README.md                                (本文档)
├── .gitignore
├── bundle-mac.sh                            (把 publish 输出 → .app bundle)
├── dist-FlashBrowser-for-MacOS.app/         (已构建产物)
├── assets/
└── src/
    └── FlashBrowser-for-MacOS/
        ├── FlashBrowser-for-MacOS.csproj
        ├── Program.cs                       (CEF 运行时初始化 + ruffle/swfproxy scheme 注册)
        ├── App.axaml / App.axaml.cs
        ├── MainWindow.axaml                 (地址栏 + 工具栏 + 浏览器占位)
        ├── MainWindow.axaml.cs              (AvaloniaCefBrowser 挂载 + 事件 + Ruffle 注入)
        ├── app.manifest
        ├── Ruffle/
        │   ├── RuffleSchemeHandlerFactory.cs   (ruffle://app/ 供给 ruffle.js / wasm)
        │   ├── RuffleInjector.cs               (注入 bootstrap JS + 早期注入 + .swf 重写)
        │   └── SwfProxySchemeHandlerFactory.cs (swfproxy://app/load?u=... 跨域 SWF 代理)
        └── Assets/Ruffle/                      (ruffle.js / core.ruffle.*.js / *.wasm)
```

---

## 实现要点

### macOS 上 CefGlue 的特殊问题

1. **`.app` bundle 必不可少**：直接 `dotnet run`/`./xxx` 跑会因 Objective-C class duplicate 崩溃
   或抛 `IOException`。必须用 `bundle-mac.sh` 把 publish 输出打成 `.app`，然后用 `open` 启动。
   证明：log 里曾出现 `IOException` → 改用 .app 后立即 work。

2. **必须用 `CefGlue.Avalonia.ARM64`**：默认的 `CefGlue.Avalonia` 拉的是 x86_64 dylib，
   在 Apple Silicon 上要么借助 Rosetta、要么干脆报平台冲突。ARM64 版直接拉
   `cef.redist.osx.arm64`（Microsoft 出品的 native lib），全程原生执行。

3. **Avalonia 11 才有 CefGlue 兼容**：CefGlue 120 用的是 Avalonia.ReactiveUI 11.0.9+，
   Avalonia 12 还不兼容。

### 关键代码片段

**Program.cs**：CEF 初始化（每次启动用唯一 cachePath 避免 macOS 多进程冲突）。
```csharp
var cachePath = Path.Combine(Path.GetTempPath(),
    "FlashBrowserForMacOS_" + Guid.NewGuid().ToString("N"));

AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown(cachePath);

AppBuilder.Configure<App>()
    .UsePlatformDetect()
    .AfterSetup(_ => CefRuntimeLoader.Initialize(new CefSettings
    {
        RootCachePath = cachePath,
        WindowlessRenderingEnabled = false
    }))
    .StartWithClassicDesktopLifetime(args);
```

**MainWindow.axaml.cs**：把 `AvaloniaCefBrowser` 挂到 XAML 的 `Decorator` 占位。
```csharp
_browser = new AvaloniaCefBrowser { Address = HomeUrl };
BrowserWrapper.Child = _browser;
```

---

## License

本项目为独立开源项目，**未与上游 Mzying2001/CefFlashBrowser 共享 license**。
当前以 **MIT** 为优先方向（待确认）。
上游项目 license 见 [Mzying2001/CefFlashBrowser](https://github.com/Mzying2001/CefFlashBrowser) 仓库说明。

> 第三方组件 license 摘要：本项目使用 **Ruffle**（MIT/Apache-2.0）、**Avalonia**（MIT）、**CefGlue**（MIT）。完整声明见 `Assets/Ruffle/LICENSE_APACHE`、`LICENSE_MIT` 等随包文件。
