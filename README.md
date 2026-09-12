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

Reference relationship: the upstream [`Mzying2001/CefFlashBrowser`](https://github.com/Mzying2001/CefFlashBrowser) (MIT) is consulted on GitHub — for the SOL/AMF parser architecture (`CefFlashBrowser.Sol/`) and the status-popup UX. No code is copied across. The only upstream files committed here are four `.sol` test fixtures under `src/FlashBrowser-for-MacOS.Tests/TestData/` (binary data, used to characterise the AMF0/AMF3 parser).

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
  （复刻 Ruffle `pluginPolyfill` 的 `defineProperty` 做法）。
  ⚠️ 2026-09-12 修正认知：4399 真正的检测门不是 plugins 而是 `checkflash()` 轮询
  （见 §TODO P0-1b），plugins 伪造保留（无害、且部分脚本仍读它）。
- 附带一个**只记录不修改**的 `MutationObserver`，当 `ruffle-player` 被移出 DOM 时打印
  `[Ruffle-early] ruffle-player REMOVED ...`，用于定位到底哪个脚本移除 player。

**已验证（node 层，GUI 因环境内存受限无法跑）**：
- 早期脚本 `node --check` 语法通过 ✅
- 行为断言：`navigator.plugins['Shockwave Flash']` truthy、`hasUsableFlash()` 返回 `true` ✅
  （复刻了 4399 `flashopen1.js` 的确切检测逻辑）

**剩余阻塞（2026-09-12 反混淆 `ruffle.js` 0.6.0 后修正了归因）**：
`load(options)` 的实现是 `loadedConfig = {...} → await this.ensureFreshInstance() →
"url" in e ? stream_from(...) : "data" in e && load_data(...)`。**移除发生在 `ensureFreshInstance`
内部，早于 `stream_from` / `load_data` 被调用** —— 证据是实测时间线里 `Ruffle instance destroyed.`
（97ms）出现在 `Loading SWF file` **之前**。`ensureFreshInstance` 内部含
`await new Promise(e => { window.setTimeout(e, 200) })`（音频非 running 时），那 97ms 正落在它里面。

由此得到两条硬约束：

1. `load()` 开头有 `if (this.element.isConnected && !this.isUnusedFallbackObject())` 守卫，
   未连接就直接 `Ignoring attempt to play a disconnected or suspended Ruffle element` 并放弃加载
   ⇒ **不能**「先在游离状态 load、完成后再插入 DOM」。
2. `load_data(data, parameters, swfFileName)` **不接受 `base`**；`base` 是 Ruffle 的 **config 项**，
   经 `fn(instance, loadedConfig)` → `setBaseUrl(loadedConfig.base)` 应用。

**已实施的处置**：把 player 挂到宿主页面不管理的容器 —— 新建 `<html>` 直接子节点
`#__ruffle_stage`（在 `<body>` 之外），按原 `<object>`/`<embed>` 的 `getBoundingClientRect()`
绝对定位（零尺寸时退化为 `position: fixed` 铺满视口）。宿主页面删除游戏容器、甚至清空 `<body>`，
都 detach 不到它，`disconnectedCallback` 因而不触发。**方向 3 已阻断「Flash 检测」这条移除路径，
但「究竟哪个脚本、移除的是哪个节点」仍未在 GUI 实测确认**（MutationObserver 埋点就是为此）。

**仍未验证**：2026-09-12 内存窗口不达标（`Pages free` 4266 页 ≈ 68 MB，低于 ~31k 的约定阈值；
swap free 618 MB < 1 GB），GUI 级验证不可行。本次改动只做到 `node --check` + jsdom 行为验证。

**下一步候选**：
1. 待 GUI 环境内存恢复后跑一次，用 MutationObserver 日志确认 player 是否仍被移除、被谁移除
2. ~~data 路径：自己 fetch 字节 + `load({ data })`，完全脱离 DOM embed~~ —— **已废弃**：移除在
   `ensureFreshInstance` 内，`load_data` 根本走不到；且 69 MB SWF 进 ArrayBuffer 在本机是内存峰值风险
3. 升级 Ruffle：0.6.0 的 DOM 生命周期/多实例修复见 ruffle-rs/ruffle 后续 release
4. 顺带修掉的既有缺陷：`#flashgame1` 有时只是包着 `<embed>` 的容器，原逻辑 `getElementById`
   命中容器后不再回退，`swfUrl` 取到空串 → 加载必然落空。现改为优先选「自身带 `src`/`data`
   （含 `param[name="movie"]`）且匹配 `upload_swf|main.swf`」的元素

**环境限制**：本机空闲内存仅 ~19MB（`Pages free`），CEF 一启动即被 SIGKILL（exit 137，0 行日志），
GUI 级验证当前不可行；方向 3 的 JS 逻辑改用 node 环境验证。

## GUI 观测手段（本机实测，2026-09-12）

本机对 GUI 的默认观测能力**几乎全被权限挡住**，下面这张表是逐条实测结果，别再重复试探：

| 手段 | 结果 |
|---|---|
| `screencapture -x` | ❌ `could not create image from display`（无屏幕录制权限） |
| `osascript` + System Events | ❌ 被拒；**且会在屏幕上留下待处理的 SecurityAgent 授权弹窗** |
| `ps`（含 `ps -p PID`） | ❌ 机器级 `operation not permitted`，**关掉沙箱也一样** |
| `log show` | ❌ `Cannot run while sandboxed`（关掉沙箱标志也一样） |
| `launchctl setenv` | ❌ `Not privileged to set domain environment` |
| `open --env` / `open --args` | ❌ 应用都收不到 |
| CEF 命令行开关 | ❌ **永远无效** —— `CefRuntimeLoader.cs:92` 只把 exe 名传进 `CefMainArgs` |
| `.workbuddy/gui-tools/winlist.swift` | ✅ 枚举屏幕窗口（owner/pid/layer/bounds **不需屏幕录制权限**） |
| 应用内诊断日志（`Diagnostics.cs`） | ✅ `FB_DIAG_LOG=<file>` 或 `--diag-log=<file>` |
| `CefSettings.RemoteDebuggingPort` | ✅ `--debug-port=<n>` / `FB_DEBUG_PORT`；`curl http://127.0.0.1:<n>/json` |

**诊断日志**（默认完全空操作，只有给了路径才写）：记录 `BrowserInitialized` / `AddressChanged` /
`LoadStart` / `LoadEnd` / `LoadError` / `ConsoleMessage` / 控件布局 / `Opened` / 8 秒哨兵。
用法：

```bash
# 直接跑 bundle 内可执行文件（这样 stderr 也能拿到；open 会把输出吞掉）
FB_DIAG_LOG=/tmp/fb.log FB_DEBUG_PORT=9222 \
  ./dist-FlashBrowser-for-MacOS.app/Contents/MacOS/FlashBrowserForMacOS
```

**窗口枚举工具**（源码在 `.workbuddy/gui-tools/winlist.swift`；该目录被 gitignore，**新 clone 不带它** ——
它用 `CGWindowListCopyWindowInfo`，只取 owner/pid/layer/bounds，因此不需要屏幕录制权限）：

```bash
swiftc -O -o /tmp/winlist .workbuddy/gui-tools/winlist.swift
/tmp/winlist flash        # 按 owner 名过滤
```

> ⚠️ **启动方式的一条更正**：`.app/Contents/MacOS/FlashBrowserForMacOS` **可以直接跑**
> （只打一条 `Class ExtensionDropdownHandler is implemented in both …` 的 objc 警告，不崩、窗口正常）。
> 只有**不在 bundle 里**的裸 `publish/` 二进制才会撞 Objective-C class duplicate。

---

## TODO

> 未完成事项清单。阻塞中的与可立即推进的分开列，每项都带可观察的完成判据。
>
> ⚠️ **需要 GUI 的任务**（P0 / P3 / P4）执行前先跑 `vm_stat | head -6` 与 `sysctl vm.swapusage`（`Pages free` ≥ ~31k 且 swap free ≥ ~1GB）—— 本机 swap 常接近耗尽，那是 CEF 被 SIGKILL（exit 137、日志 0 行）的直接原因。
> ⚠️ 但内存**不是**唯一门槛：2026-09-12 实测还出现过**间歇性启动卡死**（约 8 次运行中 1 次，浏览器未初始化、
> 原因未定位，见 P0-0）。GUI 前置检查过了仍要盯诊断日志里的 `browser initialized`。

### 🔴 阻塞中

- [x] **P0-0 · 间歇性启动卡死 —— 根因已由用户确认（2026-09-12）**
  - 现象：约 8 次运行中 1 次，窗口开、控件布局完成，但 `browser initialized` 不触发、页面不加载、
    零 TCP、`/json` 目标列表为空；重开即恢复
  - **根因（用户确认）**：启动浏览器会触发一个需要**输入操作密码**的系统授权弹窗；弹窗未应答时
    CEF 初始化一直等待，表现即「启动卡死」。与全部观测吻合：主线程栈顶是正常的
    `Dispatcher.MainLoop`（在等待、不是死锁）、零 TCP（还没走到网络）、窗口与布局都正常
  - 早先把「8 秒哨兵 `DispatcherTimer.RunOnce` 不触发」解读为 UI 线程被阻塞 —— **该判据已作废**：
    `RunOnce` 返回的 `IDisposable` 未被持有引用时会被 GC，同样不触发；诊断代码已把定时器存进字段
  - 后续（可选、不阻塞）：确认具体是哪个授权项（本机曾观测到 `SecurityAgent` / `universalAccessAuthWarn`
    弹窗残留），评估能否预先授权让弹窗不再出现；代码侧无需改动
- [x] **P0-1 · 解决 `<ruffle-player>` 被 4399 脚本从 DOM 移除**（2026-09-12 **GUI 验证通过**）
  - 根因（2026-09-12 反混淆 `ruffle.js` 0.6.0 后修正）：移除发生在 `load()` 的
    `await ensureFreshInstance()` **内部**，早于 `stream_from` / `load_data` 被调用；
    `ensureFreshInstance` 内含 `await new Promise(setTimeout 200)`，实测 `Ruffle instance destroyed.`
    （97ms）正落在其中
  - ⚠️ **原「自己拉字节 → `load({ data, base })`」首选方案已废弃**（因果不成立）：`load({ data })`
    只把字节提到前面，而 `this.instance` 在 `ensureFreshInstance` 里就已被
    `disconnectedCallback → destroy()` 置 null，`load_data` 根本走不到；且 69 MB SWF 进
    ArrayBuffer 在本机（free ≈ 68 MB）是内存峰值风险
  - ⚠️ 两条硬约束（同为实证）：`load()` 开头 `if (this.element.isConnected && ...)` 守卫
    ⇒ **不能**「游离状态先 load 再插入」；`base` 是 **config 项**（`setBaseUrl(loadedConfig.base)`），
    **不是** `load_data` 的参数
  - 处置：**把 player 挂到宿主页面不管理的容器** —— 新建 `<html>` 直接子节点 `#__ruffle_stage`
    （`<body>` 之外），按原宿主的 `getBoundingClientRect()` 绝对定位，零尺寸时退化为 `fixed`
  - 顺带修掉的既有缺陷：`#flashgame1` 有时只是包着 `<embed>` 的容器，原逻辑命中容器后不再回退，
    `swfUrl` 取到空串 → 加载必然落空。现优先选「自带 `src`/`data`（含 `param[name="movie"]`）
    且匹配 `upload_swf|main.swf`」的元素
  - **GUI 验证（2026-09-12，DevTools 协议轮询 + 诊断日志，游戏 `205551_4.htm`）**：
    - CDP `Runtime.evaluate` 每 8s 采样、持续 70s 共 10 次，全部稳定：
      `#__ruffle_stage` `stageExists=true, stageConnected=true, stageInBody=false`；
      `ruffle-player` `playerConnected=true, playerSize=[800,600]`（挂 `<html>`、`<body>` 之外，
      全程 `isConnected`）
    - 诊断日志：`browser initialized` → 页面 HTTP 200 → `New Ruffle instance created (0.6.0,
      wgpu-webgl)` → `Loading SWF file swfproxy://...main.swf`；**全程 0 次
      `Ruffle instance destroyed`**；早期注入生效（`[Ruffle-early] navigator.plugins spoofed`）
  - [x] **P0-1b · Ruffle 内部 URLLoader 的子资源 CORS**（2026-09-12 修复并 GUI 验证）
    - 根因：主 SWF 的 URL 在注入时已改写成 `swfproxy`，但游戏运行期 Ruffle 内部 URLLoader 拉的
      4399 平台资源（`flash_ctrl_version.xml`、`cdn.comment.4399pk.com/control/*.swf|gif` 广告/控制件、
      `flash_flow/log.js` 等统计端点）走浏览器原生 fetch，这些宿主不发 ACAO → `FetchError`
    - 修复 ①：早期注入包装 `window.fetch` —— 跨域 http(s) **GET** 且 `credentials !== "include"`
      的请求重写为 `swfproxy://app/load?u=...`（Ruffle 的 wasm-bindgen 每次调用按属性查找
      `window.fetch`，运行时补包对 WASM 内部发起的 fetch 同样生效；凭据类请求不改写，
      避免丢浏览器 cookie；同源/POST/`no-cors` 均不动）
    - 修复 ②：`SwfProxySchemeHandlerFactory` 的 Content-Type 改为**透传上游值**
      （原来硬编码 SWF MIME，服务不了 XML/GIF）
    - **顺带定位并修掉 showBlockFlash 竞态（这是此前「有时起不来」的真机制）**：
      `flashopen1.js` 并不查 `navigator.plugins` —— 它 `document.write` 一个 10×10 的
      `objtest.swf` embed（`testplayer1`）后**每 100ms 调 `checkflash()`**（真 Flash 插件挂在
      embed 上的方法），连续 5 次 TypeError 就用 `blockflashtip.html` iframe 整体换掉 `#swfdiv`。
      我们的 LoadEnd bootstrap 能否抢在它之前挂好 player 全看时序（实测直连常赢、应用内导航常输）
      → 早期注入给 embed/object 原型挂 `checkflash() = 1`（真插件总会应答，我们替它应答），
      检测首轮即通过，`showBlockFlash` 从此不可达；bootstrap 另加 12×500ms 重试兜底
    - GUI 验证（走应用内导航路径，即此前必败场景）：`ruffle-player` 建立且 `isConnected`、
      `#__ruffle_stage` 在位、`#swfdiv` 无提示 iframe、日志 **10+ 条 `proxied fetch`**（含此前必挂的
      `ctrl_mo_v5.swf` / `flash_ctrl_version.xml`）、**全程 0 条 `FetchError`**
    - 已知瑕疵（不影响玩法，暂不处理）：游戏统计上报的 `hosturl` / `playurl` 参数里带的是
      `swfproxy://app/load?u=...` 形态的地址 —— 代理改写泄漏进了上报字段

### 🟢 可立即推进（纯 C#，不依赖 GUI）

- [x] **P1.0 · 测试工程**（2026-09-12 完成）
  - `src/FlashBrowser-for-MacOS.Tests/`（xunit，`net8.0`，AssemblyName / RootNamespace = `FlashBrowserForMacOS.Tests`）已加入 `.sln`，并已 `ProjectReference` App 工程 —— 实测可行，App 的 `SelfContained` + 固定 RID 不阻碍被测试工程引用
  - 4 个上游 fixture 已 **vendor** 到 `Tests/TestData/`（MIT，见 §Acknowledgements）；`None Update` + `CopyToOutputDirectory` 让测试从 `AppContext.BaseDirectory` 读，不依赖机器上的外部路径
  - 完成判据：`dotnet test` → **6 passed**；`dotnet build -c Release` → **0 warn 0 err**
  - fixture 实测：`settings.sol` 845 B **AMF0**；`FBCookie.sol` 269 B / `pvz.sol` 95 B / `mao.sol` 34 B = **AMF3**
- [ ] **P1 · SOL/AMF 存档解析与编辑**（差异化 #1）
  - 子步骤：~~P1.1 AMF0 解析~~ → ~~P1.2 AMF3 解析~~ → ~~P1.3 写回~~ → P1.4 Avalonia 表格视图 + 字段编辑 → P1.5 存档定位（见下「待决策」）
  - 说明：写回（原 P1.3）随两个版本各自的读取器一并做完 —— `SolFile.Write` 按头部版本分派到 AMF0 / AMF3 写出路径，两种格式都已是**字节级 round-trip**
  - [x] **P1.1 · AMF0 解析 + round-trip**（2026-09-12 完成）
    - 新增 `Sol/Amf0Type.cs`、`Sol/Amf0Value.cs`、`Sol/Amf0Reader.cs`、`Sol/Amf0Writer.cs`、`Models/SolFile.cs`
    - 完成判据：`settings.sol` **字节级 round-trip 完全相等**（845 B 逐字节相同）；`dotnet test` → **35 passed**；`dotnet build -c Release` → **0 warn 0 err**
    - 不可解析的标记（`0x04`/`0x0D`/`0x0E`/`0x11`）与非法 UTF-8 一律**显式抛错**，不做静默跳过或替换
  - [x] **P1.2 · AMF3 解析 + round-trip**（2026-09-12 完成）
    - 新增 `Sol/Amf3Type.cs`、`Sol/Amf3Value.cs`、`Sol/Amf3Reader.cs`、`Sol/Amf3Writer.cs`、`Sol/AmfValue.cs`（AMF0 / AMF3 值的公共基类，只为了让 `SolFile` 的属性表能装两种格式；它刻意不含任何共用成员 —— 跨格式取值本身就是错的）
    - `SolFile.Read` / `Write` 按头部版本分派；**值类型与文件版本不符时显式报错**，不产出混格式文件
    - 完成判据：**4 个 fixture 全部字节级 round-trip 相等**（含 `FBCookie.sol` 269 B、`pvz.sol` 95 B、`mao.sol` 34 B）；`dotnet test` → **67 passed**；`dotnet build -c Release` → **0 warn 0 err**
    - **对象与 traits 共用一个 U29**（最容易读错的地方）：`bit0` 对象内联 / `bit1` traits 内联 / `bit2` externalizable / `bit3` dynamic / `bit4+` 密封成员数。实测反证：`pvz.sol` 的 `saveData` 那一位是 `0x0B` = `0b1011`，即「对象内联 + traits 内联 + 非 externalizable + dynamic + 0 密封成员」，与后面「空类名 + 5 组动态名值对 + 空名结束」完全吻合
    - **三张引用表**（字符串 / 对象 / traits）：值模型保留原文的引用形态（各值的 `ReferenceIndex`），写出时按原样写回 ⇒ 字节还原**不依赖我们猜对 Flash 的去重策略**；引用下标越界显式报错
    - 字符串表**只收非空内联串**（空串是结束标记，规范也说它从不按引用发送）；XML / XMLDocument 走**对象**引用表，且其文本内容不进字符串表 —— 这两条按错一条，后面所有引用下标都会错位
    - externalizable 对象**显式拒绝**：规范说它的载荷没有长度前缀、布局由类自己定义（"a private agreement between client and server"），猜边界就是在毁存档
    - Vector / Dictionary 字段顺序取自规范与 `flash-lso` 实现（**fixture 里没有，属未实测来源**）：Vector 为 `个数 → 定长标志 →（Object 向量还有元素类型名）→ 元素`，Dictionary 为 `条目数 → 弱键标志 → 键值对`
    - 已知边界：读取器接受非最紧凑的 U29，写出器总是写最紧凑形式 ⇒ 对**别的工具写的**非规范 U29 不保证逐字节相同（Flash 自己写的全是紧凑形式，4 个 fixture 已覆盖；已有测试钉住这条行为）
  - 完成判据：解**全部 4 个** fixture 正确 + round-trip 0 误差 + GUI 打开真实 `.sol` 能看见字段（2026-09-12 全部达成，见 P1.4）
  - [x] **P1.4 · Avalonia 表格视图 + 字段编辑**（2026-09-12 完成）
    - 新增 `Sol/SolPropertyRow.cs`（行模型，无 UI 依赖、可单测）、`SolViewerWindow.axaml(.cs)`、
      csproj 加 `Avalonia.Controls.DataGrid` 11.2.7、`App.axaml` 加 DataGrid Fluent 主题
    - 入口：主窗口工具栏「存档」按钮；`--sol=<path>` 启动参数直开（GUI 验证与将来排障用）
    - **保真约束（行模型的核心设计）**：编辑写回是**原地修改**原值对象（`value.Value = …`），
      绝不替换对象 —— AMF3 的 `ReferenceIndex` / 对象引用表挂在原对象上，替换即丢「此处是引用」的事实；
      容器类值（对象/数组/Vector/Dictionary/ByteArray 等）P1.4 一律只读
    - 可编辑类型：Boolean（CheckBox）、String/LongString/XMLDocument/AMF3 String·XML（文本，含带
      `@str(n)` 引用形态的 —— 只改内容、引用下标不动）、Number/Integer/Double/Date·ms（InvariantCulture
      数字，AMF3 Integer 做 29 位范围校验）。解析失败**整次中止保存**并在状态栏列出错误，不静默降级
    - 另存为/打开用 Avalonia `StorageProvider` FilePicker；「重新加载」丢弃未保存修改并明示
    - 完成判据：`dotnet test` → **78 passed**（新增 11 项行模型测试：原地写回、引用保真、范围校验、
      未编辑 fixture 仍字节级 round-trip、编辑后再解析值正确）；GUI 实测 `--sol=…/settings.sol` ——
      诊断日志 `sol viewer: loaded 'settings' format=0x00 properties=23`，`winlist` 确认窗口
      「SOL 存档 — settings.sol」在屏（821×588）。字段级像素验证受本机屏幕录制权限墙限制，无法截图，
      以「窗口在屏 + 数据经窗口代码路径加载」为观测上限
  - **LSO 格式口径（本机 4 个 fixture 逐字节核对）**：
    - 文件头：`00 BF` + `u32` 长度（= 文件总长 − 6）+ `"TCSO" 00 04 00 00 00 00` + `u16` 名字长度 + 名字 + 3 B padding + **1 B 版本（`0x00`=AMF0 / `0x03`=AMF3）**
    - ⚠️ **正文不是标准 AMF 对象**：成员表被剥掉了起止标记，且每个属性后面多一个 `0x00` 分隔字节。换版本只换「名字与值的编码」，分隔规则完全一样：
      - AMF0：`repeat { u16 keyLen | key | amf0 value | 0x00 }`
      - AMF3：`repeat { u29s keyLen+key | amf3 value | 0x00 }`
    - 实测依据：`settings.sol` 首个属性值 `Boolean true` 只占 `01 01`，下一个属性的长度字段在偏移 29 而非 28；文件末尾也是 `Number 0.0` 之后仍有一个 `0x00`。`mao.sol` 正文 = `0B "level" 04 06 00`（`0B` 是 U29S 的长度前缀、`04 06` 是整数 6），同一规则
    - 旁证（第三方实现）：Ruffle 实际使用的 `flash-lso` 把两种版本都解析成 `separated_list0(0x00)` + 尾部 `0x00`
    - **嵌套**在值里的对象走标准 AMF（AMF0 成员表 + `00 00 09`；AMF3 见 `Sol/Amf3Reader.cs` 的注释），没有这个分隔字节
  - ⚠️ 不可写「AMF0 / AMF3 各半」—— 上游 AMF0 只有 1 个 fixture
- [ ] **P2 · 本地 `.swf` 一键打开**（差异化 #2）
  - 子步骤：P2.1 `SwfProxySchemeHandlerFactory` 加 `file://` 分支（+ MIME 判断）→ P2.2 `RuffleInjector.BuildLocalLoadScript()` → P2.3 Avalonia 拖放 + FilePicker → P2.4 macOS Info.plist 关联 `.swf` UTI（**只能改 `bundle-mac.sh`**，它是 Info.plist 的唯一生成者）
  - 完成判据：拖入 `.swf` 5 秒内进游戏画面；双击 `.swf` 默认用本应用打开

### 🟡 待决策（阻塞 P1.5）

- [ ] **P1「存档编辑」的对象需先定 —— 本应用不产生 `.sol` 文件**
  - 事实：内嵌 Ruffle 是 **web/WASM** 构建，存储后端为 `ruffle_web::storage::LocalStorageBackend` → 写浏览器 **localStorage**，**不落 `.sol`**；且 CEF profile 是每次启动唯一的临时目录（`Program.cs:20-22`）、退出即递归删除（`Program.cs:83-86`）⇒ **存档每次退出被清空**
  - 三选一（未定）：
    1. **持久化 CEF profile**（固定 `RootCachePath` + 去掉退出即删），另写 **LevelDB** 读取 —— 上面那套 `.sol` 解析器不适用（存的是 localStorage 键值）
    2. **做导入/导出**（「导出当前存档 → `.sol`」「导入 `.sol` → localStorage」），绕开 LevelDB，但仍依赖方案 1
    3. **只做离线 `.sol` 工具**，不碰本应用运行时 —— 三者中最短路径，且与「P1 不需要 GUI」天然一致
  - 附带陷阱：P0-2 验证时依赖存档的游戏会表现成「首次运行」，别误判成加载失败
- [ ] **LICENSE 选定**（MIT 优先）—— 本地开发不阻塞，但公开发布 / 分发前必须补，否则等于未授权分发

### ⏳ 后续（等内存窗口或 P0 完成后）

- [ ] **P0-2 · 真实可玩验证**（依赖 GUI）
  用例池（URL 均已验 HTTP 200，**SWF 实际可玩性未验证**）：

  | ID | 游戏 | URL | 验证维度 |
  |---|---|---|---|
  | T1 | 死神VS火影3.3 | `/flash/205551_4.htm` | 加载链路（swfproxy + data 路径）|
  | T2 | 全民斗地主 | `/flash/117945.htm` | flashvars 注入 |
  | T3 | 植物大战僵尸 | `/flash/18012.htm` | SharedObject / 存档 |
  | T4 | 爆枪突击 | `/flash/130396.htm` | 键盘事件透传 |
  | T5 | 执行时选定 | — | 异常 / 超时（加载 > 30 s 时给进度反馈）|

  完成判据：T1–T4「进游戏画面 + 操作 30 秒不崩溃」
- [ ] **P3 · 多 tab / 收藏 / 历史**（可延后；多 tab 与 CEF 的进程模型交互复杂）
- [ ] **P4 · 中文 Flash 站点适配层**（候选 7k7k / 3839 / 2144 / u7u7；接入 7k7k 一个站点即算完成）
- [ ] 4399 自身的 Flash 检测弹窗（`blockflashtip.html`）遮蔽 —— 需 `pluginPolyfill` 时机或 DOM 干预
- [ ] 键盘映射 / 虚拟手柄（4399 部分小游戏需要）

### 发布前检查（硬条件）

- [ ] P0-1 佐证 / P0-2 可玩 / P1.0 `dotnet test` 可运行 / P1 round-trip 0 误差 / P2 拖放可玩
- [ ] 措辞诚实性检查：不写「4399 完美兼容」「已支持全站」（1 个用例不外推到全站）；未实测不写具体性能数字；已定位但未闭环的 bug 不写「已修复」；产物身份与声明一致（`plutil -p` 核验 bundle id）

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
# 构建 + 单元测试（仓库根执行）
dotnet restore
dotnet build -c Debug
dotnet test

# 发布 + 打 .app
cd src/FlashBrowser-for-MacOS
dotnet publish -c Release -r osx-arm64 -o ../../publish
cd ../..
./bundle-mac.sh ./publish

# 启动
open ./dist-FlashBrowser-for-MacOS.app
```

> ⚠️ **不要从仓库根目录执行 `dotnet publish -o <dir>`** —— 根目录存在 `.sln`，
> 会触发 `NETSDK1194`（solution-level `--output` 不受支持，且可能把多个工程的产物混到同一目录）。
> 上面的写法先 `cd src/FlashBrowser-for-MacOS`，再用 `-o ../../publish` 把输出指回仓库根。
>
> 若 `dotnet` 不在 PATH：`export PATH="$HOME/.dotnet:$PATH"`（见上方「前置」）。
>
> `open` 需要足够的可用内存。本机在 swap 紧张时 CEF 会被 SIGKILL（exit 137，日志为空）——
> 启动前建议先跑 `vm_stat | head -6` 与 `sysctl vm.swapusage` 确认。

### 已验证产物

本机最近一次构建（2026-09-11）产出的 `dist-FlashBrowser-for-MacOS.app/`（448MB）：

| 路径 | 内容 |
|---|---|
| `Contents/MacOS/FlashBrowserForMacOS` | 启动器（arm64 Mach-O，124KB） |
| `Contents/MacOS/libcef.dylib` | CEF 原生库（176MB），以及 `libAvaloniaNative.dylib`、`libGLESv2.dylib` 等 |
| `Contents/MacOS/Resources/` | CEF 资源（`icudtl.dat` / `v8_context_snapshot.arm64.bin` / `*.pak`，含 CEF 自带的 `Info.plist`） |
| `Contents/MacOS/CefGlueBrowserProcess/` | CEF 子进程（subprocess launcher） |
| `Contents/MacOS/Assets/Ruffle/` | Ruffle 0.6.0 资源（`ruffle.js` + `core.ruffle.*.js` ×2 + `*.wasm` ×2） |
| `Contents/Info.plist` | 标准 macOS bundle manifest（由 `bundle-mac.sh` 生成） |

> ⚠️ **`*.app` 与 `publish/` 都在 `.gitignore` 中 —— 仓库不含二进制产物**。
> 新 clone 后必须自己跑一遍下面的构建链，不能直接 `open`。

**构建身份核对**（改名 / 重建后务必跑一次）：

```bash
plutil -p dist-FlashBrowser-for-MacOS.app/Contents/Info.plist \
  | grep -E 'CFBundleExecutable|CFBundleIdentifier|CFBundleDisplayName'
# CFBundleExecutable   => "FlashBrowserForMacOS"
# CFBundleIdentifier   => "io.github.losseeer.flashbrowserformacos"
# CFBundleDisplayName  => "FlashBrowser for Mac"

# 反例：出现 CefFlashBrowser / com.Mzying2001.* 即说明 bundle 是旧命名的残留产物
ls dist-FlashBrowser-for-MacOS.app/Contents/MacOS/ | grep -i cefFlash   # 应当无输出
```

> `bundle-mac.sh` 会主动检测 publish 目录里残留的其它 apphost 并打印 WARN ——
> `dotnet publish -o <dir>` 是**叠加式**写入，不会清理上一次发布留下的文件。

> ⚠️ **已知警告**：`objc: Class ExtensionDropdownHandler is implemented in both libAvaloniaNative.dylib and libcef.dylib`
> 这是 Avalonia Native 与 CEF 都实现了 `NSToolbar` 的一个 Objective-C 类。
> 已知问题，不影响基本渲染（4399 主页可正常加载），但可能在深交互场景引发偶发崩溃。
> 见 [ask.csdn.net/questions/9021614](https://ask.csdn.net/questions/9021614) 了解完整讨论。

---

## 目录结构

```
FlashBrowser-for-MacOS/
├── FlashBrowser-for-MacOS.sln
├── README.md                                (本文档：项目画像 — 状态/差异/运行/TODO)
├── .gitignore
├── bundle-mac.sh                            (把 publish 输出 → .app bundle)
├── dist-FlashBrowser-for-MacOS.app/         (已构建产物，gitignored)
├── publish/                                 (dotnet publish 输出，gitignored)
└── src/
    ├── FlashBrowser-for-MacOS/
    │   ├── FlashBrowser-for-MacOS.csproj
    │   ├── Program.cs                       (CEF 运行时初始化 + ruffle/swfproxy scheme 注册)
    │   ├── App.axaml / App.axaml.cs
    │   ├── MainWindow.axaml                 (地址栏 + 工具栏 + 浏览器占位)
    │   ├── MainWindow.axaml.cs              (AvaloniaCefBrowser 挂载 + 事件 + Ruffle 注入)
    │   ├── app.manifest
    │   ├── Models/
    │   │   └── SolFile.cs                      (LSO 容器：文件头 + 属性表，按版本分派 Read/Write)
    │   ├── Sol/
    │   │   ├── AmfValue.cs                     (AMF0/AMF3 值的公共基类，仅用于 SolFile 的属性表)
    │   │   ├── Amf0Type.cs                     (AMF0 类型标记枚举)
    │   │   ├── Amf0Value.cs                    (值模型：一型一类，保真用)
    │   │   ├── Amf0Reader.cs                   (AMF0 读取，不可解析标记一律显式报错)
    │   │   ├── Amf0Writer.cs                   (AMF0 写出，大端)
    │   │   ├── Amf3Type.cs                     (AMF3 类型标记枚举)
    │   │   ├── Amf3Value.cs                    (值模型：保留字符串/对象/traits 的引用形态)
    │   │   ├── Amf3Reader.cs                   (AMF3 读取 + 三张引用表 + 深度上限)
    │   │   └── Amf3Writer.cs                   (AMF3 写出，有状态：引用表须全程复用)
    │   ├── Ruffle/
    │   │   ├── RuffleSchemeHandlerFactory.cs   (ruffle://app/ 供给 ruffle.js / wasm)
    │   │   ├── RuffleInjector.cs               (注入 bootstrap JS + 早期注入 + .swf 重写)
    │   │   └── SwfProxySchemeHandlerFactory.cs (swfproxy://app/load?u=... 跨域 SWF 代理)
    │   └── Assets/Ruffle/                      (ruffle.js / core.ruffle.*.js / *.wasm)
    └── FlashBrowser-for-MacOS.Tests/
        ├── FlashBrowser-for-MacOS.Tests.csproj (xunit; AssemblyName/Namespace = FlashBrowserForMacOS.Tests)
        ├── LsoFixtureTests.cs                  (上游 fixture 的表征测试)
        ├── SolFileTests.cs                     (4 个 fixture 解析 + 逐字节 round-trip)
        ├── Amf0RoundTripTests.cs               (合成值覆盖其余 AMF0 类型 + 错误路径)
        ├── Amf3RoundTripTests.cs               (AMF3 全类型 + U29 边界 + 引用表 + 错误路径)
        └── TestData/                           (4 个上游 .sol fixture，vendor 自 MIT 上游)
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
