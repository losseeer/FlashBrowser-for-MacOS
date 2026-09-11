# PLAN.md Review — 2026-09-11

> 审阅对象：`PLAN.md`（24,244 bytes，11 节 + 附录 A/B）
> 审阅方式：逐节对照 **实际代码 / 仓库状态 / 当前机器环境**（所有结论附可复现命令）
> 审阅范围：只审「计划是否与事实一致、是否可执行」，不审产品方向

---

## 0. 总体结论

骨架质量高于一般个人项目路线图：**阶段划分有依赖分析、每阶段有可观察判据、§4 有硬/软判据分层、§4.3 有诚实红线、§10 有会话交接**。这套「判据驱动 + 交接自包含」的结构是 PLAN.md 最强的部分，建议保留。

但存在 **1 处硬伤**（发布的 `.app` 与仓库现状不符，且带上游作者 bundle id）、**1 类跨项目污染**（Agent2 术语与 taxonomy 漏进来）、**若干规划漏洞**（P1 的验证不存在对应工程 / fixture 来源不明 / 依赖图自相矛盾）。

严重度分布：**硬伤 1 · 事实错误 5 · 污染 3 · 规划漏洞 10**。

---

## 1. 硬伤（P0，必须先修）

### D1 · `dist-FlashBrowser-for-MacOS.app` 是**重命名之前**的产物，且 Info.plist 仍带上游作者身份

PLAN §0 把「独立 repo + 命名收敛 ✅」列为已完成，§10.1 把 `dist-FlashBrowser-for-MacOS.app/` 列为「已发布 .app」，README §已验证产物 声称该 bundle 内含 `Contents/MacOS/FlashBrowserForMacOS`。

实测（三段命令）：

```bash
plutil -p dist-FlashBrowser-for-MacOS.app/Contents/Info.plist
# CFBundleDisplayName => "CefFlashBrowser Mac"
# CFBundleExecutable  => "CefFlashBrowser.MacOS"        ← 不是 FlashBrowserForMacOS
# CFBundleIdentifier  => "com.Mzying2001.CefFlashBrowser.Mac"  ← 上游作者的 bundle id
# CFBundleName        => "CefFlashBrowser.MacOS"

ls dist-FlashBrowser-for-MacOS.app/Contents/MacOS/ | grep -i flashbrowser
# (空) —— 内部只有 CefFlashBrowser.MacOS / .dll / .deps.json / .pdb

ls -la publish/ | grep -i flashbrowser
# CefFlashBrowser.MacOS  (2026-09-09 16:31)
```

三处连锁结论：

| 问题 | 事实 | 影响 |
|---|---|---|
| **D1a 命名** | bundle 内可执行文件是 `CefFlashBrowser.MacOS`，**不存在** `FlashBrowserForMacOS` | README「已验证产物」章节描述的文件不存在；PLAN §10.4 写的 `CFBundleExecutable = FlashBrowserForMacOS` 与实物相反 |
| **D2 身份** | bundle id = `com.Mzying2001.CefFlashBrowser.Mac`，DisplayName = `CefFlashBrowser Mac` | 直接打脸 README 头条「**独立从零实现，非 fork**」「独立 repo 不再保留上游引用」。面试官一句 `plutil -p` 就能拆穿 |
| **D3 陈旧** | `publish/` 与两个 `.app` 都停在 `2026-09-09 16:31`（旧命名期），而源码已改成 `AssemblyName=FlashBrowserForMacOS`、`RootNamespace=FlashBrowserForMacOS`（`FlashBrowser-for-MacOS.csproj`） | 这个 `.app` **不对应当前源码**。它证明的是重命名前的构建，不是 PLAN §0 声称的「命名收敛」结果 |

**附带后果（D3 衍生）**：`bundle-mac.sh` 第 29-33 行有硬校验

```bash
APP_NAME="${APP_NAME:-FlashBrowserForMacOS}"
if [ ! -f "$PUBLISH_DIR/$APP_NAME" ]; then
    echo "ERROR: expected launcher binary not found: $PUBLISH_DIR/$APP_NAME" >&2; exit 1
fi
```

→ 照 PLAN §10.2 的命令链**直接**跑第 3 步 `./bundle-mac.sh ./publish`，会因为 `publish/` 里只有旧命名的二进制而**立即 exit 1**。必须**先**跑第 2 步重新 publish（新二进制）才能过校验；而 §10.2 的写法让人以为可以单独重跑第 3 步。

**修法**（建议）：
1. 删掉根目录 `FlashBrowser-for-MacOS.app/`（重命名期孤儿，454 文件）与 `dist-` 下陈旧产物；
2. 重新 `publish → bundle-mac.sh`，用 `plutil -p` 验证 `CFBundleIdentifier = io.github.losseeer.flashbrowserformacos`；
3. PLAN §0 的「命名收敛 ✅」改成「源码已收敛 ✅ / **产物待重建 ⚠️**」；
4. §10.2 标注「第 2 步与第 3 步必须成对执行」。

---

## 2. 事实性错误（与仓库/机器实际状态不符）

| # | 位置 | PLAN 声称 | 实测 | 修法 |
|---|---|---|---|---|
| **A1** | §10.1 表格 | `dist-FlashBrowser-for-MacOS.app/`（**git tracked**） | `.gitignore:11` 有 `*.app/`；`git ls-files` 顶层只有 `.gitignore / FlashBrowser-for-MacOS.sln / PLAN.md / README.md / bundle-mac.sh / src`（共 **22** 文件）。`.app` **未被跟踪** | 删掉「git tracked」；并在 §10.2 明说「新 clone 后**必须**自己 publish，仓库不含二进制」 |
| **A2** | §10.1 表格 | 节点 SDK：系统 `/opt/homebrew/bin/dotnet` 或 `~/.dotnet/dotnet` | `/opt/homebrew/bin/dotnet` **不存在**（`ls` 报 NOT FOUND）；只有 `~/.dotnet/dotnet`，且 `which dotnet` → not found（不在 PATH） | 删掉 homebrew 那项；§10.2 命令链开头补 `export PATH="$HOME/.dotnet:$PATH"`，否则第 1 条 `dotnet restore` 直接 command not found |
| **A3** | §6.1 / §10.3 | 环境约束三行：`Pages free ~19MB` / 总内存 16GB / Rosetta 不可用 | 真正的杀手没写：`sysctl vm.swapusage` → **total 11264M, used 10941M, free 322M** —— **swap 已近耗尽**，这才是 SIGKILL(137) 的直接原因。另外 `Pages free` 一分钟内从 `27356`（≈428MB）掉到 `8426`（≈132MB），**波动极大** | §6.1 补 swap 一行；§10.3 准入检查从「`vm_stat \| head -10` 看 free > 500MB」改成 `vm_stat \| head -6` **且** `sysctl vm.swapusage`，两条同时满足才允许起 GUI。另：阈值写成「free pages > 500MB」单位混用，应写「free ≥ 约 31250 pages（≈500MB）」或直接看 `memory_pressure` |
| **A4** | §10.4 命名约定 | 列了 7 处「统一」 | 这些值实际分散在 **3 个 source of truth**：`FlashBrowser-for-MacOS.csproj`（`RootNamespace`/`AssemblyName`/`ApplicationTitle`/`ApplicationId`）、`bundle-mac.sh`（`APP_NAME`/`APP_ID`/`APP_BUNDLE`/`VERSION`）、以及文档本身。目前四者**取值一致**，但文档没说「改的时候要同步这 3 处」 | 在 §10.4 加一行「命名有 3 处权威源，改动需同步」。这也正是 D1 的成因——重建产物时没回头对齐 |
| **A5** | §2 P0-1 表格「方案 A」 | 风险只写「内存峰值 69MB × 2（下载 + 引擎内）」 | 但**当前**路径已经是全量驻留：`SwfProxySchemeHandlerFactory.cs:71` `ReadAsByteArrayAsync()` + `Swf(bytes)` 里的 `MemoryStream`，69MB 先在 CEF IO 线程完整落内存，Ruffle 再持一份。**方案 A 并没有引入「2×」这个新事实** | 把 A 的风险改成真实的那条：「A 不增加峰值内存，但把 69MB 的等待从 Ruffle 侧搬到自定义 scheme 响应侧，**拉长 CEF IO 线程阻塞**（该 handler 目前是同步阻塞实现，见同文件 `:64` TODO）」 |

---

## 3. 跨项目污染（Agent2 / hm-dianping 的内容漏进来了）

| # | 位置 | 内容 | 问题 |
|---|---|---|---|
| **B1** | §4.3 诚实红线 第 3、4 行 | 「把 self_improve 写成已上线」/「`self_improve.py` 为理论占位，生产链路不调用」 | `self_improve.py` 是 **Agent2（hm-dianping）** 的产物，本仓库无此文件（`git ls-files` 共 22 个文件，无 .py）。整表是从另一项目搬过来的 |
| **B2** | §2 P0-2 目标 | 「覆盖『**吃 · 喝 · 玩 · 乐**』+ 一类异常」 | 吃·喝·玩·乐 是 Agent2 eval 的四分类。更严重的是：下方「用例池」表里 T1–T4 的「类别」列**全填「玩」**，与目标写的四分类**自相矛盾**；且 T2–T5 的 URL 还是 `<棋牌小游戏>` `<模拟经营>` 这类占位符，判据无法执行 |
| **B3** | §4.3 全表 | 第 2 行「ablation 不适用」 | 写成「不适用」本身没错，但它在表里的存在恰恰暴露了整表是搬运来的。本项目真正该守的红线应是：**GUI 未验证 ≠ 已解决**、**69MB SWF 下载成功 ≠ 游戏可玩**、**1 个游戏通 ≠ 全站兼容**、**Phase 2「polyfill 替换成功」是 JS 层证据，不等于渲染成功** |

**修法（已落地 2026-09-11）**：
- §4.3 **整表重写**为本项目自己的 6 条红线 —— 删掉 B1/B3 的 3 行搬运内容，补上：GUI 覆盖度 / 单用例不外推 / 未开工 Phase 不写成已实现 / 已定位未闭环 ≠ 已修复 / 未实测性能数字不写 / **产物身份与声明一致（`plutil -p` 核验，即 D1 的教训）**。
- §2 P0-2 删掉「吃·喝·玩·乐」，改为「**4 类交互维度 + 1 类异常**」；「类别」列改为「**验证维度**」（加载链路 / flashvars / SharedObject / 键盘事件 / 异常）。
- T1–T4 的占位 URL 换成 **4399 官方条目**（`205551_4` 死神VS火影3.3 / `117945` 全民斗地主 / `18012` 植物大战僵尸 / `130396` 爆枪突击），游戏名取自页面 `<title>`，URL 已 curl 实测 **HTTP 200**；T5 改为**条件式用例**（触发条件是「实测加载耗时 > 30s」，非固定 URL）。表下新增两条注：URL 来源 + **SWF 可玩性未验证**（受 §10.3 swap 约束）。

---

## 4. 规划漏洞与逻辑矛盾

| # | 位置 | 问题 | 建议 |
|---|---|---|---|
| **C1** | §3 依赖图 | ASCII 图把 P1/P2 画成挂在 `P0-2` 之下（`P0-2 ↓ ├─→ P1 ├─→ P2`），**同一节的「关键洞见」却写 P1/P2 不依赖 P0-2**。图与文直接冲突 | 改图：P1/P2 与 P0 系列**并列**，只在 MVP 收口处汇总（见本文件末尾的修正图） |
| **C2** | §2 P1 / §7 / §9 | P1 的成功判据核心是「单元测试解 10 个 fixture / round-trip 0 误差」，但 **`.sln` 只有一个工程**（`git ls-files` 无 Tests 目录），PLAN 全文**没有**「新建测试工程 + 选 xunit/NUnit」这一步，§7 P1 交付物也只写 `Sol/` 6 文件 + UI | 加 **P1.0**：`FlashBrowserForMacOS.Tests` 工程 + `dotnet test` 进入 §4.1 硬判据 |
| **C3** | §2 P1 判据 vs §10.1 | 判据要求「解 **10 个** fixture，AMF0/AMF3 各半」；但上游 TestData **只有 4 个**。**本轮实测（按 LSO 头解析）**：`settings.sol` 845 B = **AMF0**；`FBCookie.sol` 269 B / `pvz.sol` 95 B / `mao.sol` 34 B = **AMF3** → 实际是 **AMF0×1 + AMF3×3**，不是「各半」，缺的 6 个也没来源 | 已改：判据改为「解全部 4 个上游 fixture（AMF0×1 + AMF3×3）」+ 列 fixture 清单表；并明写「**AMF0/AMF3 各半不可写进判据**」。**同时消解逻辑陷阱**：不再要求「从 4399 游戏导出 .sol」补齐，因此不依赖 GUI |
| **C4** | §2 P0-1 方案 A | ① `load({ data })` **必须同传 `base`** —— 不传时 Ruffle 按 `swfproxy://app/load` 解析相对路径资源，而 `SwfProxySchemeHandlerFactory.cs:80-96` 只认 `?u=` 形态、`IsAllowed` 仅 `http/https`，外链资源必然失败。② 关于 flashvars：**我上一轮说错了** —— `configure` 里 `parameters` 其实**已在 `RuffleInjector.cs:227` 传入**，`extractFlashvars()` 不是「白做」，只要切换 `{ data }` 时别丢即可 | 已改：A 行重写为 `load({ data, base, parameters })`；`base` 单列为成功判据第 4 条（含「故意不传 base 应能复现失败」的对照实验）；并修正「内存 69MB×2」这个错误风险描述 |
| **C5** | §2 P0-1 代码锚点 | `RuffleInjector.cs:226-229`（`loadMainGame` 的 `player.load`）、`:133-255`（`BuildInjectionScript`）、`:57-128`（`BuildEarlyInjectionScript`）、`MainWindow.axaml.cs:21-39` / `:120-133` —— **逐条核对全部命中 ✅** | 无需改。这是 PLAN 写得最扎实的部分 |
| **C6** | §2 P0-1 代码锚点 | `Program.cs:17-32` 标为「CEF 初始化」，但该区间只覆盖 `Main` 签名 + cachePath；真正的关键内容 `CefRuntimeLoader.Initialize` + **两个 CustomScheme 注册**在 `:28-64` | 改成 `Program.cs:28-65`，并把 `RuffleSchemeHandlerFactory.cs` / `SwfProxySchemeHandlerFactory.cs` 的注册位置一并标出 |
| **C7 ⚠️ 升级为架构级发现** | §2 P1.5 | 原路径**两处都写反**（目录应是小写 `ruffle`；`#SharedObjects` 这种 `#` 连接命名是 **Flash Player** 风格，Ruffle 用真实嵌套目录 `SharedObjects/<domain>/<path>/<name>.sol`）。**更关键的是本轮实测发现：本应用根本不产生 `.sol` 文件** —— 内嵌 Ruffle 是 **web/WASM** 构建，存储后端 `ruffle_web::storage::LocalStorageBackend` → 写**浏览器 localStorage**；而 CEF profile 在 `Program.cs:20-22` 是**每次启动唯一的临时目录**，`Program.cs:83-86` **退出即递归删除** ⇒ **存档每次退出被清空**（这同时是 P0-2 的验证陷阱：依赖存档的游戏会表现成首次运行）| 已改：P1.5 重写为「前置调查」块 —— 3 条实测证据 + 3 条候选路径（均标**未实测**）+ **3 个设计路线（未定，需用户决策）**；并修正「三方格式互转」这一措辞（三者是**同一容器格式** LSO/`.sol`，差别只在存储目录布局与 AMF 版本，写「格式互转」会被追问打穿）|
| **C8** | §7 交付物 P2 | 只列 `SwfProxySchemeHandlerFactory.cs` + Avalonia 拖放，**漏了 `bundle-mac.sh`**。而 P2.4（Info.plist 关联 `.swf` UTI）只能改 `bundle-mac.sh` —— 它是 Info.plist 的唯一生成者（`bundle-mac.sh:52` 起的 heredoc） | §7 P2 行补 `bundle-mac.sh`（新增 `CFBundleDocumentTypes` / `UTImportedTypeDeclarations`） |
| **C9** | §9 关闭条件 | 「LICENSE 选定（MIT 优先）」被列为**发布阻塞项**，但 LICENSE 不存在，且没出现在 §2/§7/§8 任何阶段里 | 要么在 P1/P2 之间加一个「P1.5 仓库合规」小项，要么把它从关闭条件下调为软判据 |
| **C10** | csproj vs §6.2 | `csproj` 写 `<RuntimeIdentifiers>osx-arm64;osx-x64</RuntimeIdentifiers>`，而 §6.2/§10.3 明确「Rosetta 不可用，必须 ARM64」 | 保留 x64 无害（future-proof），但要在 §10.4 注明「x64 RID 仅为占位，发布/CI 只用 `osx-arm64`」，否则 `dotnet publish` 可能被误配成 x64 |

---

### 4.1 C7 升级说明：本应用不产生 `.sol` 文件（本轮实测）

审阅时只把 C7 当「路径写错」。本轮按 LSO 规范解析 fixture 时顺手查了内嵌 Ruffle 的存储后端，发现是**架构级**问题：

| 事实 | 证据 |
|---|---|
| 内嵌 Ruffle 是 **web/WASM** 构建 | `Assets/Ruffle/core.ruffle.*.wasm`，符号前缀 `ruffle_web[...]` |
| 存储后端是 **LocalStorageBackend**（浏览器 localStorage）；**无** DiskStorageBackend | wasm 符号表命中 `ruffle_web::storage::LocalStorageBackend` 与 `ruffle_core::backend::storage::MemoryStorageBackend`；另有错误串 `Unable to use localStorage:` |
| CEF profile = **每次启动唯一的临时目录** | `Program.cs:20-22`：`Path.GetTempPath()/FlashBrowserForMacOS_<GUID>` |
| **退出即递归删除整个 profile** | `Program.cs:24` 注册 `ProcessExit` → `Program.cs:83-86`：`Directory.Delete(cachePath, recursive: true)` |

**后果（两条，都影响既有计划）**：

1. **P1 的前提不成立**：P1 名义是「打开 / 编辑存档」，但本应用既**不落 `.sol` 文件**、也**没有持久 profile** ⇒ P1.5「自动定位 Ruffle 存档路径」在本应用上**无对象可定位**，P1 只能面向**别家运行时**产生的 `.sol`。已在 PLAN 给出 3 条路线（持久化 profile / 导入导出 / 纯离线文件工具），**需用户决策**。
2. **P0-2 的隐含陷阱**：依赖 SharedObject 的游戏（关卡进度、设置项）**每次启动都从零开始**。若某个 T 用例「看起来像卡在初始化」，**先排除这一条**再怀疑加载链路。

**顺带纠正我自己上一轮的一个结论**：C4 里说「flashvars 要迁到 `parameters`，否则 `extractFlashvars()` 白做」—— 实测 `RuffleInjector.cs:227` **早就传了** `parameters`。真正的缺口只有 `base`。

---

## 5. 值得保留的优点（别改）

| 项 | 为什么好 |
|---|---|
| §4.1/§4.2 硬判据 vs 软判据分层 | 明确「什么算阻塞」，避免用「能跑」掩盖「没验证」 |
| §10 会话交接（机器/路径/命令链/命名/OOM） | 真正解决了「新会话从零摸索」的成本，是本 PLAN 最有价值的一节 |
| §5 追问防穿 Q1–Q6 | 与简历叙事形成闭环（Ruffle vs Lightspark、CefGlue vs WKWebView 等答案都有可验证依据） |
| §2 每阶段的「反例防御」 | 「不能写 4399 完美兼容 / 可以写 N 个真实用例 end-to-end」——比泛泛的「注意不要夸大」可执行得多 |
| 代码锚点（除 C6 外） | 逐条实测命中，说明写文档时确实看了代码 |

---

## 6. 建议的修改顺序

> **进度（2026-09-11 更新）**：**第 1–6 步全部完成**（D / A / B / C 各项均已落地）。

| # | 项 | 状态 | 落地位置 |
|---|---|---|---|
| 1 | **D1/D2/D3** 重建产物 + 修 bundle-mac.sh 根因 | ✅ | `bundle-mac.sh`（新增 `APP_BASENAME` + 旧 apphost 守卫）；产物重建并核对 `CFBundleIdentifier=io.github.losseeer.flashbrowserformacos`；README「已验证产物」重写 |
| 2 | **A1/A2/A3** 修 §10 交接契约 | ✅ | `PLAN.md` §0 / §6.1 / §10.1 / §10.2 / §10.3 / §10.6 |
| 3 | **B1–B3** 重写 §4.3 红线表 + 修正 §2 P0-2 分类与占位 URL | ✅ | `PLAN.md` §4.3（6 条项目红线，换掉 self_improve/ablation）；§2 P0-2 用例池 T1–T4 换真实 4399 条目（HTTP 200 实测），T5 改为条件式 |
| 4 | **C1** 改 §3 依赖图（图与文对齐） | ✅ | `PLAN.md` §3 —— 改为「主体线（依赖 GUI 验证）」+「并行线（不依赖 P0-2，可立即开工）」两条并列链路；并补「P1.0/P1 纯 C# 可立即跑 `dotnet test`、P2 仅 P2.2/P2.3 需 GUI」 |
| 5 | **C2/C3** 补 P1.0 测试工程 + 明确 fixture 来源与数量 | ✅ | `PLAN.md` §2 P1（新增 P1.0 行、**fixture 清单表（AMF0×1 + AMF3×3，实测）**、「AMF0/AMF3 各半」禁令）、§4.1（`dotnet test` 入硬判据）、§7（P1.0 行）、§8（P1.0 0.5 人天）|
| 6 | **C4–C10** 逐条打补丁 | ✅ | C4 → §2 P0-1（`base` 单列判据 + 修正内存描述）；C6 → §附录 A（`Program.cs:28-65` + 两处 scheme 注册行号）；**C7 → §2 P1.5 整块重写**；C8 → §7 P2 补 `bundle-mac.sh`；C9 → §9 拆硬/软条件（LICENSE 降级）；C10 → §10.4 补 RID 注记。C5 复核确认无需改 |

**第 1 步补充说明（本轮实际做了什么）**：
- 根因不是「忘了重建」，而是 `bundle-mac.sh:19` 的 `APP_BUNDLE` 由 `APP_NAME`（无连字符）派生，
  展开成 `./dist-FlashBrowserForMacOS.app`，**与文档引用的 `./dist-FlashBrowser-for-MacOS.app` 不是同一目录** ——
  所以改名只能靠手工 `mv`，谁也想不到要重新 bundle。已引入 `APP_BASENAME` 修正。
- 顺带新增「旧 apphost 残留」守卫（`dotnet publish -o` 是叠加式写入，旧命名二进制会一起进 bundle）。
- 第 2 步顺带补了两条同一命令链上的实测坑：`NETSDK1194`（仓库根跑 publish）与
  批量删除保护拦截 `rm -rf publish` / `CLEAN=1 bundle`。

**第 3–6 步的补充发现**：§10 环境约束里真正卡住 GUI 的是 **swap 近耗尽**，不只是 free pages；
该条已随 A3 一并修正到 §6.1 与 §10.3。

---

## 附：本次 review 的复现命令

```bash
cd ~/Dev/FlashBrowser-for-MacOS
git ls-files | wc -l                       # 22
git ls-files | awk -F/ '{print $1}' | sort -u
cat .gitignore                              # 第 11 行 *.app/
plutil -p dist-FlashBrowser-for-MacOS.app/Contents/Info.plist
ls dist-FlashBrowser-for-MacOS.app/Contents/MacOS/ | grep -i flashbrowser
which dotnet; ls /opt/homebrew/bin/dotnet; ls -la ~/.dotnet/dotnet
vm_stat | head -6; sysctl vm.swapusage
ls ~/Dev/CefFlashBrowser/CefFlashBrowser.Tests/TestData/     # 只有 4 个 .sol
grep -n 'APP_NAME=' bundle-mac.sh
```
