# FlashBrowser-for-MacOS · 开发计划

> 配套文档：根目录 [`README.md`](./README.md) 是项目画像（状态/差异/运行），本文档是路线图（下一步做什么 / 怎么验证 / 何时停）。
>
> 维护原则：每阶段以「可观察的成功判据」收尾；判据没满足就**不进入下一阶段**，哪怕代码看起来能跑。
>
> 文档视角：C 类（portfolio 主导）—— 计划项的可展示性 > 增量覆盖度。

---

## 0. 当前快照（2026-09-10）

| Phase | 状态 | 关键产物 |
|---|---|---|
| Phase 1 · 脚手架 | ✅ | `.app` bundle 可启动，`MainWindow` + 地址栏 + DevTools |
| Phase 2 · Ruffle 注入 | ✅ | `ruffle://app/` scheme + `RuffleInjector.BuildInjectionScript()` |
| Phase 3 · swfproxy + 单实例 | ⚠️ 局部 | `swfproxy://app/load?u=` 跨域代理可用；`main.swf` 加载仍断在 `stream_from null` |
| Phase 3a · 早期注入（导航 Flash 检测）| ✅ JS | `JavascriptContextCreated` + `navigator.plugins` spoof，已绕过 `flashopen1.js` 的 DOM 干预 |
| Phase 3b · Player DOM 生命周期 | ❌ 阻塞 | MutationObserver 已埋点，**环境 OOM 无法 GUI 验证**（见 §6 约束） |
| Phase 4 · SOL/AMF 存档编辑 | ❌ 未开始 | 计划内 |
| Phase 5 · 本地 SWF 一键打开 | ❌ 未开始 | 计划内 |
| 独立 repo + 命名收敛 | ✅ | `bec41be` on `main` (21 files, 1459 insertions)，已 push origin |

**Phase 3b 是当前唯一硬阻塞**。其余都是「按部就班即可推进」。

---

## 1. 路线图总览

| 阶段 | 主题 | 优先级 | 依赖 | 验证手段 | 预计可展示 |
|---|---|---|---|---|---|
| **P0-1** | 解决 Player DOM 移除 | P0（最高） | 内存恢复 / 离线 build | GUI MutationObserver 日志 | 4399 至少 1 个游戏能进游戏画面 |
| **P0-2** | 验证 P0-1 真实可玩 | P0 | P0-1 通 | GUI 实际点开 3 个游戏 | 「点开 4399 Flash 游戏」截图 |
| **P1** | SOL/AMF 存档编辑器 | P1（差异化核心）| 无（纯 C#，不依赖 CEF）| 单元测试 + fixture 比对 | 跨 Ruffle/Flashpoint/原版三方存档兼容 |
| **P2** | 本地 SWF 一键打开 | P1（差异化核心）| Phase 3 路径 | GUI 拖文件 → 播放 | 「拖 SWF 即玩」无站点集成 |
| **P3** | 多 tab / 收藏 / 历史 | P2 | Phase 1 OK | GUI | 体验升级，可选 |
| **P4** | 中文 Flash 站点适配层 | P2 | P0-2 | 接入 7k7k / 3839 | 多站点覆盖 |

**P0-1 / P0-2** 是「项目能不能跑通」的分水岭；
**P1 / P2** 是「项目差异化能不能站住」的关键；
**P3 / P4** 是锦上添花，可延后到下一轮开发周期。

---

## 2. 阶段详述

### P0-1 · 解决 Player DOM 移除（Phase 3b）

**目标**：让 `main.swf` 在 Ruffle 实例存活的前提下真正开始执行（至少进游戏画面，黑屏/卡 loading 算失败）。

**问题（已确认）**：
```
New Ruffle instance created   ← e.build() 完成
Ruffle instance destroyed.    ← ~100ms 后，disconnectedCallback
Serious error ... reading 'stream_from'  ← this.instance 已 null
```
**根因**：Ruffle `load()` 的 `await ensureFreshInstance()` 两次让出事件循环期间，4399 的脚本把 `<ruffle-player>` 从 DOM 移除 → `disconnectedCallback → destroy() → this.instance = null`。

**候选方案**（按推荐度排序）：

| 方案 | 工程做法 | 风险 | 评估 |
|---|---|---|---|
| **A. data 路径** | 自己 `HttpClient` 拉 SWF 字节 → `ruffle.load({ data: Uint8Array, parameters })` | 内存峰值 69MB × 2（下载 + 引擎内） | ✅ **推荐**：彻底脱离 DOM 生命周期，与 Phase 3 swfproxy 共用同一个 fetcher |
| **B. MutationObserver 反制** | 检测到 player 被移出时**重新挂回原容器** | 4399 脚本持续反挂回会进入死循环 | ⚠️ 可作 A 的临时兜底 |
| **C. 升级 Ruffle** | 升 0.7+（0.6.0 已知 DOM 生命周期 bug）| API 变化 / asset 重新打包 | 🔁 长期方案，与 A 并行 |
| **D. 主 frame 拦截** | 注册 `ResourceHandlerFactory` 拦截 `main.swf` 请求，本地回吐 | 与 swfproxy 重复实现 | ❌ 不推荐 |

**关键代码锚点**：
- `src/FlashBrowser-for-MacOS/Ruffle/RuffleInjector.cs:226-229` — 当前 `loadMainGame` 调 `player.load({ url: proxyUrl })`（**A 方案改成 `{ data: bytes }` 即可**）
- `src/FlashBrowser-for-MacOS/Ruffle/SwfProxySchemeHandlerFactory.cs` — 现成的服务端 fetcher，可复用

**成功判据**（**硬性**，三项任一不满足即不进入 P0-2）：
1. GUI 启动 → 地址栏输入 `https://www.4399.com/flash/205551_4.htm` → MutationObserver **不再**打印 `ruffle-player REMOVED` 日志（或只出现一次且已被 load() 处理）
2. console 输出 `Ruffle instance destroyed.` 计数 = 0
3. 至少 1 个 main.swf 加载后进入游戏画面（不一定是 205551，任一可玩即可）

**反例防御（写简历/portfolio 时）**：
- ❌ 不能写：「4399 完美兼容」「所有 Flash 游戏可玩」
- ✅ 可以写：「打通 4399 Flash 游戏加载链路并对 X 个真实站点用例完成 end-to-end GUI 验证」

---

### P0-2 · 真实可玩验证（Phase 3c）

**目标**：在至少 N 个游戏上完成「点击 → 进入游戏画面 → 操作 30 秒不崩溃」端到端验证。

**用例池**（建议先打 5 个，覆盖「吃 · 喝 · 玩 · 乐」+ 一类异常）：

| ID | URL | 类别 | 验证要点 |
|---|---|---|---|
| T1 | `/flash/205551_4.htm` | 玩（横版动作）| 验证 swfproxy + data 路径 |
| T2 | `/flash/<棋牌小游戏>` | 玩 | 验证 flashvars 传递 |
| T3 | `/flash/<模拟经营>` | 玩 | 验证 SharedObject / 存档 |
| T4 | `/flash/<解密类>` | 玩 | 验证键盘事件透传 |
| T5 | `/flash/<loading 超 30s 的>` | 异常 | 验证进度反馈 / 超时 |

**成功判据**：
- T1–T4 全部「进游戏画面 + 30s 不崩溃」
- T5 给出明确的超时行为（不静默卡死）

**面试话术**：「接入真实站点 5 个游戏做端到端验证」—— 这是比「100 个游戏支持」更可信的表述。

---

### P1 · SOL/AMF 存档编辑（差异化 #1）

**目标**：用户能打开 / 编辑 4399 / Ruffle / Flashpoint 三方存档格式互转。

**为什么这是差异化**：
- Ruffle、Flashpoint、Lightspark、NewGrounds Player 都**不提供**图形化存档编辑
- 原版 `Mzying2001/CefFlashBrowser` 的 SOL 解析是 C++/CLI wrapper，与 macOS 不兼容
- 本项目可用**纯 C#** 实现，与 .NET 8 + Avalonia UI 无缝衔接

**参考**：`/Users/dedsecczk/Dev/CefFlashBrowser/CefFlashBrowser.Sol/sol.h`（283 行纯 C++ stdlib，无第三方依赖）—— 思路**可移植**到 C#，但**不复制代码**。

**里程碑**：

| 子阶段 | 内容 | 验证 |
|---|---|---|
| P1.1 | `Models/SolFile.cs` + `Sol/Amf0Reader.cs`（AMF0 解析） | 单元测试：解 5 个 fixture，与 hex dump 一致 |
| P1.2 | `Sol/Amf3Reader.cs`（AMF3 解析） | 单元测试同上 |
| P1.3 | `Sol/SolWriter.cs`（写回） | round-trip：解 → 写 → 解，内容相等 |
| P1.4 | Avalonia UI：表格视图 + 字段编辑 | GUI：打开 .sol 看字段、改字段、保存 |
| P1.5 | Ruffle 存档路径识别 | 自动定位 `~/Library/Application Support/Ruffle/*.sol#sharedobjects` |

**风险点**：
- AMF3 引用对象（`ObjectTraits` / `StringReference`）容易写错——**先做 round-trip 测试再写 UI**
- 大存档（>10MB）需要流式解析，不能一次 `ToArray()`——但 4399 实际存档通常 < 1MB，先按这个假设

**成功判据**：
- ✅ 解 10 个 fixture 全部正确（含 AMF0 / AMF3 各半）
- ✅ round-trip 测试 0 误差
- ✅ GUI 打开一个 4399 真实 .sol 能看见字段

**简历措辞参考**：
> 「独立实现 AMF0 / AMF3 双向解析器（round-trip 0 误差），并搭建 Avalonia 图形化编辑界面，实现 Ruffle / Flashpoint / 原版三方存档兼容」

---

### P2 · 本地 SWF 一键打开（差异化 #2）

**目标**：拖一个 `.swf` 到窗口里 / 双击 `.swf` 用本应用打开 → 立即播放。

**为什么这是差异化**：
- Ruffle 桌面端**有**「Open File」但没有 flashvars 注入
- Flashpoint 是 Windows-only
- 中文 Flash 圈子对「脱离 4399 站点玩小游戏」有刚需（很多老 SWF 已下架）

**实现路径**（与 Phase 3 同源）：
```
拖文件 → swfproxy://app/load?u=file://...&type=local
        ↓
SwfProxySchemeHandlerFactory 已有 fetcher，加 file:// 分支
        ↓
RuffleInjector.BuildLocalLoadScript() → 仅调 player.load({ url: proxyUrl })
        ↓
```

**里程碑**：

| 子阶段 | 内容 | 验证 |
|---|---|---|
| P2.1 | `SwfProxySchemeHandlerFactory` 增加 `file://` 分支（+ mime 判断）| curl 本地 swfproxy:// 拿到字节 |
| P2.2 | `RuffleInjector.BuildLocalLoadScript()`（不带 4399 站点剥离逻辑）| GUI 加载本地 SWF 进游戏画面 |
| P2.3 | `Avalonia` 拖放接收 + FilePicker | 拖一个 1MB SWF 进窗口 → 播放 |
| P2.4 | macOS Info.plist 关联 `.swf` UTI | 双击 .swf 默认打开本应用 |

**成功判据**：
- ✅ 拖一个 `.swf` 进窗口 5 秒内进入游戏画面
- ✅ 双击 `.swf` 文件默认用本应用打开

---

### P3 · 多 tab / 收藏 / 历史（P2，可延后）

**范围**：
- 多 tab（`TabControl` + 每 tab 一个 `AvaloniaCefBrowser`）
- 收藏 = URL + 标题 JSON 持久化（`~/Library/Application Support/FlashBrowser-for-MacOS/bookmarks.json`）
- 历史 = SQLite 记录每次访问

**不推荐优先做**：
- 多 tab 与 CEF 的内存/进程模型交互复杂（每个 tab 一个 render process）
- 收藏/历史是大路货，对 portfolio 叙事增量有限

**何时启动**：P0 + P1 + P2 都完成后，且内存恢复到 GUI 可验证的状态。

---

### P4 · 中文 Flash 站点适配层（P2）

**候选站点**：7k7k.com、3839.com、2144.cn、u7u7.com。

**与 4399 适配的差异**：

| 站点 | 检测机制 | SWF 来源 | 特殊处理 |
|---|---|---|---|
| 7k7k | 自写 `detect.js` | 同站 CDN | flashvars 含 `gameId`，需转 base64 |
| 3839 | `<noscript>` 提示 | 跨域 CDN | iframe 套娃更深 |
| 2144 | 二次跳转 | 跳到 sda.4399.com | 直接复用 4399 路径 |
| u7u7 | 检测相对简单 | 单页 HTML | 几乎零改造 |

**判断标准**：接入 7k7k 一个站点就算 P4 完成；其他站点只是参数扩展，不另立 Phase。

---

## 3. 阶段依赖图

```
Phase 3b (P0-1)
    ↓
P0-2 (可玩验证)
    ↓
    ├──→ P1 (SOL/AMF)        ← 不依赖 P0-2，可并行启动
    ├──→ P2 (本地 SWF)        ← 不依赖 P0-2，可并行启动
    │
    └──→ P3 (多 tab)
            ↓
            P4 (多站点)
```

**关键洞见**：P1 / P2 **不需要** P0-2 验证完成，因为它们与 Ruffle 加载主链路解耦（SOL 是后处理，本地 SWF 是入口前置）。**P1 / P2 可在当前环境（OOM）直接开发并跑单元测试**。

---

## 4. 验证方法学

每阶段必须定义**两类判据**：

### 4.1 硬判据（不满足即阻塞）

| 类型 | 例子 |
|---|---|
| **编译通过** | `dotnet build` 0 warn 0 err |
| **不破坏已有** | 已有用例 T1 仍可玩（regression） |
| **新能力可见** | MutationObserver 日志 / 单元测试 / GUI 截图 |

### 4.2 软判据（达成加分 / 不达成不阻塞）

| 类型 | 例子 |
|---|---|
| 性能 | 启动 < 3s、内存峰值 < 500MB |
| 兼容性 | 跨 5 个 4399 游戏可玩 |
| UX | 错误信息中文化、操作可逆 |

### 4.3 诚实红线（写简历时绝不能踩）

| 红线 | 反例措辞 | 正确措辞 |
|---|---|---|
| 没有 GUI 验证就说「完美兼容」| "完美支持 4399 全站" | "打通 4399 Flash 游戏加载链路，N 个真实用例 GUI 验证通过" |
| 把 ablation / ablation study 套用过来 | "ablation 后提升 X%" | 这是 ablation，不适用；改写「with/without SOL 对照实验」 |
| 把 self_improve 写成已上线 | "Agent 自演进已部署" | "预留自演进扩展点，当前静态" |
| 把「理论占位」写成「已实现」 | "self_improve.py 已上线" | "self_improve.py 为理论占位，生产链路不调用" |

> ⚠️ **诚实红线适用所有阶段**。每完成一阶段写 commit message / README 时先过这道红线条。

---

## 5. 「追问防穿」清单（面试场景预演）

> 这部分给「写完代码后被面试官追问」做预演。每一项都是已知风险点，预先想好答案。

### Q1：「为什么 Ruffle 而不是 NewGrounds Player / Lightspark / 别的 Flash 仿真器？」

A：Ruffle 是**目前唯一活跃维护**的 Flash 仿真器（WASM / Rust，npm 周下载 50k+），其他三个都已停更。NewGrounds Player 内核就是 Ruffle 的早期 fork。Lightspark 在 macOS 上崩溃频繁。

### Q2：「为什么 CefGlue 而不是 WebView2 / WKWebView？」

A：Ruffle **必须**塞进页面的 `<object>` / `<embed>` 位置替换成 `<ruffle-player>`，需要拦截页面脚本（`flashopen1.js` 检测）的早期注入能力。WKWebView 的 WKUserScript 时机晚于页面脚本，挡不住 4399 的 Flash 检测；WebView2 不在 macOS 上。CefGlue 120 是 macOS 唯一能拿到 `OnContextCreated` 时机的方案。

### Q3：「如果 Ruffle 也跑不动某个游戏怎么办？」

A：**诚实回答**——目前还没有通用 fallback。可能的路径是降级到 NewGrounds Player（同内核）或记录该游戏的 SWF 哈希 + 错误模式留待后续 Ruffle 升级。**不要假装这是已解决的问题**。

### Q4：「为什么不用 WebAssembly System Interface (WASI) 直接跑 Ruffle CLI？」

A：Ruffle CLI 没有浏览器 DOM（无 `navigator.plugins` spoof 对象），无法接入 4399 站点集成层。本项目的价值在于「站点集成 + Ruffle」，不是「裸跑 Ruffle」——后者已经有官方 `ruffle_desktop`。

### Q5：「SOL 解析为什么不用现成的 Library？」

A：现成 .NET AMF 库（如 `amf-lib`）上次更新 2019 年、对 AMF3 `ObjectTraits` 引用处理不完整。我们游戏的存档常包含复杂对象引用，必须自己写并加 round-trip 测试。

### Q6：「为什么 Avalonia 11 不是 12？」

A：CefGlue 120 依赖 Avalonia 11 的 `Avalonia.ReactiveUI 11.0.9+`，Avalonia 12 还不兼容。这是已知的第三方控件兼容性问题，等 Avalonia 12 + CefGlue 适配稳定后再升级。

---

## 6. 环境约束与应对

### 6.1 当前硬约束

| 约束 | 数值 | 影响 |
|---|---|---|
| `Pages free` | ~19MB | CEF 启动 SIGKILL（exit 137） |
| 总内存 | 16GB | 当前空闲页框极小 |
| Rosetta | 不可用 | 必须 ARM64 原生 CEF |

### 6.2 应对策略

| 阶段 | 是否需要 GUI | 应对 |
|---|---|---|
| P0-1 | ✅ 是 | 必须等内存恢复 / 借机器 |
| P0-2 | ✅ 是 | 同上 |
| P1 | ❌ 否 | 单元测试即可推进 |
| P2 | 部分 | fetcher 单元测试 + GUI 在最后做 |
| P3 | ✅ 是 | 等 P0-2 完成 |
| P4 | ✅ 是 | 等 P0-2 完成 |

**关键洞见**：P1 / P2 在当前环境**完全可以推进**——纯 C# 单元测试不依赖 CEF。把 P1 / P2 提前启动能在内存恢复前积累 60% 的工作量。

---

## 7. 交付物（每阶段）

| 阶段 | 代码 | 文档 | 可展示 |
|---|---|---|---|
| P0-1 | `MainWindow.axaml.cs` + `RuffleInjector.cs` 修改 | README §Phase 3 补完 | — |
| P0-2 | — | README §可玩游戏清单 + 截图 | 截图 / 录屏 |
| P1 | `Sol/` 全套（6 文件）+ 单元测试 + UI | README §SOL 编辑 + 用户指南 | 「SOL 编辑器」演示 |
| P2 | `SwfProxySchemeHandlerFactory.cs` + Avalonia 拖放 | README §本地 SWF | 「拖 SWF 即玩」录屏 |
| P3 | tab/bookmark/history | README §功能 | — |

---

## 8. 时间预算（粗估，仅供规划参考）

| 阶段 | 人天（单人）| 说明 |
|---|---|---|
| P0-1 | 1–2 | 改 load 路径 + 单元测试 + GUI 验证 |
| P0-2 | 1 | 5 个游戏跑通 |
| P1 | 3–4 | AMF0/3 解析 + round-trip + UI |
| P2 | 1–2 | swfproxy 扩 file:// + Avalonia 拖放 |
| P3 | 2 | tab / bookmark / history |
| P4 | 1 | 7k7k 一个站点 |

**最小可发布版本（MVP）= P0-1 + P0-2 + P1 + P2**，约 6–9 人天。这是 portfolio 叙事最划算的边界。

---

## 9. 关闭条件（如何判断项目「可发布」）

满足**全部**：

- [ ] P0-1：Player 不再被移除的 MutationObserver 日志佐证
- [ ] P0-2：5 个 4399 游戏 end-to-end 可玩
- [ ] P1：SOL 编辑器 GUI 可用 + round-trip 0 误差
- [ ] P2：本地 SWF 拖放可玩
- [ ] README 诚实红线检查通过
- [ ] LICENSE 选定（MIT 优先）
- [ ] CI（可选）：`dotnet build -c Release -r osx-arm64` 0 错误

---

## 附录 A · 关键代码索引

| 模块 | 文件 |
|---|---|
| CEF 初始化 | `src/FlashBrowser-for-MacOS/Program.cs:17-32` |
| 主窗口 | `src/FlashBrowser-for-MacOS/MainWindow.axaml.cs:21-39` |
| 早期注入事件 | `src/FlashBrowser-for-MacOS/MainWindow.axaml.cs:120-133` |
| 注入脚本（后期）| `src/FlashBrowser-for-MacOS/Ruffle/RuffleInjector.cs:133-255` |
| 注入脚本（早期）| `src/FlashBrowser-for-MacOS/Ruffle/RuffleInjector.cs:57-128` |
| SWF 跨域代理 | `src/FlashBrowser-for-MacOS/Ruffle/SwfProxySchemeHandlerFactory.cs` |
| Ruffle 资源供给 | `src/FlashBrowser-for-MacOS/Ruffle/RuffleSchemeHandlerFactory.cs` |
| .app 打包 | `bundle-mac.sh` |
| 已构建产物 | `dist-FlashBrowser-for-MacOS.app/` |

---

## 附录 B · 参考资料

- 上游项目：`/Users/dedsecczk/Dev/CefFlashBrowser/`（fork 已重命名为独立 repo，本仓库不再与上游共享 git 历史）
- PIVOT 战略报告：`/Users/dedsecczk/WorkBuddy/2026-09-10-14-04-22/research/macos-cefflashbrowser-oss-flash-game-browser/report.md`
- 4399 反 Flash 检测：`flashopen1.js` 中 `hasUsableFlash()` 函数（已镜像在 `RuffleInjector.cs:57-128` 的反例）
- CefGlue 120 文档：`CefRuntime.RegisterExtension` 在 CefGlue.Common **未公开**，本项目用 `JavascriptContextCreated` 替代