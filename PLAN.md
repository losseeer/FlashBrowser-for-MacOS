# FlashBrowser-for-MacOS · 开发计划

> 配套文档：根目录 [`README.md`](./README.md) 是项目画像（状态/差异/运行），本文档是路线图（下一步做什么 / 怎么验证 / 何时停）。
>
> 维护原则：每阶段以「可观察的成功判据」收尾；判据没满足就**不进入下一阶段**，哪怕代码看起来能跑。
>
> 文档视角：C 类（portfolio 主导）—— 计划项的可展示性 > 增量覆盖度。

---

## 0. 当前快照（2026-09-11）

| Phase | 状态 | 关键产物 |
|---|---|---|
| Phase 1 · 脚手架 | ✅ | `.app` bundle 可启动，`MainWindow` + 地址栏 + DevTools |
| Phase 2 · Ruffle 注入 | ✅ | `ruffle://app/` scheme + `RuffleInjector.BuildInjectionScript()` |
| Phase 3 · swfproxy + 单实例 | ⚠️ 局部 | `swfproxy://app/load?u=` 跨域代理可用；`main.swf` 加载仍断在 `stream_from null` |
| Phase 3a · 早期注入（导航 Flash 检测）| ✅ JS | `JavascriptContextCreated` + `navigator.plugins` spoof，已绕过 `flashopen1.js` 的 DOM 干预 |
| Phase 3b · Player DOM 生命周期 | ❌ 阻塞 | MutationObserver 已埋点，**环境 OOM 无法 GUI 验证**（见 §6 / §10.3 约束） |
| Phase 4 · SOL/AMF 存档编辑 | ❌ 未开始 | 计划内 |
| Phase 5 · 本地 SWF 一键打开 | ❌ 未开始 | 计划内 |
| 独立 repo + 命名收敛 | ✅ | `bec41be` on `main` (21 files, 1459 insertions)，已 push origin；**2026-09-11 补：构建产物已用当前源码重建，`plutil -p` 核对 bundle id = `io.github.losseeer.flashbrowserformacos`（源码侧与产物侧均已收敛）** |

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
| **A. data 路径** | 自己拉 SWF 字节 → `ruffle.load({ data: <Uint8Array>, base: <原 SWF 所在目录>, parameters: flashvars })` | 拉长 CEF IO 线程阻塞（fetcher 目前是**同步**的，见 `SwfProxySchemeHandlerFactory.cs:63-65,71`）。内存**不是** 69MB×2 —— 该文件 `:71` 已经全量常驻 | ✅ **推荐**：彻底脱离 DOM 生命周期，与 Phase 3 swfproxy 共用同一个 fetcher |
| **B. MutationObserver 反制** | 检测到 player 被移出时**重新挂回原容器** | 4399 脚本持续反挂回会进入死循环 | ⚠️ 可作 A 的临时兜底 |
| **C. 升级 Ruffle** | 升 0.7+（0.6.0 已知 DOM 生命周期 bug）| API 变化 / asset 重新打包 | 🔁 长期方案，与 A 并行 |
| **D. 主 frame 拦截** | 注册 `ResourceHandlerFactory` 拦截 `main.swf` 请求，本地回吐 | 与 swfproxy 重复实现 | ❌ 不推荐 |

**关键代码锚点**：
- `src/FlashBrowser-for-MacOS/Ruffle/RuffleInjector.cs:226-229` — 当前 `loadMainGame` 调 `player.load({ url: proxyUrl })`。**A 方案切 `{ data }` 时两个参数不能丢/必须补**：
  - `parameters`（flashvars）**已在 `:227` 传入**，切换时照搬即可（`extractFlashvars()` 在 `:163-177`，别让它白做）
  - `base` **必须补** —— 不传时 Ruffle 把相对路径的资源按 `swfproxy://app/load` 解析，而 `SwfProxySchemeHandlerFactory` 只认 `?u=` 形态（`:80-96`，`IsAllowed` 仅 `http/https`），外链资源会直接失败
- `src/FlashBrowser-for-MacOS/Ruffle/SwfProxySchemeHandlerFactory.cs` — 现成的服务端 fetcher，可复用（注意它是同步阻塞的，见上表 A 行风险）

**成功判据**（**硬性**，四项任一不满足即不进入 P0-2）：
1. GUI 启动 → 地址栏输入 `https://www.4399.com/flash/205551_4.htm` → MutationObserver **不再**打印 `ruffle-player REMOVED` 日志（或只出现一次且已被 load() 处理）
2. console 输出 `Ruffle instance destroyed.` 计数 = 0
3. 至少 1 个 main.swf 加载后进入游戏画面（不一定是 205551，任一可玩即可）
4. **`base` 生效验证**：改用 `{ data }` 后，该 SWF 若通过相对路径引外链资源，console **无** 指向 `swfproxy://app/` 的解析失败（对照实验：故意不传 `base` 应能复现失败）

**反例防御（写简历/portfolio 时）**：
- ❌ 不能写：「4399 完美兼容」「所有 Flash 游戏可玩」
- ✅ 可以写：「打通 4399 Flash 游戏加载链路并对 X 个真实站点用例完成 end-to-end GUI 验证」

---

### P0-2 · 真实可玩验证（Phase 3c）

**目标**：在至少 N 个游戏上完成「点击 → 进入游戏画面 → 操作 30 秒不崩溃」端到端验证。

**用例池**（建议先打 5 个，覆盖 4 类交互维度 + 1 类异常）：

| ID | 游戏（4399 官方条目）| URL | 验证维度 | 验证要点 |
|---|---|---|---|---|
| T1 | 死神VS火影3.3 | `/flash/205551_4.htm` | 加载链路 | swfproxy + data 路径（横版动作）|
| T2 | 全民斗地主 | `/flash/117945.htm` | flashvars | 页面参数是否正确注入 SWF |
| T3 | 植物大战僵尸 | `/flash/18012.htm` | SharedObject | 本地存档读写（与 Phase 4 衔接）|
| T4 | 爆枪突击 | `/flash/130396.htm` | 键盘事件 | WASD / 方向键是否透传到 Ruffle |
| T5 | 候选，执行时选定 | 见注 ② | 异常 / 超时 | 加载耗时 > 30s 时给进度反馈，不静默卡死 |

> ① **URL 来源**：T1–T4 取自 4399 官方分类页的真实条目（`/flash_fl/{2,4,7,8}_1.htm`），页面可达性已验证（HTTP 200，2026-09-11）。**SWF 实际可玩性未验证** —— 需在 P0-2 用 GUI 实测确认（受 §10.3 swap 约束，只能在 OOM 窗口外执行）。
> ② **T5 不是固定 URL**：其触发条件是「实测加载耗时 > 30s」，执行 P0-2 时从资源量最大的用例（或另选大型 3D 游戏）中选定，并记录其 SWF 体积与实测加载耗时。

**成功判据**：
- T1–T4 全部「进游戏画面 + 30s 不崩溃」
- T5 给出明确的超时行为（不静默卡死）

**面试话术**：「接入 4399 真实站点、5 个用例做端到端 GUI 验证」—— 这是比「100 个游戏支持」更可信的表述。

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
| **P1.0** | **新建 `FlashBrowserForMacOS.Tests` 工程（xunit，`net8.0`）+ 把 4 个上游 fixture 拷进 `Tests/TestData/`** | `dotnet test` 能跑起来 → 从此 `dotnet test` 进入 §4.1 硬判据 |
| P1.1 | `Models/SolFile.cs` + `Sol/Amf0Reader.cs`（AMF0 解析） | 单元测试：解**上游唯一 1 个** AMF0 fixture（`settings.sol`, 845 B）|
| P1.2 | `Sol/Amf3Reader.cs`（AMF3 解析） | 单元测试：解上游 **3 个** AMF3 fixture |
| P1.3 | `Sol/SolWriter.cs`（写回） | round-trip：解 → 写 → 解，内容相等 |
| P1.4 | Avalonia UI：表格视图 + 字段编辑 | GUI：打开 .sol 看字段、改字段、保存 |
| P1.5 | **存档定位（前置调查见下）** | 见「P1.5 前置调查」 |

**为什么必须有 P1.0**：`.sln` 目前只有 1 个工程（`git ls-files` 无 Tests 目录），而 P1 的核心判据是**单元测试 + round-trip** —— 没有测试工程，整条 P1 判据无法执行。

**Fixture 清单（实测，2026-09-11）**：

| fixture | 大小 | 格式 | 来源 |
|---|---|---|---|
| `settings.sol` | 845 B | **AMF0** | `~/Dev/CefFlashBrowser/CefFlashBrowser.Tests/TestData/` |
| `FBCookie.sol` | 269 B | AMF3 | 同上 |
| `pvz.sol` | 95 B | AMF3 | 同上 |
| `mao.sol` | 34 B | AMF3 | 同上 |

> 解析口径：LSO 头 = `00 BF` + `u32` 长度 + `"TCSO" 00 04 00 00 00 00` + `u16` 名字长度 + 名字 + 3 B padding + **1 B 版本（`0x00`=AMF0 / `0x03`=AMF3）**。
> ⚠️ **「AMF0 / AMF3 各半」不可写进判据** —— 上游只有 4 个 fixture、其中 AMF0 仅 1 个。AMF0 的**边界**（长字符串 / ECMA array / Date / 对象引用）若要覆盖，得自造 fixture 或从 Flash Player 6–8 时代的内容导出 —— 那是**额外任务**，不是既有资产。

**风险点**：
- AMF3 引用对象（`ObjectTraits` / `StringReference`）容易写错——**先做 round-trip 测试再写 UI**
- 大存档（>10MB）需要流式解析，不能一次 `ToArray()`——但 4399 实际存档通常 < 1MB，先按这个假设
- **AMF0 覆盖面过窄**（只有 1 个 fixture）→ 别用「AMF0 已完整支持」这类措辞

**成功判据**：
- ✅ 解**全部 4 个**上游 fixture 正确（AMF0×1 + AMF3×3，清单见上）
- ✅ round-trip 测试 0 误差
- ✅ GUI 打开一个真实 .sol 能看见字段（**注意**：见下方「P1.5 前置调查」——「本应用产生的 .sol」并不存在）

**P1.5 前置调查：Ruffle 存档到底在哪（2026-09-11 实测，改变了 P1 的前提）**

**关键结论：本应用内嵌的 Ruffle 不产生任何 `.sol` 文件。**

| 证据 | 位置 |
|---|---|
| 内嵌的是 Ruffle **web/WASM** 构建（不是桌面版）| `src/FlashBrowser-for-MacOS/Assets/Ruffle/core.ruffle.*.wasm` |
| 其存储后端是 `ruffle_web::storage::LocalStorageBackend`（wasm 符号实测）→ 写**浏览器 localStorage**，不是文件 | `strings` 扫 `core.ruffle.*.wasm` 可命中该符号 |
| CEF profile 被设为**每次启动唯一的临时目录** | `Program.cs:20-22`（`Path.GetTempPath()/FlashBrowserForMacOS_<GUID>`）|
| 退出时**递归删除整个 profile** | `Program.cs:24`（注册 `ProcessExit`）→ `Program.cs:83-86`（`Directory.Delete(recursive: true)`）|

⇒ **游戏存档在每次退出时被清空、每次启动都从零开始。** 这不只是 P1 的前提问题，也是 P0-2 的验证陷阱：**依赖存档的游戏会表现成「首次运行」**（例如关卡进度、设置项），别把它误判成加载失败。

因此 P1.5「自动定位」的对象只能是**别家运行时**产生的 `.sol`：

| 来源 | 路径（macOS）| 实测状态 |
|---|---|---|
| Ruffle 桌面版（沙箱/公证构建）| `~/Library/Containers/rs.ruffle.ruffle/Data/Library/Application Support/ruffle/SharedObjects/<domain>/<path>/<name>.sol` | 本机未安装，**未实测** |
| Ruffle 桌面版（非沙箱）| `~/Library/Application Support/ruffle/SharedObjects/<domain>/<path>/<name>.sol` | 本机未安装，**未实测** |
| Flash Player 遗留 | `~/Library/Preferences/Macromedia/Flash Player/#SharedObjects/<domain>/<path>/<name>.sol` | **未实测** |

> **原路径两处都写反了**：`…/Ruffle/*.sol#sharedobjects` —— ① 目录名是小写 `ruffle`，不是 `Ruffle`；② Ruffle 用**真实嵌套目录** `SharedObjects/<domain>/<path>/<name>.sol`，而 `#SharedObjects` 这种 `#` 连接命名是 **Flash Player** 的风格（`…#SharedObjects\<domain>\<path>\<name>.sol`），两者不是一回事。
> 依据：ruffle-rs/ruffle discussions **#15177** / **#3816**（`dirs::data_local_dir` + 沙箱路径示例）。**上表三条路径均需在本机 `ls` 实测后才能写进判据。**

**要真做「编辑本应用存档」，得先改一个前置条件**（三选一，**属设计决策，未定**）：

1. **持久化 CEF profile** —— 把 `RootCachePath` 从 GUID 临时目录改为固定路径（如 `~/Library/Application Support/FlashBrowserForMacOS/cef`），并去掉「退出即删」。之后还要从 CEF 的 localStorage LevelDB 取 SharedObject 条目 —— **需要另写 LevelDB 读取，上面那套 `.sol` 解析器不适用**（存的是 localStorage 键值，不是 `.sol` 文件）。
2. **做导入/导出而不是直接编辑** —— UI 内提供「导出当前存档 → `.sol`」「导入 `.sol` → localStorage」，绕开 LevelDB 格式（但同样依赖 1 的 profile 持久化才有意义）。
3. **只做离线文件工具** —— 把 P1 定位成「独立的 `.sol` 解析 / 编辑 / 互转工具」，不碰本应用运行时。**与 §6.2「P1 不需要 GUI」天然一致，也是三者里最短路径。**

> **顺带修正一处措辞**：Flash Player / Ruffle / Flashpoint **用的是同一个容器格式**（LSO = `.sol` = AMF0/AMF3），差别只在**存储目录布局**与 AMF 版本。所以正确说法是「自动定位并互认各运行时的存档目录」，**不是**「三方格式互转」——后者会被面试官追问「三者格式哪里不同」而穿帮。

**简历措辞参考**：
> 「独立实现 LSO(`.sol`) 容器 + AMF0/AMF3 双向解析器（round-trip 0 误差），并搭建 Avalonia 图形化存档编辑界面；支持 Flash Player / Ruffle / Flashpoint 各运行时存档目录的自动定位与互认」

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
主线（依赖 GUI 验证）:
    Phase 3b (P0-1) ──→ P0-2 (可玩验证) ──→ P3 (多 tab) ──→ P4 (多站点)

并行线（不依赖 P0-2，可立即开工）:
    P1.0 (Tests 工程) ──→ P1 (SOL/AMF 解析 + round-trip) ──┐
    P2 (本地 SWF: file:// 分支 + 拖放) ────────────────────┴──→ MVP 收口 = P0-1+P0-2+P1+P2
```

**关键洞见**：P1 / P2 **不需要** P0-2 验证完成 —— SOL 是后处理、本地 SWF 是入口前置，两者都与加载主链路解耦。
**可开工性**：P1.0 / P1 是**纯 C#**，在当前 OOM 环境下可立即开发并跑 `dotnet test`；P2 的 `file://` 分支也可用 curl 单测 —— 只有 P2.2 / P2.3 的「进游戏画面」「拖放」两步需要 GUI。

---

## 4. 验证方法学

每阶段必须定义**两类判据**：

### 4.1 硬判据（不满足即阻塞）

| 类型 | 例子 |
|---|---|
| **编译通过** | `dotnet build` 0 warn 0 err |
| **测试通过** | `dotnet test` 全绿（**P1.0 引入测试工程后生效**；此前无测试工程，此项为空）|
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
| 用 1 个用例外推到全站 | "已支持全站 Flash 游戏" | 写清实测通过的用例数（T1–T4）与站点，不做外推 |
| 把计划内未开工的 Phase 写成已实现 | "支持 SOL/AMF 存档编辑、三方格式互转" | Phase 4/5 未开工 → 写「规划中」，不写「已支持」 |
| 把已定位但未闭环的 bug 说成已修复 | "已解决 Ruffle 实例被销毁问题" | "已定位根因（DOM 生命周期 + 早期注入），Phase 3b 未闭环" |
| 引用未实测的性能数字 | "启动 < 3s、内存峰值 < 500MB" | 属 §4.2 软判据，未实测前不写具体数值 |
| 把上游产物身份说成自己的 | bundle 里留着 `com.Mzying2001.*` 却宣称「独立实现」 | 交付前 `plutil -p` 核验 bundle id，产物身份必须与声明一致（见 §10.2）|

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

| 约束 | 数值（2026-09-11 实测） | 影响 |
|---|---|---|
| `vm_stat` `Pages free` | **4.3k–27.4k pages（≈68–428MB，分钟级剧烈波动）** | 单看 free pages 会误判；CEF 启动 SIGKILL（exit 137） |
| **`sysctl vm.swapusage` free** | **322–363MB**（total 11.0GB / used ≈10.9GB） | **这才是 SIGKILL(137) 的直接原因** —— swap 也接近耗尽，内核无处换页，日志 0 行 |
| 总内存 | 16GB | 当前空闲页框极小 |
| Rosetta | 不可用 | 必须 ARM64 原生 CEF |

> ⚠️ **判据必须两条一起看**：`Pages free` 与 `swap free`。
> 早期只盯 `Pages free`（记成「~19MB」）导致把「swap 耗尽」误诊为「空闲内存不足」，
> 检查方式也随之写错（只跑 `vm_stat`）。

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
| **P1.0** | `FlashBrowserForMacOS.Tests` 工程（xunit）+ `Tests/TestData/` 4 个 fixture | — | `dotnet test` 输出 |
| P1 | `Sol/` 全套（6 文件）+ UI + **测试工程内的用例** | README §SOL 编辑 + 用户指南 | 「SOL 编辑器」演示 |
| P2 | `SwfProxySchemeHandlerFactory.cs` + Avalonia 拖放 + **`bundle-mac.sh`**（P2.4 的 `.swf` UTI 只能改它 —— 它是 Info.plist 的**唯一生成者**）| README §本地 SWF | 「拖 SWF 即玩」录屏 |
| P3 | tab/bookmark/history | README §功能 | — |

---

## 8. 时间预算（粗估，仅供规划参考）

| 阶段 | 人天（单人）| 说明 |
|---|---|---|
| P0-1 | 1–2 | 改 load 路径（含 `base`）+ 单元测试 + GUI 验证 |
| P0-2 | 1 | 5 个用例跑通 |
| **P1.0** | **0.5** | **建 Tests 工程 + 拷 4 个 fixture（前置，否则 P1 无法验收）** |
| P1 | 3–4 | AMF0/3 解析 + round-trip + UI |
| P2 | 1–2 | swfproxy 扩 file:// + Avalonia 拖放 |
| P3 | 2 | tab / bookmark / history |
| P4 | 1 | 7k7k 一个站点 |

**最小可发布版本（MVP）= P0-1 + P0-2 + P1.0 + P1 + P2**，约 6.5–9.5 人天。这是 portfolio 叙事最划算的边界。

---

## 9. 关闭条件（如何判断项目「可发布」）

**硬条件（全部满足才算「可发布」）**：

- [ ] P0-1：Player 不再被移除的 MutationObserver 日志佐证
- [ ] P0-2：5 个 4399 用例 end-to-end 可玩（清单见 §2 P0-2）
- [ ] P1.0：`dotnet test` 可运行（测试工程已存在）
- [ ] P1：SOL 编辑器 GUI 可用 + `dotnet test` 里 round-trip 0 误差
- [ ] P2：本地 SWF 拖放可玩
- [ ] README 诚实红线检查通过（§4.3）

**软条件（建议，不阻塞本地开发）**：

- [ ] LICENSE 选定（MIT 优先）—— **本地开发不阻塞，但「放上 GitHub Releases / 公开分发」前必须补**；仓库当前**无 LICENSE 文件**，届时等于未授权分发
- [ ] CI：`dotnet build -c Release -r osx-arm64` 0 错误（本地已 0 warn 0 err，CI 只是固化该判据）

---

## 10. 会话交接清单（**新会话读这一段就能开干**）

> 本节是 PLAN.md 的「自包含补丁」——光读 §10 + 附录 A，新会话 agent 也能：
> - 知道在哪台机器、什么路径、什么约束下开工
> - 不重复踩「dotnet 没装 / 路径不对 / CEF OOM」等已知坑
> - 找到上游 SOL 参考、TestData fixture、PIVOT 报告

### 10.1 机器与路径速查

| 项 | 值 |
|---|---|
| 工作目录 | `~/Dev/FlashBrowser-for-MacOS/` |
| 仓库远端 origin | `https://github.com/losseeer/FlashBrowser-for-MacOS.git` ✅（2026-09-10 已切换） |
| 仓库远端 upstream | 已移除（独立 repo 不再保留上游引用）|
| .NET SDK | **`~/.dotnet/dotnet`（SDK 8.0.425）。`/opt/homebrew/bin/dotnet` 不存在；`~/.dotnet` 不在 PATH** → 用前先 `export PATH="$HOME/.dotnet:$PATH"` |
| 已发布 .app | `dist-FlashBrowser-for-MacOS.app/`（**gitignored —— 仓库不含二进制**；2026-09-11 已用当前源码重建，`plutil -p` 核对过 bundle id） |
| publish 输出 | `publish/`（gitignored） |
| 构建产物是否进 git | **否** —— `.gitignore` 忽略 `*.app/` 与 `publish/`。`git ls-files` 仅 22 个文件（源码 + 文档）。**新 clone 必须自己跑 §10.2 才有可执行产物** |
| 上游 SOL 参考 | `~/Dev/CefFlashBrowser/CefFlashBrowser.Sol/sol.h` + `sol.cpp` |
| TestData fixtures | `~/Dev/CefFlashBrowser/CefFlashBrowser.Tests/TestData/*.sol`（实测：4 个） |
| PIVOT 报告 | `~/WorkBuddy/2026-09-10-14-04-22/research/macos-cefflashbrowser-oss-flash-game-browser/report.md` |

### 10.2 启动命令链

```bash
# 0. dotnet 不在 PATH（SDK 8.0.425 装在 ~/.dotnet）—— 每条新 shell 都要先导出
export PATH="$HOME/.dotnet:$PATH"

# 1. 还原依赖 + 编译（在仓库根执行 OK；基线 0 warn 0 err）
cd ~/Dev/FlashBrowser-for-MacOS
dotnet restore
dotnet build -c Release

# 2. 发布到 ./publish/（gitignored）
#    ⚠️ 不要在仓库根跑 publish —— 根目录有 .sln，-o 会触发 NETSDK1194
cd src/FlashBrowser-for-MacOS
dotnet publish -c Release -r osx-arm64 -o ../../publish
cd ~/Dev/FlashBrowser-for-MacOS

# 3. 打 .app bundle（默认输出 ./dist-FlashBrowser-for-MacOS.app，与本文档引用一致）
./bundle-mac.sh ./publish

# 4. 校验 bundle 身份（改名 / 重建后**必跑**）
plutil -p ./dist-FlashBrowser-for-MacOS.app/Contents/Info.plist \
  | grep -E 'CFBundleExecutable|CFBundleIdentifier'
# 期望：CFBundleExecutable => "FlashBrowserForMacOS"
#       CFBundleIdentifier => "io.github.losseeer.flashbrowserformacos"
# 反例：出现 CefFlashBrowser / com.Mzying2001.* → 产物是旧名残留，见 §10.6

# 5. 启动 GUI（先过 §10.3 的内存准入检查）
open ./dist-FlashBrowser-for-MacOS.app

# 6. 看日志（CefGlue 把 console.log 写到 stderr）
./dist-FlashBrowser-for-MacOS.app/Contents/MacOS/FlashBrowserForMacOS 2>&1 | tee run.log
```

**两个已实测的坑**：

- **批量删除保护**：`rm -rf publish` 与 `CLEAN=1 ./bundle-mac.sh` 都会被安全守卫拦截（>50 文件）。
  要清干净就改用可恢复的方式：`mv publish ~/.Trash/publish.$(date +%Y%m%d-%H%M%S)`，再重新 publish。
- **`dotnet publish -o <dir>` 是叠加式写入**，不会清理上一次发布留下的文件。
  `bundle-mac.sh` 已内置「旧 apphost 残留」检测并打印 WARN（见 §10.6）。

### 10.3 环境硬约束（**OOM 警告**）

| 指标 | 数值（2026-09-11 实测） | 影响 |
|---|---|---|
| `vm_stat` `Pages free` | 4.3k–27.4k pages（**≈68–428MB，分钟级剧烈波动**） | CEF 启动 SIGKILL（exit 137，无日志） |
| **`sysctl vm.swapusage` free** | **322–363MB**（total 11.0GB / used ≈10.9GB） | **swap 近耗尽 —— SIGKILL 的直接原因** |
| 总内存 | 16GB | 当前空闲页框极小 |
| Rosetta | 不可用 | 必须 `osx-arm64` |

**应对**：
- **OOM 期间只推进 P1 / P2 的纯 C# 工作**（单元测试不依赖 CEF）
- OOM 期间 GUI 验证一律跳过，所有「GUI 通断」相关的工作改用「node 层 JS 行为断言 + 单元测试」代替
- 任何「GUI 启动」步骤执行前**先跑这两条**，**两条都满足**才动手：

  ```bash
  vm_stat | head -6                 # 要求 Pages free ≥ ~31k（≈500MB）
  sysctl vm.swapusage               # 要求 free ≥ ~1GB
  ```

  > 只满足其一不足以判定。历史误判成因：只跑 `vm_stat` 并把 `Pages free` 记成固定「~19MB」，
  > 于是漏掉了真正卡住 GUI 的 swap 耗尽。`Pages free` 会随其它进程在几十秒内从 428MB 掉到 68MB，
  > **必须在真正执行 `open` 之前的那一次测量才有意义**。

### 10.4 命名约定速查

| 维度 | 值 |
|---|---|
| 文件夹 / repo | `FlashBrowser-for-MacOS` |
| .sln / .csproj | `FlashBrowser-for-MacOS.{sln,csproj}` |
| C# Namespace / Assembly | `FlashBrowserForMacOS`（PascalCase，无横杠） |
| UI Title | `FlashBrowser for Mac` |
| CFBundleExecutable | `FlashBrowserForMacOS` |
| Bundle ID | `io.github.losseeer.flashbrowserformacos`（全小写） |
| Address bar 默认主页 | `https://www.4399.com/` |
| RuntimeIdentifier | 默认 **`osx-arm64`**（`csproj:13`）。`csproj:12` 的 `RuntimeIdentifiers` 另列 `osx-x64` **仅为 future-proof 占位**，发布 / CI 一律 `-r osx-arm64`（Rosetta 不可用，见 §6.1）|

**禁止**：
- 把 `Cef` 加进项目名（运行时不是 CEF Flash Player，是 Ruffle WASM）
- 把 namespace 写成 `FlashBrowser-for-MacOS`（C# 不允许横杠）

### 10.5 关键决策的「为什么」（防被面试官追问打穿）

| 决策 | 答案 |
|---|---|
| 为什么 Ruffle 而不是 NG Player / Lightspark | Ruffle 是唯一活跃维护的 Flash 仿真器（WASM/Rust），其他三个停更 |
| 为什么 CefGlue 而不是 WKWebView / WebView2 | 需要 `OnContextCreated` 时机；WKWebView 的 WKUserScript 时机太晚 |
| 为什么 Avalonia 11 不是 12 | CefGlue 120 依赖 Avalonia.ReactiveUI 11.0.9+ |
| 为什么 .app bundle 必不可少 | 直接 `dotnet run` 因 Objective-C class duplicate 崩溃 |
| 为什么 CefGlue.Avalonia.ARM64 而不是普通版 | 默认版拉 x86_64 dylib，Apple Silicon 上要么 Rosetta 要么报错 |
| 为什么 swfproxy:// 自定义 scheme | sda.4399.com 没 ACAO header，必须服务端代理 + 加 Referer |

### 10.6 已知警告与坑

| 现象 | 原因 | 影响 |
|---|---|---|
| `objc: Class ExtensionDropdownHandler is implemented in both libAvaloniaNative.dylib and libcef.dylib` | Avalonia Native + CEF 各自实现了 NSToolbar 一个类 | 不影响基本渲染，深交互偶发崩溃（已记入 README） |
| `Ruffle instance destroyed` ~100ms 后 | `disconnectedCallback` → `destroy()` | Phase 3b 阻塞，未解 |
| `Serious error ... reading 'stream_from'` | `this.instance` 在 load() 异步期间被 null | 同上 |
| `warning NETSDK1194: The "--output" option isn't supported when building a solution` | 在**仓库根**跑 `dotnet publish -o`，根目录有 `.sln` | 仅警告不失败，但多工程会输出到同一目录；改为 `cd src/FlashBrowser-for-MacOS` 后再 publish（见 §10.2 第 2 步） |
| bundle 内 `Info.plist` 仍是旧身份（`CFBundleIdentifier=com.Mzying2001.CefFlashBrowser.Mac`） | ① 改名只做了手工 `mv`，没重新 bundle；② `dotnet publish -o` 叠加式写入，旧 apphost 留在 publish 里一起被打包 | 产物与「非 fork」的定位自相矛盾。已于 2026-09-11 重建修正；`bundle-mac.sh` 现内置旧 apphost 残留 WARN，重建后按 §10.2 第 4 步校验 |

### 10.7 git 仓库当前状态（**2026-09-10 已修正**）

```bash
git remote -v
# origin	https://github.com/losseeer/FlashBrowser-for-MacOS.git  ✅ 独立 repo
# （upstream 已移除，独立 repo 不再保留上游引用）
```

历史 commit：`bec41be` (scaffold) → `872874b` (PLAN v1) → `971d1dc` (PLAN §10+§11)。

新会话接手时**不要重复** `git remote set-url`，直接 `git pull` 即可。

### 10.8 验证检查清单（每阶段开始 / 结束跑一遍）

**开始一个阶段前**：
```bash
git status          # 确认 working tree clean
git log --oneline -5 # 确认在 main 分支上
dotnet build -c Release  # 确认基线 0 warn 0 err
```

**结束一个阶段后**：
- [ ] `dotnet build -c Release` 仍 0 warn 0 err（无回归）
- [ ] 新增的 success criteria 全部勾选（§2 阶段详述里每阶段都有）
- [ ] 改动至少 1 个 commit，且 commit message 描述了「为什么」+「如何验证」
- [ ] README 里的对应章节已更新（如果用户可见行为变了）
- [ ] §4.3 诚实红线检查通过（不夸大 GUI 验证覆盖度）

---

## 11. PLAN.md 自包含性矩阵（新会话接手 checklist）

| 任务类型 | 读完 PLAN.md 够吗？ | 需要额外读什么 |
|---|---|---|
| P1 SOL/AMF 单元测试开发 | ✅ 够 | `~/Dev/CefFlashBrowser/CefFlashBrowser.Sol/sol.h`（移植参考） |
| P2 swfproxy:// + file:// 分支 | ✅ 够 | 无 |
| P0-1 / P0-2 GUI 验证 | ⚠️ 不够 | §10.2 启动命令链 + §10.3 OOM 检查；**必须在内存恢复时执行** |
| P3 多 tab | ⚠️ 部分 | §附录 A 关键代码索引 + Avalonia 11 TabControl 文档 |
| P4 中文站点集成 | ❌ 不足 | 需要每个站点单独抓 HTML 反爬样本 |
| 命名修改 / 文件夹重构 | ⚠️ 容易遗漏 | §10.4 命名约定速查（7 处统一） |
| git 仓库结构修复 | ⚠️ 需手动 | §10.7 切换 origin 命令 |

**结论**：P1 / P2 在 OOM 环境**完全可独立推进**；P0 系列必须等内存恢复；P3 / P4 现阶段不建议启动。

---

## 附录 A · 关键代码索引

| 模块 | 文件 |
|---|---|
| CEF 初始化 | `src/FlashBrowser-for-MacOS/Program.cs:28-65`（`CefRuntimeLoader.Initialize` + **两个 CustomScheme 注册**）；`Main` 与 cachePath 在 `:18-22`；退出清理在 `:71-89` |
| 主窗口 | `src/FlashBrowser-for-MacOS/MainWindow.axaml.cs:21-39` |
| 早期注入事件 | `src/FlashBrowser-for-MacOS/MainWindow.axaml.cs:120-133` |
| 注入脚本（后期）| `src/FlashBrowser-for-MacOS/Ruffle/RuffleInjector.cs:133-255` |
| 注入脚本（早期）| `src/FlashBrowser-for-MacOS/Ruffle/RuffleInjector.cs:57-128` |
| SWF 跨域代理 | `src/FlashBrowser-for-MacOS/Ruffle/SwfProxySchemeHandlerFactory.cs`（在 `Program.cs:54-64` 注册）|
| Ruffle 资源供给 | `src/FlashBrowser-for-MacOS/Ruffle/RuffleSchemeHandlerFactory.cs`（在 `Program.cs:41-51` 注册）|
| .app 打包 | `bundle-mac.sh` |
| 已构建产物 | `dist-FlashBrowser-for-MacOS.app/` |

---

## 附录 B · 参考资料

- 上游项目：`/Users/dedsecczk/Dev/CefFlashBrowser/`（fork 已重命名为独立 repo，本仓库不再与上游共享 git 历史）
- PIVOT 战略报告：`/Users/dedsecczk/WorkBuddy/2026-09-10-14-04-22/research/macos-cefflashbrowser-oss-flash-game-browser/report.md`
- 4399 反 Flash 检测：`flashopen1.js` 中 `hasUsableFlash()` 函数（已镜像在 `RuffleInjector.cs:57-128` 的反例）
- CefGlue 120 文档：`CefRuntime.RegisterExtension` 在 CefGlue.Common **未公开**，本项目用 `JavascriptContextCreated` 替代