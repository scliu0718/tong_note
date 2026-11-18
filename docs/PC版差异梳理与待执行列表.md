# 同笔记 PC 版（Windows）差异梳理与待执行列表

> **基准**：`docs/PC版需求文档.md`（2026-11-18，1212 行 / 14 章 + 4 附录 / 181 条 `PC-xx-nn` 需求 + 5.17 快捷键 17 行）
> **审计对象**：`windows_tonote/`（约 16,250 行，Core 层 `Tonote.Core`、App 层 `Tonote.App`、`tests/`、构建脚本与 README）
> **对照基准**：Mac `mac_tonote/Sources/WorkLogCore/`、`WorkLogRichText/`、`WorkLog/` 与 Android `android_tonote/` 的同步 / 编解码实现
> **审计性质**：只读审计，未修改任何源码；三路并行（同步与数据层、UI 与业务覆盖、富文本与表格附件）交叉核对
> **关注维度**：① 逻辑是否一致 ② 业务功能缺失与否。**UI 视觉对齐不作为独立维度**，仅在它构成业务功能缺失证据或影响验收文案时列入（集中在 W-41 / W-42）
> **执行方式**：按 `W-01 → W-43` 顺序逐条执行；每条是独立工作单元，含现状证据、要做的改动、验收口径、代码落点与依赖

---

## 0. 先说结论

### 0.1 总体判断

Windows 版的**骨架与协议底层质量不低**：三栏三档侧栏、按篇存储的同步 Core、四状态任务、回收站墓碑、受控 HTML 编解码、表格模型、设置面板、WebView2 编辑器都已成型。真正的问题不在「有没有做」，而在两处：

1. **同步引擎的三条铁律没有落地**——下载失败被当成「云端无文件」、`create errno=0` 后仍因回读抖动反复重传、目录列表存在性判定缺失。这三条叠加会产生**本地旧版反向覆盖云端**的数据丢失路径。
2. **需求点名要求「PC 修正 Mac 缺陷」的地方，Windows 反而照抄了缺陷**——回收站搜索失效、字号菜单勾选 15、侧栏硬编码昵称未改、旧 Markdown 仅打开就被改写。同时 `SyncScope` 页面范围同步、任务字段三方合并、图片粘贴链路、快捷键、安装包是成片缺失。

按 181 条需求粗估：**约 50 条已实现、70 条部分实现、60 条缺失**（快捷键按 17 行计，为审计估算而非逐条精确统计）。三路审计合并去重后形成 **43 个执行单元：P0 23 条 / P1 17 条 / P2 3 条**。

### 0.2 两个维度的判定口径

| 维度 | 判定标准 | 典型表现 |
|---|---|---|
| **逻辑不一致** | Windows 有实现，但语义 / 决策 / 错误分类 / 跨端契约与 Mac 基准或需求第 5~8 章不符；**结果可能错，比「没做」更危险** | 下载 404 → 抛 `BaiduPanNotFoundException` → 同步层吞成 `null` → 走 `Upload`；`data-fullwidth="1"` 当满宽 |
| **业务功能缺失** | 需求编号对应的能力在代码中完全没有实现，或只有声明 / 死代码 / 兜底入口 | 无 `SyncScope`、无 `baseTodo`、剪贴板粘贴只取 `text/plain`、`ImagePathScanner` 无任何调用方 |

> 两者兼有的条目（如 `SyncScope`、`mergeTodos`）标为「两者兼有」，按「缺失」立项、按「逻辑不一致」验收。

### 0.3 上线阻断项命中情况（需求 12.6）

| # | 阻断项 | 当前状态 | 对应执行单元 |
|---|---|---|---|
| 1 | 任何导致云端数据被旧数据覆盖的路径 | **已命中**（断网 / `errno 20020` / 风控 HTML / filemetas 漏项任一场景都会触发） | W-01、W-02、W-03、W-04、W-05 |
| 2 | 任何静默丢失用户输入或图片的路径 | **已命中**（粘贴图片静默丢弃、裸 `<img>` 解码丢图、表格删行列静默 return） | W-07、W-15、W-19 |
| 3 | 跨端数据格式不兼容 | **已命中**（表格满宽语义反转；默认色/引用灰被固化；旧 Markdown 被改写） | W-08、W-09、W-10 |
| 4 | 令牌或密钥出现在日志、诊断信息或崩溃报告中 | 目前未见明文日志，但令牌为明文文件且 `client_secret` 走 GET query，日志体系补齐前不可判「已达标」 | W-37、W-38、W-39 |
| 5 | 登录后无法回到应用（协议回调失败） | 协议注册与单实例转发已有；**已有窗口置前缺失**、`state` 空值跳过校验、登录中按钮可重入 | W-21、W-23 |

**结论：当前版本不满足上线条件**，必须先完成批次 0~3（P0 23 条）。

### 0.4 怎么依次执行

```
批次 0  P0 数据正确性（同步三铁律）      W-01 ~ W-06   ← 必须先做，否则后面任何补齐都会放大丢数据风险
   ↓
批次 1  P0 跨端数据契约                  W-07 ~ W-11
   ↓
批次 2  P0 核心能力                      W-12 ~ W-17   ← W-12/W-14 与批次 0 同一引擎，建议连做
   ↓
批次 3  P0 安全与平台                    W-18 ~ W-23
   ↓
批次 4  P1 功能补齐                      W-24 ~ W-36
   ↓
批次 5  P1 可靠性 / 安全                 W-37 ~ W-40
   ↓
批次 6  P1/P2 文案与视觉统一             W-41 ~ W-43
```

执行约定（项目长期规则）：**每轮只做编译 + 重启 / 安装，不做自动化 UI 测试，实机由用户手工验收**；Core 层离线单测属需求 12.2 明确要求，保留为 `dotnet test`；每轮精确 `git add` 本轮文件，避免带入并行会话半成品。

---

## 1. 覆盖度与差异速览

### 1.1 已对齐，不需要动（避免重复劳动）

| 域 | 已对齐内容 | 证据 |
|---|---|---|
| 同步决策表 | `AfterFetch` 7 行全分支（rev 公式 / neverSynced 平局云端胜 / `changed` / `!dirty` / `dirty&&!changed` / `conflict`）、`SameTime` 1 秒、`RemoteWins >=`、`ShouldSkipFetch` | `SyncRules.cs:57-98` |
| 云端契约 | `/apps/tong_log` 固定根 + `TONOTE_REMOTE_ROOT` 沙箱、`notes/<UUID>.json`、`todos/`、`notebooks.json`、`devices/`、`avatar.json`、`manifest.json`、`attachments/`，大写 UUID、分页 200/5000 | `BaiduPanConfig.cs:13,20,35-43`；`SyncEngine.cs:781-820` |
| 上传 | `precreate → superfile2 → create`、4 MiB 分片、MD5 小写 hex、`rtype=3` / `autoinit=1`、秒传 `return_type==2`、`create errno=0` 客户端层即返回 | `BaiduPanClient.cs:412-513`；`SyncRules.cs:101-120` |
| 回环防护 | 所有用户写操作 `Touch → MarkDirty → Schedule` 全覆盖；同步写回走**不标脏** upsert（`ApplySyncedNote/Todo`、`ApplyCloud`） | `NoteStore.cs:79-257`；`TodoStore.cs:46-153`；`SyncRules.cs:190-208` |
| 墓碑 / 笔记本 / 附件 | `purged` 只传墓碑跳过附件、`deleteRemote` 把 `-9/31066` 视为已不存在；笔记本 `names=(local∪remote)−removed`；附件正则去重保序 + `uploadedAttachments` 跳过 | `SyncEngine.cs:396-427,701-777`；`SyncRules.cs:122-175` |
| 设备会话 | 设备号生成后不变、`BeginLoginAsync` 换 sessionId、`Decide` 三态、`Kick` 只改对方 kicked、不能下线自己、机器名 | `DeviceSession.cs:53-289` |
| HTML Core | 行内 run 嵌套顺序、标题不套 `<strong>`、转义与栈式解析、`<tbody>/<thead>/<tfoot>` 展开、`<th>` 当 `<td>`、`data-align` / `data-bg` 兼容 `style` 与 `rgb()`、表格 100×32 上限与矩形化、`data-widths` 权重、**Core 层 `data-fullwidth="0"` 语义正确** | `RichTextHtmlCodec.cs:54-252`；`RichTextTableCodec.cs:18-127`；`RichTextDocument.cs:191-353` |
| 视图层已做对 | 任务终态只读（含写入守卫）、笔记删除=移入回收站、排序与间接置顶、侧栏三档阈值 82/156 一次一档、头像 8 预设、回收站只读编辑器、WebView2 缺失回退不破坏 HTML、约 850ms 自动保存 | `ShellView.cs:482-639,1069-1110,1140-1195`；`AvatarStore.cs:18-28`；`RichTextEditor.cs:465-553` |

### 1.2 差异分布（合并去重后）

| 批次 | 维度 | 条数 | 优先级 | 主要落点 |
|---|---|---|---|---|
| 0 | 逻辑不一致（5）+ 功能缺失（1） | 6 | P0 | `BaiduPanClient.cs`、`SyncEngine.cs`、`tests/` |
| 1 | 逻辑不一致（4）+ 功能缺失（1） | 5 | P0 | `RichTextHtmlCodec.cs`、`EditorHtml.cs`、`RichTextEditor.cs`、`tests/` |
| 2 | 两者兼有（3）+ 功能缺失（3） | 6 | P0 | 新增 `SyncScope.cs`、`SyncRules.cs`、`EditorHtml.cs`、剪贴板链路 |
| 3 | 两者兼有（2）+ 逻辑不一致（1）+ 功能缺失（3） | 6 | P0 | `BaiduLogin.cs`、`MainWindow.axaml.cs`、安装包脚本、全局快捷键 |
| 4 | 逻辑不一致（2）+ 两者兼有（5）+ 功能缺失（6） | 13 | P1 | `ShellView.cs`、`Dialogs.cs`、`RichTextEditor.cs`、`AppSession.cs` |
| 5 | 逻辑不一致（2）+ 功能缺失（2） | 4 | P1/P2 | `TonoteJson.cs`、`BaiduPanTokenStore.cs`、`BaiduPanClient.cs` |
| 6 | 逻辑不一致（2）+ 功能缺失（1） | 3 | P1/P2 | 文案表、CSS、P2 增强 |
| **合计** | **逻辑不一致 16 / 功能缺失 17 / 两者兼有 10** | **43** | **P0 23 / P1 17 / P2 3** | — |

---

## 2. 待执行列表（W-01 ~ W-43）

### 批次 0 · P0 数据正确性（同步三铁律）

#### W-01 下载链路绝不能抛 `notFound`

- **维度**：逻辑不一致 · **优先级**：P0 · **需求**：7.1 铁律 1、PC-S-07、PC-S-15、7.8、11.2 #4
- **现状**：`BaiduPanClient.cs:311-312` 在 `dlink` 为空时直接 `throw new BaiduPanNotFoundException(remotePath)`；`DlinkFromFilemetasAsync` 的 :379-382（列父目录抛 NotFound）、:386（查不到 fs_id）、:402（filemetas errno≠0）、:404-405（info 空）全部退化成 `null` → 上层必然抛 NotFound；:326-338 直链 404 重取一次仍失败也抛 NotFound。随后 `SyncEngine.cs:822-832 / :838-848` 把该异常吞成 `return null`，:343-345 / :558-560 交给 `AfterFetch` → `SyncRules.cs:69-72` 返回 `Upload`。
- **对照**：Mac `BaiduPanClient.swift:248-267` 注释明确「本方法只抛临时错误，绝不抛 notFound」，3 次重试后抛 `server`；`SyncEngine.swift:819-821` 用 `try` 不吞错误。
- **要做**：把下载链路所有 `null` / `BaiduPanNotFoundException` 出口改为**临时错误**（强类型 `temporary` / `server`），同步层不再吞；`notFound` 只能由 W-02 的目录列表判定产生。顺带补齐 3 次重试 + `300ms×attempt` 退避、`downloadByPathBlocked` 进程级标志（命中 `errno 20020` 后本进程直走 filemetas）、250ms 强刷父目录重取 fs_id、路径→fs_id 缓存（列目录与 `create` 成功后写入）、空内容重试；风控返回非 JSON HTML 时用 `try` 吞掉并继续降级，不要中断。
- **验收**：注入断网 / 502 / `errno 20020` / 直链 404 / filemetas 漏项 / 风控 HTML，本地旧数据**不得覆盖云端**；日志只出现临时错误，本轮该篇跳过、下轮重试；无变更时请求数符合 9.1（1~3 个）。
- **落点**：`Tonote.Core/Baidu/BaiduPanClient.cs:277-408`、`Tonote.Core/Sync/SyncEngine.cs:822-848`
- **依赖**：无（**必须先做**）

#### W-02 云端存在性改为「目录列表 + 是否同步过」三分支

- **维度**：逻辑不一致 · **优先级**：P0 · **需求**：7.1 铁律 3、PC-S-07、7.3、7.4、7.8
- **现状**：`SyncEngine.cs:334-345`（笔记）、:550-560（任务）**没有 `listing == null` 分支**，无论目录里有没有该 id 都无条件下载；`listing` 只用于 `ShouldSkipFetch` 和记 `ServerMtime`；`ListJsonAsync:804-807` 把列目录的 NotFound 吞成空字典。
- **对照**：Mac `SyncEngine.swift:264-275 / :573-584` 与 Android `SyncEngine.kt:487-496` 的三分支。
- **要做**：① `listing == nil && baseRev != 0` → 跳过本轮、错误上抛、等列表恢复；② `listing == nil && baseRev == 0` → 认定云端无文件，`remote = nil`；③ `listing != nil` → `try await download`，错误一律上抛。补 `BaiduPanClient.remoteFileExists`（由目录列表判定），列目录失败不得退化成空字典。
- **验收**：注入「目录有 id 但下载失败」→ 不上传；注入「目录无 id 且曾同步过」→ 跳过本轮；注入「列目录返回 notFound」→ 不得触发全量本地上传；「从未同步过」不发多余下载请求。
- **落点**：`Tonote.Core/Sync/SyncEngine.cs:334-345,550-560,781-820`、`Tonote.Core/Baidu/BaiduPanClient.cs`
- **依赖**：W-01

#### W-03 `publish` 回读只用于确认，不得 `rev+1` 重传

- **维度**：逻辑不一致 · **优先级**：P0 · **需求**：PC-S-06、7.1 铁律 2、7.7、7.8
- **现状**：客户端层 `BaiduPanClient.cs:473-475` 已合规（`create errno=0` 即返回）；但 `SyncEngine.cs:386-452`（笔记）、:600-649（任务）只回读 **1 次**，无 3 次重试 / 400ms×retry；`check == null` 时 for 循环直接 `continue` → **重新上传整篇 JSON + 附件**；`check` 的 rev / 时间不符 → `AfterFetch` → `rev+1` 重传；3 次后抛「连续三次没能写上云端」，cursor 未更新、dirty 仍 true，下轮继续传。
- **对照**：Mac `SyncEngine.swift:341-370 / :637-655` 明确「create errno=0 即代表文件已写入……不能因为直链抖动就 rev+1 反复重传」；Android `SyncEngine.kt:571-595` 同构。
- **要做**：`create errno=0` 即标记已发布；回读改为**最多 3 次、退避 400ms×retry**且只做确认；回读全失败 → `dirty=false`、`applySyncedNote/Todo`、记状态、留待下一轮校验，**不重传、不 rev+1**。
- **验收**：注入回读失败 → 不产生重复上传、下一轮校验后收敛；注入限速 `errno 31034` → 不出现持续重复上传；3 次后不误报「没能写上云端」。
- **落点**：`Tonote.Core/Sync/SyncEngine.cs:386-452,600-649`
- **依赖**：W-01、W-02

#### W-04 附件下载失败要留下轮重试

- **维度**：逻辑不一致 · **优先级**：P0（连锁于 W-01）· **需求**：PC-S-08、7.7
- **现状**：`SyncEngine.cs:770` `catch (BaiduPanNotFoundException) { continue; }`，`complete` 保持 true 视为成功且不加 `UploadedAttachments`；`UseRemote` 分支 :366-368 / :581-583 的 `serverMtime = null` 保护不触发。
- **对照**：Mac `SyncEngine.swift:490-493` 统一 `catch { complete = false }`。
- **要做**：附件下载任何异常一律 `complete = false` 并置 `cursor.serverMtime = null`，下一轮重试；不因「文件不存在」提前跳过。
- **验收**：注入附件 404 → 下轮重新拉，图片最终可见；断网恢复后 ≤10 秒开始补拉。
- **落点**：`Tonote.Core/Sync/SyncEngine.cs:723-777`
- **依赖**：W-01

#### W-05 错误分类改为强类型，别用中文子串匹配

- **维度**：逻辑不一致 · **优先级**：P0 · **需求**：9.2、PC-SY-04
- **现状**：`SyncEngine.IsTransient:253-267` 依赖「请求失败 / proxy / 502 / 503 / 504」中文消息子串；`BaiduPanClient.cs:322-323` 抛「下载文件失败（HTTP …）」、:502 抛「上传分片失败（HTTP …）」都不在匹配表 → 下载 / 上传类瞬态被判永久错误，不进入 :237-238 的同轮补跑。
- **对照**：Mac 用强类型 `BaiduPanError.server / temporary`。
- **要做**：在 `BaiduPanErrors.cs` 定义 `Temporary / Permanent / DeviceKicked` 强类型异常，客户端所有网络失败点按类型抛；`IsTransient` 改类型判断；90 秒超时保留。
- **验收**：断网、502、404、分片失败、令牌中途续期都能正确进入重试或终止，不靠字符串。
- **落点**：`Tonote.Core/Baidu/BaiduPanErrors.cs`、`BaiduPanClient.cs`、`Tonote.Core/Sync/SyncEngine.cs:232-267`
- **依赖**：W-01

#### W-06 故障注入测试集（与批次 0 同步落地）

- **维度**：功能缺失 · **优先级**：P0 · **需求**：PC-SY-08、PC-SY-01、12.2、11.2 #17
- **现状**：`tests/Tonote.Core.Tests/CoreTests.cs` 13 条离线单测（JSON / 存储 / 回收站 / 笔记本 / Todo / VisibleText / OAuth）；`tests/Tonote.LiveTests/` 是真实网盘分组测试 A~H；**无断网 / 列目录异常 / 下载 404 / 回读失败 / `errno 20020` / `31034` 限速 / 令牌中途续期 / `enqueue` 纯函数**专项测试。README 亦承认未验证故障注入。
- **要做**：新增 FakePan 内存假网盘 + 上述 7 类故障注入用例；`SyncScope.enqueue` 纯函数单测随 W-12 一并补。
- **验收**：`dotnet test` 全绿；每类故障断言「本地旧数据不覆盖云端 / 不重复上传 / 下轮收敛」，作为 W-01~W-05 的回归网。
- **落点**：`tests/Tonote.Core.Tests/` 新增测试文件、`windows_tonote/README.md`
- **依赖**：W-01（建议同批做，边修边验）

---

### 批次 1 · P0 跨端数据契约

#### W-07 Core 解码补块级裸 `<img>`

- **维度**：逻辑不一致 · **优先级**：P0 · **需求**：PC-I-09、6.3 解码容错第 2 条、12.4 X3、12.6 第 2 项、11.2 #4
- **现状**：`RichTextHtmlCodec.cs:157-213` 的 `BlocksFrom` 有 `p/h1~h6/blockquote/pre/ul/ol/table/br`，**没有 `case "img"`**；裸 `<img>` 落入 default（:193-207）因无 children 返回空列表 → `Decode` 得到 `RichTextDocument.Empty()`。Android 端确会产出 `<p>文字</p><img …>`（见 `tests/Tonote.LiveTests/GroupC.cs:241,260`）。`RichTextEditor.cs:539-541` 的 `onlyEmpty` 判定会把 `normalized=""` 写回 → **纯单图笔记整篇正文被清空**。JS `clean()`（`EditorHtml.cs:191-209`）会把裸 img 包进 `<p>`，但只覆盖「编辑后保存」链路。
- **对照**：Mac `RichTextHTMLCodec.swift:213-216` 已修。
- **要做**：`BlocksFrom` 增 `case "img"`，单独成段（`inlineRuns` 处理），保持 `Encode(Decode(html))` 归一化后不丢图。
- **验收**：12.4 X3 通过；纯单图笔记打开不编辑再切换，正文不变；带文字的裸图笔记图片可见。
- **落点**：`Tonote.Core/Html/RichTextHtmlCodec.cs:157-213`
- **依赖**：无（可与批次 0 并行）

#### W-08 表格 `data-fullwidth` 语义纠正

- **维度**：逻辑不一致 · **优先级**：P0 · **需求**：PC-T-01、PC-T-05、6.4、12.4 X1·X2
- **现状**：**Core 层是对的**（`RichTextTableCodec.cs:18-20,72`：`"0"` = 固定宽、缺失 = 满宽），错全在 App 层：`EditorHtml.cs:91` 用 `table[data-fullwidth="1"]{width:100%}` 依赖非法值；`:321` 新建表写 `<table data-fullwidth="1">` 且无 100px 列宽；`:384-390` `setFullWidth` 直接写 `"1"/"0"`；`RichTextEditor.cs:386-387`「匹配笔记宽度」传 `"1"`。结果是 Mac/Android 产出的满宽表（无属性）在 Windows 不铺满，固定宽表 `data-fullwidth="0"` 在 Windows 既不铺满也无固定列宽。
- **要做**：CSS 改为 `table{width:100%}` + `table[data-fullwidth="0"]{width:auto}`，每列固定 100px；新建表写 `data-fullwidth="0"`；`setFullWidth` 与工具栏命令改为「满宽 = 移除属性 / 固定宽 = 写 `"0"`」。
- **验收**：12.4 X1 / X2 一致；新建表不铺满；三端互开表格行列数与宽度模式一致。
- **落点**：`Tonote.App/RichText/EditorHtml.cs:91,321,384-390`、`Tonote.App/RichText/RichTextEditor.cs:386-387`
- **依赖**：无

#### W-09 旧 Markdown 笔记仅打开不编辑不得改写

- **维度**：逻辑不一致 · **优先级**：P0 · **需求**：6.5、PC-E-11
- **现状**：`RichTextEditor.cs:453-455` 打开 markdown 笔记即把 `_pendingHtml` 设为 `Encode(MarkdownConverter.ToDocument(note.Body))`；`EditorHtml.cs:227-230` 编辑器 `blur` 也 post `changed`；`RichTextEditor.cs:538-546` 的 `SaveHtml` 对 `ContentFormat != richText` **无「是否真编辑」判断**，直接 `UpdateContent(id, normalized, richText)`。→ 打开旧笔记后仅切换笔记 / 失焦就升级为 `richText` 并改写 `body` 与 `updatedAt`，与 Android「Mac 的 Markdown 笔记只读」的跨端保护相反。
- **要做**：记录「本会话是否发生真实内容编辑」（JS `input`/`beforeinput` 或真正的内容差异比对）；未编辑则不写盘、不升级 `contentFormat`、不动 `updatedAt`；只有用户真实输入后才按 `MarkdownConverter` 有损简化并升级。
- **验收**：打开 Mac 的 Markdown 笔记 → 切换 / 失焦 / 关闭，`body`、`contentFormat`、`updatedAt` 三项均不变；输入一个字后才升级。
- **落点**：`Tonote.App/RichText/RichTextEditor.cs:453-455,538-546`、`EditorHtml.cs:211-230`
- **依赖**：无

#### W-10 HTML 清洗契约三项（颜色 / `td style` / 内联图片）

- **维度**：逻辑不一致 · **优先级**：P0 · **需求**：PC-F-06、PC-F-09、PC-T-04、6.3 第 6 条、12.4 X1·X2
- **现状**：
  - `RichTextHtmlCodec.cs:389-391` / `:100-101` 无条件写 `<span style="color:…">`；`EditorHtml.cs:124-135` `cleanStyle` 保留任意 `color`；`RichTextEditor.cs:318-323`「恢复默认文字色」写 `#1D1D1F`。→ 默认色 / 引用灰 `#86868B` 被固化，Mac/Android 打开当显式颜色。
  - `EditorHtml.cs:116` 的 `td` 白名单只有 `['data-align','data-bg']`（无 `style`），而 Core `RichTextTableCodec.cs:95-127` **是兼容** `style="background-color:#…"` 的。→ 打开 Mac 老笔记一编辑保存，单元格底色被 CSS 清洗洗掉。
  - `EditorHtml.cs:147-151` 对 `src` 仅在含 `attachments/` 时改写，`data:` / `blob:` / 绝对 file 路径原样保留，无转存附件逻辑。
- **要做**：① 仅当颜色 ≠ `#1D1D1F` 且 ≠ `#86868B` 才写 HTML（对齐 Mac `RichTextBridge.swift:751-761`），背景 α≤0.01 视为无；② `td` 白名单补 `style` 或清洗时把 `background-color` 归一化为 `data-bg`；③ `data:` / `blob:` / 绝对路径图片在保存前转存为 `attachments/<noteUUID>/<UUID>.<ext>` 相对路径，否则按 6.3 第 6 条**不写盘并报错**。
- **验收**：PC-F-06 / PC-F-09 / PC-T-04 验收通过；三端互开不出现多余 `<span style="color:…">`；含 `td style` 的老笔记编辑保存后底色保留。
- **落点**：`Tonote.Core/Html/RichTextHtmlCodec.cs:100-101,389-400`、`Tonote.App/RichText/EditorHtml.cs:113-153,191-224`、`Tonote.App/RichText/RichTextEditor.cs:318-323`
- **依赖**：W-07

#### W-11 CoreTests 补编解码 / 表格 / 裸 img 单测

- **维度**：功能缺失 · **优先级**：P0 · **需求**：12.2、12.3、12.4 X3
- **现状**：13 条单测无 `RichTextHtmlCodec` 往返、无表格（`tbody` / 裸 `img`）、无 `ImagePathScanner`、无 `MarkdownConverter`；LiveTests `GroupE.cs:176-195` 用的是 `<p><img …></p>`（图片包在 p 内），`GroupC.cs:241,260` 的裸 `<img>` 只走同步上传下载、**不经 `Decode`**，`GroupF.cs:198-212` 直接对 `Decode` 测、绕过 JS `clean()` → W-07 / W-08 / W-10 无任何测试拦截。
- **要做**：补 `Encode(Decode(html))` 往返、`tbody` 展开且不写出、`data-widths` 保留、裸 `<img>` 解码、JS `clean()` 后 `td[style]` 保留、`ImagePathScanner`、`MarkdownConverter` 单测。
- **验收**：`dotnet test` 全绿，12.2 表中 Core 层 7 类全覆盖；新增测试能复现并拦住 W-07 / W-08 / W-10 的三类退化。
- **落点**：`tests/Tonote.Core.Tests/CoreTests.cs` 或新增 `HtmlCodecTests.cs`
- **依赖**：W-07、W-08、W-10

---

### 批次 2 · P0 核心能力

#### W-12 `SyncScope` 队列与调度核心

- **维度**：两者兼有 · **优先级**：P0 · **需求**：PC-S-11、7.4、7.5、PC-SY-01/02/03/04/06、7.10、11.2 #1、里程碑 M1
- **现状**：`SyncEngine.cs:36` `DebounceMilliseconds = 1500`；`Schedule():151-171` 为 1.5 秒防抖；`SyncRoundAsync:179-185` 忙时只置 `_needsAnotherRound = true`，**丢弃 scope 与 priority**，只是「再跑一轮全量」；`SyncTodosAsync:504-525` 无 `dirtyOnly` / `onlyTodoId` 参数。全仓检索 `SyncScope|enqueue|dirtyOnly|onlyTodoId|scheduleNotesUpload|flushNotes` **零匹配**。`Dialogs.cs:600` 的使用说明还在宣传「1.5 秒防抖」。
- **对照**：Mac `SyncEngine.swift:61-79,103-110`；Android `SyncScope.kt:1-25`、`SyncEngine.kt:455-476`。
- **要做**：新增 `SyncScope.cs`（7 种枚举 `.all / .notes / .note(id) / .quickSync / .todos / .todo(id) / .dirtyTodos`）；唯一入口 `request(scope, priority)`；正在同步 → `enqueue(scope, front)` 纯函数（插队队首、`.all` 覆盖、同范围去重）；笔记 30 秒 / 快同步 15 秒节流（计时中不重置、快同步提前截止）；只处理脏数据；离开笔记 / 任务页即 flush；窗口关闭 flush 最多等 3 秒；同步中按钮不置灰。
- **验收**：PC-SY-01（`enqueue` 纯函数单测）、PC-SY-02（连续编辑 4 次上传 ≈4 次 / 8 次 ≈8 次上限内）、PC-SY-03（网络面板仅出现该条任务的上传）、PC-SY-04、PC-SY-06；无变更时请求数 1~3 个。
- **落点**：新增 `Tonote.Core/Sync/SyncScope.cs`；重构 `Tonote.Core/Sync/SyncEngine.cs:36,151-250,504-525`
- **依赖**：W-01~W-05（同一引擎，建议连做）

#### W-13 所有同步触发点 scope 化

- **维度**：功能缺失 · **优先级**：P0 · **需求**：7.5、PC-SY-03/04/07、PC-L-09、PC-TD-11/16、PC-SE-01
- **现状**：`ShellView.cs:113`「同步本篇」→ `SyncSingleNoteAsync`（无插队）；`:337` 进入任务页 → `QuickSyncAsync()` **全量**；`:360` 任务页「同步」按钮 → `QuickSyncAsync()` **全量**；`Dialogs.cs:376`「立即同步」→ 全量（此项对齐）；`AppSession.cs:57` 45 秒 `PeriodicTimer` 全量；`ShellView.cs:881-888` 筛选栏**没有绿色「一键同步」按钮**（全仓 Grep「一键同步」无结果）；侧栏同步状态仅文字、无「N 项待上传」与旋转反馈（PC-SY-05/07）。
- **要做**：点开笔记 / 快同步卡 → `.note(id)` 插队；任务保存 → `.todo(id)` 即刻上传、离开任务页 → `.dirtyTodos`；任务页与设置按钮按当前范围；前台 45 秒保持 `.all`；补「一键同步 / 已同步」按钮（`.notes`、同步中 ProgressView、不置灰、文案随状态跳变）与「N 项待上传」指示；删除使用说明里的「1.5 秒防抖」表述。
- **验收**：7.5 逐条；同步中再点另一篇排到队首；任务保存网络面板只出现该条；X4~X6、X11 通过。
- **落点**：`Tonote.App/ShellView.cs:113,205-213,337,360,881-888`、`Tonote.App/Dialogs.cs:376,600`、`Tonote.App/AppSession.cs:51-68`
- **依赖**：W-12

#### W-14 任务字段三方合并 `mergeTodos` + `baseTodo` 祖先快照

- **维度**：两者兼有 · **优先级**：P0 · **需求**：PC-S-05、7.6、6.1、6.2、11.2 #2、12.4 X4
- **现状**：`SyncRules.cs:7-34` 的 `SyncCursor` **无 `BaseTodo`**（`Clone():26-33`、`SyncEngine.cs:907-914` 的 `CopyCursorFrom` 也无）；全仓 **无 `MergeTodos` / `TodoMergeResult`**；`SyncEngine.cs:651-686` `ResolveTodoConflictAsync` 直接 `RemoteWins`（:654）拆冲突副本（:667-685）；:897-905 的 `MergeTodos(local, remote)` 仅按 `updatedAt` 取新者。
- **对照**：Mac `SyncRules.swift:16,95-156`、`SyncEngine.swift:692-705`；Android `SyncRules.kt:82-142`、`SyncEngine.kt:423-424,505,521,528-540`。
- **要做**：`SyncCursor` 加 `BaseTodo` 并在 `UseRemote` / `Publish` / `RememberMtime` / 合并成功各处回写快照；实现 `mergeTodos(base, local, remote)` 按字段三方合并（status / terminated 优先、purged 墓碑优先、日期取新、**仅 `notes` 双改才出冲突副本**）；合并成功以 `rev = max+1` 写回原 `todos/<id>.json`，不生成副本。
- **验收**：12.4 X4「手机改备注 + PC 改状态」合成一条、无副本；X5 笔记正文双改仍出「（冲突副本）」两份都不丢。
- **落点**：`Tonote.Core/Sync/SyncRules.cs`、`Tonote.Core/Sync/SyncEngine.cs:651-686,897-914`、`Tonote.Core/Storage/TodoStore.cs:46-54`
- **依赖**：W-12（同期可先做模型部分）

#### W-15 图片粘贴 / 复制 / 拖拽全链路

- **维度**：功能缺失 · **优先级**：P0 · **需求**：PC-I-01~07、PC-I-10、PC-E-08/09、12.3、5.17 `Ctrl+V/C/Shift+V`、12.6 第 2 项、里程碑 M2
- **现状**：全仓对 `Clipboard|IDataObject|DataObject|DataFormats|GetImage|SetImage|ContainsImagePath|ImagePathScanner.` 零命中（仅 `ImagePathScanner.cs` 自身定义）。唯一粘贴处理是 `EditorHtml.cs:231-236`：`preventDefault()` → `getData('text/plain')` → `execCommand('insertText')`，**只粘纯文本、图片静默丢失**，且正是需求禁止的「回落浏览器默认粘贴」。`ImagePathScanner.cs:24-154` 规则已实现且比 Android 更完整（盘符 `C:\` / UNC `\\server\` / 含空格 / 多路径混排 / 存在且非目录），**但无调用方 = 死代码**。「插入图片」按钮链路（`AttachmentStore.cs:36-60` + `RichTextEditor.cs:562-590`）已可用。
- **要做**：C#/WebView2 层拦截粘贴，实现五分支（文件 URL / 位图 DIB-PNG-Bitmap / HTML 内联 `<img src="data:…">` / 路径文本接 `ImagePathScanner` / RTF 内联图）；落盘 `attachments\<noteUUID>\<UUID>.<ext>` 正斜杠 + 480px 显示，落盘后解码校验、失败转 PNG 重试；补 `paste-debug.log` 与剪贴板格式清单；失败弹「操作失败」对话框，**绝不回落默认粘贴**；补选区纯图片复制写 PNG、`Ctrl+Shift+V` 纯文本粘贴、拖拽图片。
- **验收**：12.3 五类粘贴全部可识别并落盘；不可识别时有可见错误；粘贴超大图（>10 MB）界面不卡死。
- **落点**：新增 `Tonote.App/RichText/ClipboardImageImport.cs`；改 `Tonote.App/RichText/EditorHtml.cs:231-236`、`RichTextEditor.cs:562-590`；接入 `Tonote.Core/Html/ImagePathScanner.cs`
- **依赖**：W-19（统一错误上报）

#### W-16 表格编辑稳定性（P0 组）

- **维度**：功能缺失 · **优先级**：P0 · **需求**：PC-T-06、PC-T-09、PC-T-10、PC-T-11、PC-T-13、PC-T-15、11.2 #5、12.3
- **现状**：编辑器**直接操作 DOM、绕过 Core 表格模型**（`RichTextDocument.cs:291-344` 的上限与沿用样式逻辑完全没被调用）。`EditorHtml.cs:333-358` 增删行列无 100×32 上限检查、无提示 / `table-debug.log`，`removeRow/removeColumn` 静默 `return`（:361,372）；`:378-383` `removeTable` 无条件删、无「选区完整覆盖整表」结构保护；`:52` 只有 `p,div{min-height:1.3em}`，无 `<td>` 点击落点 / `td>p{min-height:1.75em}`；`:81-86` 无 `word-break:keep-all`（中文撑宽）；无 `visualViewport` 光标滚入可视区；`:162-189` 嵌套表格未过滤 / 拍平。
- **要做**：DOM 命令前先走 Core 模型校验（上限、沿用相邻行列对齐与背景色）；撞上限给可见反馈 + 轻提示 + 蜂鸣 + `table-debug.log`；删表加选区完整性保护；补 `<td>` 点击置光标到格内末尾、空格子 1.75em、中文 `keep-all`、窗口缩小输入时滚入可视区、嵌套表格拍平；增删列后置 `data-widths = nil`。
- **验收**：12.3 表格用例全通过 —— 6×8 插入、逐格点击聚焦（含最后一格）、中文输入撑宽、Tab 跳格、末格 Tab 加行、100×32 上限反馈、整表删除、无邻格聚焦 / 无逐字竖排 / 无静默失败。
- **落点**：`Tonote.App/RichText/EditorHtml.cs:52,81-86,162-189,320-405`、`Tonote.App/RichText/RichTextEditor.cs:354-439`
- **依赖**：W-08

#### W-17 列表渲染与编辑交互

- **维度**：两者兼有 · **优先级**：P0 · **需求**：PC-LI-01~09、8.3、5.17 `Tab/Shift+Tab`、11.2 #16
- **现状**：**HTML 编码是对的**（`RichTextHtmlCodec.cs:22-47,215-236` 只写 `<li data-level="n">`，标记不进内容，PC-LI-07 满足）；但 `EditorHtml.cs:74-76` 只有 `ul,ol{margin:6px 0}`、`li{line-height:1.3}`、`li::marker{color:#2E7CF6}`，用浏览器默认 marker —— **无 CSS counter、无 `li[data-level]` 缩进、无 `1.→a.→i.`、无父实心/子空心圆点、无 `4+level×32`、无编号顺延**；`:237-252` keydown 只有 `Ctrl+B` 与表格内 Tab，列表层级 / 退格退出 / 回车继承 / 单元格内禁用列表全无。
- **对照**：Android `EditorScreen.kt:665-686,681-686`、Mac `RichTextBridge.swift:244-316`。
- **要做**：CSS 补 counter 与 `data-level` 缩进、蓝色实心 / 空心圆点；JS 补 Tab/Shift+Tab 升降级（0~3 钳制）、隔 1 段正文的编号顺延、退格退出、回车继承与空项二次回车退出、单元格内禁用列表守卫；项间无额外外边距（PC-LI-09）。
- **验收**：12.3 列表用例「1/2/3 级有序与无序混排、Tab 升降级、退格退出、回车继承、编号顺延」与 Mac 一致。
- **落点**：`Tonote.App/RichText/EditorHtml.cs:74-76,237-252`、`Tonote.App/RichText/RichTextEditor.cs:163-164,241-243`
- **依赖**：W-16（同一编辑器 JS）

---

### 批次 3 · P0 安全与平台

#### W-18 链接协议白名单与外链行为

- **维度**：两者兼有 · **优先级**：P0（安全）· **需求**：PC-E-07、9.3、PC-F-08
- **现状**：`RichTextEditor.cs:335-352` `LinkPopover` 无「显示文字（可留空）」、无预填 `https://`、无「请输入有效的链接地址」、**无协议白名单**；`EditorHtml.cs:314-319` `insertLink` 直接拼接未转义 URL → `javascript:` 可注入；`WebView2Host.cs:88-119` 未注册 `NavigationStarting`，JS 无 `<a>` click 拦截 → 点击链接在 WebView 内导航，无 Ctrl+点击系统浏览器打开。
- **要做**：仅允许 `http/https/mailto`；补对话框字段与非法提示；URL 转义；`NavigationStarting` 拦截并改为系统浏览器打开。
- **验收**：输入 `javascript:alert(1)` 被拒绝且不写入 HTML；点击外链不在编辑器内导航；9.3 安全条款通过。
- **落点**：`Tonote.App/RichText/RichTextEditor.cs:335-352`、`EditorHtml.cs:314-319`、`WebView2Host.cs:88-119`
- **依赖**：无

#### W-19 「绝不静默失败」统一错误上报

- **维度**：逻辑不一致 · **优先级**：P0 · **需求**：PC-E-09、PC-T-06、8.5、12.6 第 2 项
- **现状**：大量静默 `return` —— `RichTextEditor.cs:557`（编辑器未就绪）、`:342`（链接空）、`:305`（色值非法）、`:576`（未选文件）、`:679`（插图无 TopLevel）、`EditorHtml.cs:361,372`（表格删行/列）、`:231-236`（粘贴静默丢图）；插图失败只有 banner（`:588`），无统一「操作失败」对话框。
- **要做**：统一 `ReportFailure(title, reason, context)` → 可见对话框 + 诊断日志（不含 token / 正文）；把所有静默 return 替换为弹错；`paste-debug.log` / `table-debug.log` 落地；表格撞上限见 W-16。
- **验收**：8.5 每一条失败路径都有可见反馈；12.6 第 2 项不再命中。
- **落点**：新增 `Tonote.App/Dialogs.cs` 统一入口；改 `RichTextEditor.cs`、`EditorHtml.cs`
- **依赖**：W-15、W-16 的错误分支接入

#### W-20 全局快捷键全集

- **维度**：功能缺失 · **优先级**：P0/P1 · **需求**：5.17（17 行）、PC-B-06、PC-L-04、里程碑 M2
- **现状**：全 App 层仅 4 处 KeyDown（`Dialogs.cs:155,370`、`ShellView.cs:123,398`），JS 仅 `Ctrl+B` + 表格 Tab。缺 `Ctrl+N / F / K / I / U / Shift+S / Shift+V / W`、`F5`、`Delete`、`Esc`（浮层 / 对话框 / 日历）、`Ctrl+V/C` 自定义链路、列表 `Tab/Shift+Tab`、`Backspace` 列表项首、`Ctrl+点击`；`Ctrl+B/I/U` 未与工具栏状态同步；`Ctrl+Z/Y` 依赖 WebView 原生，「图片 / 表格操作可撤销」未验证。
- **要做**：`MainWindow` / `ShellView` 级全局 KeyDown 分发；编辑器内由 JS + C# 桥接；工具栏状态同步；Esc 统一关闭浮层 / 对话框 / 日历。
- **验收**：5.17 逐行通过；Ctrl+B/I/U 与工具栏高亮一致；撤销 / 重做覆盖图片与表格。
- **落点**：`Tonote.App/MainWindow.axaml.cs`、`ShellView.cs`、`RichTextEditor.cs`、`EditorHtml.cs`、`Dialogs.cs`
- **依赖**：W-15（Ctrl+V/C）、W-17（列表键位）

#### W-21 登录安全与登录态路由补强

- **维度**：两者兼有 · **优先级**：P0 · **需求**：PC-A-01/03/08/10、12.6 第 5 项
- **现状**：`BaiduLogin.cs:75` 条件是 `if (PendingState != null && !Equals(...))` → **pending state 为空时跳过校验**直接换 token；`Start():23-49` 每次点击覆盖 `PendingState`、登录中按钮未禁用；`LoginView.cs:167-173` 凭证缺失时**两个登录按钮未禁用**，文案与需求统一文案不符；`LoginView.cs:186` 提供需求外旁路「先使用本机笔记」→ `MainWindow.axaml.cs:39,32` 直接 `ShowShell()` 并 `StartSync()`，未登录启动同步必然失败；错误文案缺「（state 不匹配）」。
- **要做**：state 无条件校验 + 正确错误文案 + 并发保护（登录中禁用两个按钮、不覆盖 pending state）；凭证缺失禁用按钮并统一文案「尚未配置 AppKey / AppSecret，请先在 baidupan.secret.json 中填写」；移除本地模式旁路，登录入口改为「查看使用说明」；`MainWindow` 仅在 `IsSignedIn` 时 `StartSync()`。
- **验收**：重复回调 / 并发点击被拒绝；删凭证文件启动按钮禁用；未登录不再触发同步失败；12.6 第 5 项不命中。
- **落点**：`Tonote.App/Auth/BaiduLogin.cs:23-49,75-80`、`Tonote.App/LoginView.cs:137-190`、`Tonote.App/MainWindow.axaml.cs:25-39`
- **依赖**：W-23（协议注册部分）

#### W-22 窗口关闭 / 失活 / 关机 flush

- **维度**：功能缺失 · **优先级**：P0 · **需求**：PC-N-06、PC-E-10、PC-W-07、7.9-4、12.5 强杀
- **现状**：`MainWindow.axaml.cs:59-64` 只 `StopPolling + Dispose`，无 Activated/Deactivated/SessionEnding 监听；编辑器约 850ms 落盘（`EditorHtml.cs:211-214` + `RichTextEditor.cs:502-547`），切换笔记有 Flush，但**关窗未 Flush**；失活 / 最小化 / 关机 / 注销全部无 flush。
- **要做**：窗口激活时校验 + 同步、失活 / 最小化 / 关闭 / `SessionEnding` 前 flush 待上传（最多等 3 秒）与编辑器内容；`Ctrl+W / Alt+F4` 走同一路径。
- **验收**：编辑中强杀进程重启数据完整（最多丢 1 秒输入）；关机注销不丢未上传改动。
- **落点**：`Tonote.App/MainWindow.axaml.cs:43-64`、`AppSession.cs`、`RichTextEditor.cs:502-547`
- **依赖**：W-12（flush 队列）

#### W-23 安装包、单实例置前与 Windows 平台化

- **维度**：功能缺失 · **优先级**：P0 · **需求**：PC-W-01/02/04/07、PC-N-07、PC-A-04、12.1 #1、里程碑 M3
- **现状**：`scripts/publish-windows.sh:5-9` 只有 `dotnet publish -r win-x64 --self-contained`；协议注册靠运行时写 HKCU（`AppInstance.cs:33`）；`AppInstance.cs:77-89` 收到回调只 `Send`，**不把已有窗口置前**；无 MSI/Inno/NSIS 安装包、无开始菜单 / 桌面快捷方式、无卸载保留数据询问；无 WebView2 Evergreen Bootstrapper 静默安装；`app.manifest` 无 `dpiAware` / Per-Monitor V2；无自动更新。
- **要做**：按用户安装的安装包 + 安装期注册 `tongnote://` + 快捷方式 + 卸载保留数据询问；WebView2 Evergreen 静默安装；单实例回调置前窗口；`app.manifest` 补 DPI 声明；收 `WM_QUERYENDSESSION`（与 W-22 合流）。
- **验收**：12.1 #1 干净机器安装启动无报错；浏览器授权点「打开同笔记」回到已运行窗口且不新开第二个；100% / 150% / 200% 缩放视觉走查通过。
- **落点**：`scripts/publish-windows.sh`、`Tonote.App/AppInstance.cs:20-89`、`Tonote.App/ProtocolRegistration.cs`、`Tonote.App/app.manifest`
- **依赖**：W-21、W-22

---

### 批次 4 · P1 功能补齐

#### W-24 回收站搜索生效与清空控件

- **维度**：逻辑不一致 · **优先级**：P1（需求点名「PC 修正」）· **需求**：PC-L-04、第 10 章
- **现状**：`ShellView.cs:1001-1007` 显式写 `if (_pane != Pane.Trash && …)` → **回收站里搜索框可见但无效**，正是需求要求 PC 修正的 Mac 缺陷，Windows 照抄了；Watermark「搜索标题或正文」≠「搜索笔记、标签…」；无 `×` 清空；`Ctrl+F` 缺。
- **要做**：去掉 `_pane != Pane.Trash` 条件、补 `×` 清空与 `Ctrl+F`、Watermark 对齐附录 A。
- **验收**：回收站内输入标题 / 标签能过滤；点击 `×` 清空；`Ctrl+F` 聚焦搜索框。
- **落点**：`Tonote.App/ShellView.cs:38,1001-1007`
- **依赖**：W-20

#### W-25 侧栏真实昵称与账号信息失败静默

- **维度**：两者兼有 · **优先级**：P1 · **需求**：PC-B-04、PC-A-13、PC-SE-03、第 10 章
- **现状**：`ShellView.cs:742-768` 侧栏顶部只有应用名 + 头像 + 同步文字，**无真实昵称、无「已保存到本地」绿点、无账号区齿轮**（齿轮退化成独立「设置」文字按钮 :771）；需求第 10 章明确要求把 Mac 硬编码的「老王的工作台」改掉，Windows 没做任何替代。`Dialogs.cs:522-537` 读取网盘信息失败时显示「读取网盘信息失败：…」，而需求 / Mac `SyncView.swift:201` 要求「失败静默，显示『已授权』与 — / —」。
- **要做**：侧栏宽档补头像 + 真实昵称 + 绿点 + 齿轮；账号信息失败静默并显示「已授权」与 `— / —`。
- **验收**：附录 A 文案一致；断网时设置面板不出现错误文本。
- **落点**：`Tonote.App/ShellView.cs:742-771`、`Tonote.App/Dialogs.cs:522-537`
- **依赖**：无

#### W-26 删除后选中下一条与附件引用检查

- **维度**：逻辑不一致 · **优先级**：P1 · **需求**：PC-N-03/05、PC-I-08、PC-S-09、12.6 第 2 项
- **现状**：移入回收站 / 恢复有回退（`ShellView.cs:1079-1096`），但**彻底删除后**（`:1098-1110`、`:965-970`）不选中下一条，`_selected` 仍可能指向已删笔记 → 空白编辑器。`NoteStore.cs:190-198` `PermanentlyDelete` 无条件 `RemoveAttachments(id)`，`AttachmentStore.cs:82-96` 直接 `Directory.Delete(recursive:true)`；而 `ApplySyncedNote`（`:85-88`）**已做**「其它未 purged 笔记是否引用」检查 → 两篇共用一个 `attachments/<id>/` 时彻底删除一篇会误删另一篇的图。
- **要做**：彻底删除与右键彻底删除统一走回退选中的 `selectFirstNote`；`PermanentlyDelete` 复用 `ApplySyncedNote` 的引用检查。
- **验收**：回收站彻底删除后编辑器自动显示下一条；两篇共用附件目录时删一篇另一篇图片仍可见。
- **落点**：`Tonote.App/ShellView.cs:965-970,1079-1110`、`Tonote.Core/Storage/NoteStore.cs:190-198`
- **依赖**：无

#### W-27 任务备注双态、展开冻结与「文字[图片]」预览

- **维度**：两者兼有 · **优先级**：P1 · **需求**：PC-TD-05/08/09/13/15/16、D.1-18
- **现状**：`ShellView.cs:566-615,728-739` 备注是**纯文本 TextBox**（MinHeight 80 非 120），保存时 `RebuildTodoNotes` 重新包 `<p>` **会丢原 HTML 格式**；「编辑/保存」不在截止区同一行；保存后不清 `_expandedTodo`，查看态无 90/320 固定高度，无「保存并收起」；`:480-564` 任务行只有标题，**无「文字[图片]」预览**、无「最近修改 yyyy-MM-dd HH:mm」；`:480-509` 无 `frozenIDs`，展开期间列表顺序随排序变化；无 0.15s 折叠动画；年步进 `AddYears(±1)` 无 2000–2100 钳制；日历无「已选 yyyy年M月d日」。
- **对照**：Mac `TodoView.swift:17,394,410-417,487,574`；Android 已实现「文字[图片]」。
- **要做**：备注改富文本双态（查看 / 编辑），保存不再重包 HTML；展开冻结顺序、保存解冻并收起；任务行补「文字[图片]」预览与最近修改时间；截止区同行「编辑 / 保存」；补折叠动画、年钳制与日历已选文案；任务保存 → `.todo(id)` 即刻上传、离开 → `.dirtyTodos`（与 W-13 合流）。
- **验收**：12.4 X12（Android 带图任务在 PC 展开可见、列表显示「文字[图片]」）；展开编辑期间列表不跳动；保存不清空 HTML 格式。
- **落点**：`Tonote.App/ShellView.cs:446-470,480-659,670-739`、`Tonote.App/Dialogs.cs:170-268`
- **依赖**：W-13

#### W-28 设置面板补齐（含 PC 新增项）

- **维度**：功能缺失 · **优先级**：P1 · **需求**：PC-SE-01/03/04/05/06/07、PC-A-12、PC-SY-05
- **现状**：`Dialogs.cs:374-417` 四态徽标 / 最近同步 / 变更数已做，但「立即同步」按钮固定文案、无「同步范围 笔记 · 任务」与「按篇存在 /apps/tong_log/notes/」副文案；缺「授权方式 百度网盘开放平台 OAuth 2.0」、**重新授权入口**、失败兜底「已授权」；设备区缺说明文案与「刷新中…/正在下线…/未命名设备」；配额文案≠「已用空间 · 以百度网盘为准」；缺页脚「同笔记 · 数据属于你自己 / 无需订阅 · 无服务器存储」；**PC-SE-07（本机数据目录、在资源管理器打开、版本号、一键复制诊断信息）整项全无**；侧栏「N 项待上传」指示与同步中旋转反馈缺。
- **要做**：按附录 A 逐条补文案与入口；补诊断信息（严格排除 token / secret / 笔记正文）；接 `SyncEngine` 的 dirty 计数。
- **验收**：PC-SE-01~07 逐条；诊断信息复制后不含任何凭证。
- **落点**：`Tonote.App/Dialogs.cs:374-537`、`Tonote.App/ShellView.cs:205-213`、`Tonote.Core/Sync/SyncEngine.cs`（暴露 dirty 计数）
- **依赖**：W-12、W-25

#### W-29 使用说明改随包 guide 且未登录可达

- **维度**：两者兼有 · **优先级**：P1 · **需求**：PC-A-14、11.2 #15、里程碑 M4
- **现状**：`Dialogs.cs:57-63,582-620` 是内建结构化文本窗口（**非**随包 `guide/index.html` + 6 图 + WebView2）、**非单例**（每次 new）、无「没有找到使用说明…」文案；唯一入口 `:499` 在设置面板 → **未登录不可达**，而需求登录页要求「查看使用说明」。
- **要做**：改 WebView2 加载随包 guide、单例、登录页可达、缺文件文案；顺带删掉 `Dialogs.cs:600` 的「1.5 秒防抖」旧表述（W-13）。
- **验收**：未登录点「查看使用说明」能打开；重复点击不叠窗；6 张图可见。
- **落点**：`Tonote.App/Dialogs.cs:57-63,499,582-620`、`Tonote.App/LoginView.cs:137-190`、`scripts/publish-windows.sh`（打包 guide）
- **依赖**：W-21、W-23

#### W-30 登录页品牌与状态补齐

- **维度**：功能缺失 · **优先级**：P1 · **需求**：PC-A-09/10、附录 A
- **现状**：`LoginView.cs:41-190` 缺页脚「/apps/tong_log」、缺「记录 · 同步 · 属于你」小标、副标缺「从此告别…焦虑」、主按钮为 Stretch 非 340×48、次按钮缺两行跳转说明、缺「查看使用说明」入口；多出需求外的「先使用本机笔记」；无「正在授权…」状态。
- **要做**：按附录 A 与 Mac 设计稿逐条补；登录中禁用按钮并显示状态（与 W-21 合流）。
- **验收**：登录页 8 视图对照通过；两个按钮在授权中均禁用。
- **落点**：`Tonote.App/LoginView.cs:41-190`
- **依赖**：W-21

#### W-31 富文本工具栏与色板

- **维度**：两者兼有 · **优先级**：P1 · **需求**：5.9、PC-F-02/06/07、PC-E-04、第 10 章
- **现状**：`RichTextEditor.cs:147-175` 是 **17 个按钮**（需求 16 项），把有序 / 无序列表做成独立按钮且 T 浮层里也有，形成重复入口；`:258-259` 把 `value == "15"` 写死为当前字号，**照抄了 Mac「误把 15 标勾」的缺陷**（需求第 10 章明确 PC 应修正为勾选实际字号，正文默认 14）；`:37-47` 文字 / 背景各 8 色且色值与需求的 9 色不符、无真取色器；`:228-248` 切样式只调 `formatBlock`，无「切正文/引用/代码块/列表清粗体、字号重置、切标题强制加粗」清理规则；「单元格对齐」按钮无「仅光标在表格内」启用条件；`ShellView.cs:253` 字数只在选中笔记时赋值一次，编辑中不刷新（`Note.cs:101` 的 `WordCount` 算法本身正确）。
- **要做**：工具栏收敛为 16 项；字号勾选改读实际字号（默认 14）；色板改 9 色 + 自定义取色器；补切样式清理规则与表格内启用条件；字数统计实时刷新。
- **验收**：5.9 逐项；字号勾选随光标变化；编辑中字数实时更新。
- **落点**：`Tonote.App/RichText/RichTextEditor.cs:37-47,147-175,228-259,406-439`、`Tonote.App/ShellView.cs:253`
- **依赖**：W-17

#### W-32 表格 P1 交互

- **维度**：功能缺失 · **优先级**：P1 · **需求**：PC-T-07/08/14/16、12.3、里程碑 M4
- **现状**：无「常驻小灰圆 → hover 蓝色实心圆白加号 + 引导线」边沿手柄（需求允许首版用工具栏浮层兜底，已有，故 P1）；无单元格右上角 16px 下箭头与 248 宽菜单（20 色 / 满宽复选 / 删行列 / 删表），Windows 把 20 色塞进 168 宽的 `AlignPopover`；增删行列不沿用相邻行对齐与背景色（Core 模型有但编辑器不用）；增删列后 `data-widths` 不置 nil；22×50 大表格性能未优化 / 未验证。
- **要做**：补边沿手柄（几何与 Mac `RichTextTableGutterView` 对齐）、单元格下拉菜单、增删沿用样式、`data-widths` 清理、大表格性能 <100ms。
- **验收**：12.3 / 12.5 表格用例；22×50 输入响应 <100ms。
- **落点**：`Tonote.App/RichText/EditorHtml.cs:320-405`、`RichTextEditor.cs:354-439`
- **依赖**：W-16

#### W-33 头像选择器与云同步状态

- **维度**：两者兼有 · **优先级**：P1 · **需求**：PC-B-05、PC-AV-03、12.4 X8
- **现状**：`Dialogs.cs:50-55,271-304` 用模态 `Window.ShowDialog` 而非 244 宽 popover、WrapPanel 非严格 4 列网格、选中用蓝框非打勾、**底部无「更换后自动上传 / 正在上传… / 已同步 / 上传失败（已保存到本地）」**；`AvatarStore.cs:52-54` 已有 `CloudState / CloudError` 但 UI 完全没消费。8 预设 id 与本地立即生效、仅本地从未选择才采纳云端都已对齐。
- **要做**：改 popover、4 列网格、选中打勾；UI 消费 `CloudState` 显示 4 段状态。
- **验收**：12.4 X8（PC 换头像后 Mac / Android 一致）；断网时显示「上传失败（已保存到本地）」。
- **落点**：`Tonote.App/Dialogs.cs:50-55,271-304`、`Tonote.App/ShellView.cs:749-763`
- **依赖**：W-28

#### W-34 窗口状态记忆与侧栏细节

- **维度**：功能缺失 · **优先级**：P1 · **需求**：PC-N-02、PC-B-02/03/09/10、里程碑 M4
- **现状**：`MainWindow.axaml:6-10` 固定尺寸 + `CenterScreen`，无位置 / 尺寸 / 最大化记忆与多显示器回可见区；`ShellView.cs:1155-1156` hover 是整条 6px handle 变绿（Mac 是 2px 绿线 + 8×20 白块断开），Windows 白块 16×18 且无 Tab 聚焦 / 空格键盘可达；`:1287-1288` 极窄档用系统 `ToolTip`（有延迟、无 `+12,+16` 定位、窄档也加），而 Mac 明确用自定义即时浮窗；回收站三档无「图标 / 横排 / 竖排」三态；侧栏无「本地存储 / N 篇笔记 / 本地」计量。
- **要做**：窗口几何持久化 + 多显示器边界校验；侧栏 hover 改 2px 绿线 + 8×20 白块、键盘可达、自定义即时浮窗、三态回收站按钮、存储计量。
- **验收**：PC-N-02、PC-B-02/03/09/10 逐条；150% / 200% 缩放浮窗定位正确。
- **落点**：`Tonote.App/MainWindow.axaml(.cs)`、`Tonote.App/ShellView.cs:70-75,834-836,1140-1195,1287-1288`
- **依赖**：W-23

#### W-35 对话框串行化（单一 `activeDialog`）

- **维度**：功能缺失 · **优先级**：P1 · **需求**：PC-B-11、8.5
- **现状**：`Dialogs.cs:18-25` 每次 `new ConfirmDialog(...).ShowDialog(owner)`，无单一 `activeDialog` 状态机；Mac `SidebarView.swift:22`、`EditorView.swift:36`、`TodoView.swift:23-24` 都因「多个 `.alert` 串联只有一个生效」而显式用单一状态规避。连续触发两个删除确认可能叠窗 / 丢窗。
- **要做**：引入单一 `activeDialog` 状态（删除笔记 / 删除任务 / 下线设备 / 图片失败 / 操作失败等枚举），统一分发与关闭。
- **验收**：连续快速触发两种确认不叠窗、不丢窗；Esc 关闭当前对话（与 W-20 合流）。
- **落点**：`Tonote.App/Dialogs.cs:18-25`、`Tonote.App/ShellView.cs`、`Tonote.App/RichText/RichTextEditor.cs`
- **依赖**：W-19

#### W-36 失败重试、回前台校验与设备列表补行

- **维度**：功能缺失 · **优先级**：P1 · **需求**：PC-SY-04、PC-D-03/05、PC-D-07
- **现状**：`AppSession.cs:51-68` 有 45 秒 `PeriodicTimer`，但 `:63` `catch { }` 吞掉失败后**无 10 秒内重试一次**；登录后调 `BeginLoginAsync`（覆盖写）而非 `ensureNotKicked`，无窗口回前台 / 应用激活监听；`DeviceSession.cs:168-172` 定义了 `LocalSnapshot()` 但 `ListDevicesAsync:227-268` 未调用，`Dialogs.cs:541-550` 在 `records.Count == 0` 时直接显示「暂无设备记录」；`DeviceSession.cs:204-211` 把 NotFound 当 `remote = null` → `Publish` 覆盖写，若 NotFound 源自 W-01 的误抛会把 `kicked:true` 覆盖成 false（自我解绑）。
- **要做**：轮询失败 10 秒内重试一次；应用打开 / 窗口回前台调 `ensureNotKicked` 且提示「本机已在其他设备被下线，请重新登录」；设备列表用 `LocalSnapshot()` 补本机行；网络错误不得误清下线标记（依赖 W-05 强类型分类）。
- **验收**：PC-SY-04（断网恢复 ≤10 秒补传）、12.4 X7（Mac 下线 PC 后回登录页且提示正确、本地笔记不清空）。
- **落点**：`Tonote.App/AppSession.cs:51-95`、`Tonote.App/MainWindow.axaml.cs`、`Tonote.Core/Sync/DeviceSession.cs:168-268`、`Tonote.App/Dialogs.cs:541-550`
- **依赖**：W-05、W-12、W-22

---

### 批次 5 · P1 可靠性 / 安全

#### W-37 令牌 DPAPI 加密与诊断信息脱敏

- **维度**：功能缺失 · **优先级**：P1 · **需求**：PC-A-07、9.3、12.6 第 4 项、11.2 #13
- **现状**：`BaiduPanTokenStore.cs:33-38` 用 `TonoteJson.WriteAtomic` **明文**写 `baidupan-token.json`，且 `if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(...)` —— **Windows 分支不做任何权限收紧**，注释还自称「非 Windows 上限制」，与目标平台相反；全仓 `DPAPI|ProtectedData` 零匹配。
- **要做**：`ProtectedData.Protect` + `DataProtectionScope.CurrentUser`；文件 ACL 仅当前用户；诊断日志 / 崩溃报告严格排除 token / secret / 笔记正文（与 W-28、W-38 合流）。
- **验收**：12.6 第 4 项；复制诊断信息后全串检索无凭证。
- **落点**：`Tonote.Core/Baidu/BaiduPanTokenStore.cs:16-38`
- **依赖**：W-28、W-38

#### W-38 损坏文件备份与调试日志滚动

- **维度**：功能缺失 · **优先级**：P1 · **需求**：6.1、9.2、9.5、11.2 #13
- **现状**：`NoteStore.cs:259-271`、`TodoStore.cs:155-167`、`SyncRules.cs:218-231`、`NotebookStore.cs:62-87`、`DeviceSession.cs:101-115`、`BaiduPanTokenStore.cs:16-31`、`AvatarStore.cs:116-127` 全是 `catch { 回退空集合 }`，全仓 `corrupt` 零匹配；`TonoteJson.cs:20-29` 原子写已对齐但无备份环节、无失败重试 3 次；无 `sync-debug.log` / `paste-debug.log` / `table-debug.log`（每轮 scope / 请求数 / 耗时 / 失败原因）、无 2MB×3 滚动。
- **要做**：统一 `Deserialize` 包装，损坏先备份 `*.corrupt-<时间戳>` 再回退；原子替换失败重试 3 次 + 可见错误；新增 `SyncDebugLog` 与 3 类调试日志，2MB×3 滚动，脱敏。
- **验收**：手工写坏 `notes.json` 后重启，原文件被备份、未被空集合覆盖；日志滚动且不含凭证 / 正文。
- **落点**：`Tonote.Core/Json/TonoteJson.cs:20-45`、各 `*Store.cs`、`Tonote.App/RichText/`、新增 `Tonote.Core/SyncDebugLog.cs`
- **依赖**：W-01、W-15、W-19

#### W-39 `client_secret` 改 POST body

- **维度**：逻辑不一致 · **优先级**：P1 · **需求**：9.3、第 10 章
- **现状**：`BaiduPanClient.cs:64-77` `RequestTokenAsync` 把全部参数拼进 URL 后 `http.GetAsync(url)`；`ExchangeCodeAsync:53-60` 把 `client_secret` 放进该参数表 → `client_secret` / `code` / `access_token` 出现在 GET query（代理日志、Referer、浏览器历史泄露面）；源码与文档均无「已知风险」标注。
- **注意**：`PostFormAsync:546-566` 的 precreate / create / delete / filemanager 业务参数在 body、`method/opera/access_token` 在 query，与 7.8 契约及 Mac 实现一致，**这部分不构成偏差，不要改**。
- **要做**：令牌交换改 POST body（若百度接口支持）；若不支持，维持现状并在代码与需求文档标注已知风险。
- **验收**：抓包核对 `client_secret` 不在 URL；或文档有明确风险标注。
- **落点**：`Tonote.Core/Baidu/BaiduPanClient.cs:53-77`、`docs/PC版需求文档.md` 第 10 章
- **依赖**：无

#### W-40 JSON 键排序与本机文件名契约（低优先，可并批处理）

- **维度**：逻辑不一致 · **优先级**：P2 · **需求**：6.1
- **现状**：`TonoteJson.cs:31-45` 有 `WriteIndented`、ISO8601、`UnsafeRelaxedJsonEscaping`、大写 Guid，**但无键排序**（按属性声明顺序输出）；Mac `SyncRules.swift:260` 用 `[.prettyPrinted, .sortedKeys]`。例：`SyncRules.cs:9-22` 的 `SyncCursor` 顺序非字典序。另 6.1 约定 `settings.json` / `notebooks.json`，Windows 实为 `last-sync.json` / `avatar-state.json` / `notebook-state.json`，语义一致但文件名契约不同。
- **说明**：`Note.cs` / `Todo.cs` 属性恰好声明成字母序，**云端 JSON 不受影响**，仅本机文件 diff 噪音。
- **要做**：`TonoteJson` 补键排序；文件名契约要么对齐要么在需求文档标注为 PC 差异。
- **验收**：同一份数据重复写盘 diff 为空。
- **落点**：`Tonote.Core/Json/TonoteJson.cs:31-45`
- **依赖**：W-38

---

### 批次 6 · P1/P2 文案与视觉统一

> 本批次按用户要求**不作独立维度**，只保留会影响验收判定或与需求硬文案冲突的项；纯视觉走查放到 M4 人工验收。

#### W-41 文案族（附录 A 逐条对齐）

- **维度**：逻辑不一致 · **优先级**：P1 · **需求**：PC-TD-01/02/06/10、PC-R-05、PC-E-05、PC-L-02/08/11、PC-SE-05、附录 A
- **现状（举例）**：任务页页头写「快任务」应为「任务」（`ShellView.cs:345`）；输入占位「添加一条快任务，回车即可创建」≠「添加任务，回车即可创建…」；统计串无条件显示「终止 N · 逾期 N」应为条件显示 `[· 已终止 N] [· 逾期 N]`（`TodoStore.cs:31-32`）；删除确认「「X」将被永久删除，并会从其它设备的任务列表里移除。」+「删除」≠ 需求「…其他已同步设备上也会一并移除，此操作不可恢复。」+「永久删除」；笔记删除确认标题「移入回收站？」≠「删除笔记？」且传 `danger:false`（绿色确认按钮）；彻底删除文案、右键「移入回收站」≠「删除笔记」、配额文案、页脚文案、快同步卡副标题「打开这篇独立笔记」≠「多设备共享 · 一处编辑处处可见」均不符。
- **要做**：按 `PC版需求文档.md` 附录 A 逐条替换，并从设计稿 / Mac 源码取原文；删除类确认统一 `danger:true`。
- **验收**：附录 A 逐条人工核对通过；12.6 之外无文案类验收失败。
- **落点**：`Tonote.App/ShellView.cs:345,383-405,874-878,958-987,1069-1087`、`Tonote.App/Dialogs.cs:457-481`、`Tonote.Core/Storage/TodoStore.cs:31-32`
- **依赖**：无

#### W-42 视觉与协议细节族（P2 合批）

- **维度**：逻辑不一致 · **优先级**：P2 · **需求**：PC-F-03、PC-TD-07、PC-L-07、PC-R-04、PC-E-06、6.3、PC-T-04、A5/A7/A8/A9
- **现状**：引用 `margin:8px` 应为段前后 6、代码块应为 4（`EditorHtml.cs:63-73`）；脚本在 `<body>` 而非 `<head>`（`:103-105`，Android 注释明确反对）；`hr` 进 JS 白名单但 Core 解码丢弃；`VisibleText.cs:35-45` 实体解码把 `&amp;` 放最后，与 Mac/Android 相反 → `&amp;lt;` 双重转义跨端不一致；`EditorHtml.cs:395` 左对齐写 `data-align="left"`（契约「左不写」）、「清除底色」写 `data-bg="#FFFFFF"` 而非移除属性、色板首项是「白」而非「无颜色」；`MarkdownConverter.cs:96-97` 连续行合并不加空格、行内 `` `代码` `` 生成 `font-size:13px` 而 Android 生成 `<code>`；回收站排序用 `UpdatedAt` 而非 `deletedAt`（`ShellView.cs:1012`）；`_metaRow.IsVisible = !IsQuickSync` 使回收站元数据行仍显示；红描边 1px 应为 0.5px、待完成状态圈红色应为灰色 `#C7C7CC`；笔记卡片缺 1pt `#EFEFF2` 发丝线、右键菜单缺分隔线与红字。
- **要做**：按上表逐条校正；`hr` 要么进 Core 白名单要么从 JS 移除；实体顺序统一为 `&amp;` 最先。
- **验收**：12.3 / 12.4 X1·X2 视觉对照通过；`Encode(Decode(html))` 归一化后三端一致。
- **落点**：`Tonote.App/RichText/EditorHtml.cs:63-73,91-117,191-209,395`、`Tonote.Core/Html/VisibleText.cs:35-45`、`Tonote.Core/Html/MarkdownConverter.cs:96-97,220-231`、`Tonote.App/ShellView.cs:242,919-950,1012`
- **依赖**：W-08、W-10

#### W-43 P2 增强（按排期，非上线阻断）

- **维度**：功能缺失 · **优先级**：P2 · **需求**：PC-W-03、14.3、PC-T-14 UI、PC-I-10、7.9-3、里程碑 M5
- **内容**：自动更新「提示 + 下载页」、ARM64、拖拽图片导入（W-15 已含可顺带做）、表格列宽拖拽 UI（`data-widths` 数据层已保留）、任务上传合并窗口、可选本地加密、深色模式（14.3 待议）、8 小时挂机与高 DPI 走查、LiveTests 故障注入集扩充。
- **验收**：按 14.3 与排期确认，不纳入 M1~M4 退出标准。
- **落点**：跨模块
- **依赖**：W-01~W-42

---

## 3. 批次与需求里程碑映射

| 文档里程碑 | 退出标准 | 对应本清单 |
|---|---|---|
| **M1 同步语义对齐（P0）** | PC-SY-01~04/06/08 通过；X4~X6 通过 | 批次 0（W-01~06）+ W-12、W-13、W-14、W-36 的重试部分 |
| **M2 编辑器稳定性（P0）** | 12.3 全通过 | 批次 1（W-07~11）+ W-15、W-16、W-17 + W-20 快捷键 |
| **M3 平台化（P0）** | PC-W-01~11 通过；干净机器安装验收 | W-18、W-19、W-21、W-22、W-23 |
| **M4 体验打磨（P1）** | P1 需求完成 80% | 批次 4（W-24~36）+ 批次 5（W-37~39）+ W-41 |
| **M5 增强（P2）** | 按排期 | W-40、W-42、W-43 |

---

## 4. 回归验收顺序

1. **每轮改动**：`dotnet build` 0 错误 → `dotnet test`（CoreTests + 新增故障注入 / 编解码单测）→ 编译产物重启 / 安装 → **用户手工验收**（项目约定不做自动化 UI 测试）。
2. **批次 0 完成后**：先跑 W-06 的 7 类故障注入，确认本地旧数据不覆盖云端，再动批次 1。
3. **批次 1 完成后**：跑 12.4 X1 / X2 / X3（三端互通），确认表格不退化、裸图不丢、Markdown 原稿不改。
4. **批次 2 完成后**：跑 12.4 X4 / X5 / X6 / X11 / X12 与 12.3 编辑器重点用例。
5. **批次 3 完成后**：跑 12.1 冒烟 8 条（含干净机器安装、粘贴截图、3×3 表格、任务保存即传）。
6. **上线前**：逐条核对 12.6 五条阻断项，全部不命中；12.5 性能与稳定性（1000 篇冷启动 ≤2s、22×50 表格 <100ms、8 小时挂机、弱网收敛、强杀）。

---

## 5. 审计证据来源

| 来源 | 覆盖范围 | 结论要点 |
|---|---|---|
| 同步与数据层审计 | `Tonote.Core/Baidu/`、`Sync/`、`Storage/`、`Models/`、`Json/`，对照 Mac `WorkLogCore` 与 Android `data/sync` | L1~L17 逻辑不一致 + F1~F10 功能缺失 + 已对齐 12 项 |
| UI 与业务覆盖审计 | `Tonote.App/` 全部 `.cs/.axaml`、README、`publish-windows.sh`，181 条编号逐条覆盖矩阵 | 约 50 已实现 / 70 部分 / 60 缺失；28 条逻辑不一致；P0/P1/P2 分级 |
| 富文本 / 表格 / 附件审计 | `Tonote.Core/Html/`、`Tonote.App/RichText/`、`tests/`，对照 Mac `WorkLogRichText` 与 Android `EditorScreen.kt` | A1~A10 协议不一致 + B1~B7 功能缺失 + 已对齐 18 项 |
| 需求文档 | `docs/PC版需求文档.md` 第 4~12 章、附录 A/B/C/D | 181 条编号、12.6 阻断项、M1~M5 里程碑 |

> 三路审计均为只读，未修改 `windows_tonote` 任何源码。文中标注「未逐行核实」的个别条目（如 PC-A-06 令牌「提前 5 分钟过期」、PC-D-03 设备排序、PC-S-04 冲突副本生成）不在本清单立项，执行到对应批次时顺手复核即可。

---

*相关文档：需求基准 `docs/PC版需求文档.md`；功能与行为基准 `docs/功能点文档.md`；协议与架构 `docs/设计文档.md`；视觉基准 `同笔记-Mac端UI设计.html`、`同笔记-App端UI设计.html`；Windows 现状 `windows_tonote/README.md` 与 `docs/windows_tonote-*.html`。*
