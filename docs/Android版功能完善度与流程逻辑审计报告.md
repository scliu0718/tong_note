# 同笔记 Android 版｜流程逻辑缺失与功能完善度审计报告

- 审计日期：2026-11-13
- 审计范围：`android_tonote/app/src/main/java/com/tong/log/app/` 全部 33 个 Kotlin 文件 / 4,251 行、`AndroidManifest.xml`、Gradle 配置；对照 `docs/功能点文档.md`（16 域 / 168 点）、`docs/设计文档.md`，并与 `mac_tonote/Sources/WorkLogCore/` 逐项比对。
- 方法：三路只读静态审计 + 关键结论人工复核 + 真机 `sync-debug.log` 与百度开放平台接口实测。
- 性质：只读审计，报告中所有「行号」以审计时点源码为准。文末「本轮修复」记录 2026-11-13 已落地的改动。

---

## 一、结论速览

**加权功能完成度约 56.1%**（有效功能点 140 项：完全覆盖 62、部分覆盖 33、未覆盖 45；28 项桌面专属交互标为「不适用」）。

Android 版的**数据层与同步内核完成度很高**：`Note`/`Todo` 12/11 字段、`contentFormat`、ISO8601 整秒日期、大写 UUID、按篇云端文件路径、附件相对路径正则、三方合并五态决策、4 MiB 分片上传、设备会话，均与 Mac 逐项对齐；还额外实现了 Mac 没有的 `filemetas` 下载直链兜底。登录、笔记列表、任务、我的、同步、回收站、设备、编辑器等**外壳页面齐备**。

落差集中在两处：

1. **编辑与组织能力被大幅简化**：编辑器从 AppKit TextKit 1 换成 WebView `contenteditable + execCommand`，表格单元格编辑、列表层级、字号菜单、受控粘贴链路等整域缺失（LI 14.3%、T 14.3%、E 31.8%、C 33.3%、F 40.0%）。
2. **大量业务层方法「已写但未接线」**：`NotebookStore.add/remove`、`NoteStore.setNotebook/addTag/removeTag/togglePin`、`TodoStore.setNotes/setPlannedAt`、`BaiduPanClient.quota/deleteRemote`、`ImagePathScanner`、`RichTextTableCodec.tableCss` 零调用；最严重的是 `SyncCursorStore.markDirty()` 与 `SyncEngine.schedule()` 全工程零调用。

真机日志（JEF-AN00，126 KB / 1,082 轮同步）把「远端有数据但手机空、上传不成功」的历史故障完整串起来：

| 阶段 | 日志证据 | 次数 |
|---|---|---|
| OkHttp response 被 `use { it }` 提前关闭 | `同步失败：IllegalStateException closed` | 28 |
| 上传参数放 form body | `precreate HTTP 400` | 6 |
| 下载直链偶发失效 | `下载笔记失败：下载失败 HTTP 404` | 51 |
| 取直链失败 | `文件不存在或无权下载` | 33 |
| 上传回读抖动被误判 | `连续三次没能写上云端` | 1 |

前两项已在上一轮修复；本轮针对后三项与「标脏链路」做了加固（见第六节）。

---

## 二、按功能域覆盖度

完成度 = (已覆盖 + 0.5 × 部分覆盖) ÷ (基准数 − 不适用)。

| 功能域 | 基准 | 已覆盖 | 部分 | 未覆盖 | 不适用 | 完成度 |
|---|---|---|---|---|---|---|
| A 账号与登录 | 13 | 9 | 2 | 1 | 1 | 83.3% |
| S 云同步 | 20 | 14 | 3 | 3 | 0 | 77.5% |
| D 设备会话与安全 | 8 | 4 | 2 | 2 | 0 | 62.5% |
| N 主界面与导航 | 7 | 3 | 1 | 0 | 3 | 87.5% |
| B 侧栏 | 11 | 4 | 2 | 1 | 4 | 71.4% |
| C 笔记本与标签 | 9 | 3 | 0 | 6 | 0 | 33.3% |
| L 笔记列表 | 12 | 2 | 6 | 2 | 2 | 50.0% |
| E 编辑器容器与元数据 | 12 | 3 | 1 | 7 | 1 | 31.8% |
| F 富文本格式 | 11 | 1 | 6 | 3 | 1 | 40.0% |
| LI 列表 | 9 | 0 | 2 | 5 | 2 | 14.3% |
| I 图片与附件 | 13 | 2 | 2 | 3 | 6 | 42.9% |
| T 表格 | 14 | 0 | 2 | 5 | 7 | 14.3% |
| R 回收站 | 5 | 4 | 1 | 0 | 0 | 90.0% |
| TD 任务 | 13 | 6 | 2 | 4 | 1 | 58.3% |
| AV 头像 | 4 | 2 | 0 | 2 | 0 | 50.0% |
| SE 设置面板 | 7 | 5 | 1 | 1 | 0 | 78.6% |
| **合计** | **168** | **62** | **33** | **45** | **28** | **56.1%** |

若把「不适用」也计入分母（严格全量口径）：完全覆盖 36.9%，加权 46.7%。

---

## 三、P0：会造成数据丢失 / 写坏 Mac 数据的流程断裂

### P0-1 本地改动不标脏（上传不成功的根因）

`SyncCursorStore.markDirty()` 与 `SyncEngine.schedule()`（1.5 秒防抖）**定义后全工程零调用**；`SyncEngine` 里所有 `SyncCursorStore.set(...)` 都传 `dirty = false`；`SyncCursor.dirty` 默认 `false`（Mac 为 `true`）。

后果链：Android 收下云端笔记后 cursor 为 `dirty=false, baseRev=R, baseUpdatedAt=T1, serverMtime=M1`；用户编辑后 `updatedAt=T2` 但 cursor 不变；下一轮 `shouldSkipFetch` 命中直接 `return`，**改动永不上云**。若此时 Mac 也编辑了，`changed=true && dirty=false` → `UseRemote` → **用云端版本静默覆盖 Android 刚编辑的内容**。

波及：标题、正文、标签、笔记本、置顶、移入回收站、彻底删除墓碑、任务状态/备注/计划时间。唯一例外是「新建且云端无同名文件」，能首次上传一次。

### P0-2 下载异常被当成「云端无此文件」

`SyncEngine.syncOneNote()` 把 `downloadData` 的**任何异常** catch 成 `remoteNote = null` → `afterFetch(remoteRev=null)` → `Upload` → 用本地版本覆盖云端 Mac 笔记。Mac 同类错误只上抛跳过。真机日志里 51 次直链 404 + 33 次取链失败，正是这个通道的高频触发源。

### P0-3 上传回读抖动被误判为上传失败

`publish()` 每轮「上传 → 回读 → 不匹配就 rev+1 重传」，三次后抛「连续三次没能写上云端」。但 `create errno=0` 已代表文件写入成功；回读走的是同一条不稳的下载链路，抖动会白白重传并误报失败（日志中有 1 次实锤）。

### P0-4 快同步笔记两端初始内容不同

同一固定 ID：Mac 是 Markdown 说明正文 + `工作日志`；Android 是空 `richText` + `全部笔记`。首次同步必然 `neverSynced` 冲突或互相覆盖，还可能生成「（冲突副本）」垃圾笔记。

### P0-5 编辑器有损改写 Markdown 原稿

`EditorScreen.saveBody()` 取 WebView `innerHTML` 并**一律强制** `contentFormat = RICH_TEXT`。Mac 的 Markdown 笔记只要在 Android 打开再返回，原稿就被有损转换（丢 H4~H6、删除线、`+ ` 列表、非附件图片），并生成 `<font>` / `<div>` 等非受控标签，Mac 解码会丢颜色与段落结构。**标脏链路接通后这条会真的写坏云端**，因此本轮已加保护（见第六节）。

### P0-6 Mac 端自身缺陷（跨端放大项）

`BaiduPanClient.swift` 把 `method=download&path` 的 `errno 20020` 一律当 `notFound`，导致 Mac 读不到任何云端正文（含 Android 写的），并会用本地旧版重新上传反向覆盖。**这是 Mac 侧待修项，不在 Android 范围内。**

---

## 四、P1：功能链路缺失

| 缺口 | 证据 | 影响 |
|---|---|---|
| 标签管理全链路 UI 缺失 | `NoteStore.allTags/addTag/removeTag/removeTagEverywhere` 零调用；搜索 placeholder 写「标签」却只匹配 title/plainBody | 不能加/删/按标签筛选 |
| 笔记本管理 UI 缺失且 chips 截断 | `NotebookStore.add/remove`、`NoteStore.moveNotes/setNotebook` 零调用；`notebooks.take(6)` | 第 7 个起自建笔记本不可见；不能新建/删除/切换归属 |
| 表格只能插入不能编辑 | `RichTextTableCodec.cellColors/tableCss()` 零调用 | Mac 表格能显示但改不动 |
| 任务详情缺失 | `TodoStore.setNotes/setPlannedAt/updateTitle`、`Todo.notes` 零 UI 调用 | 无备注、无截止时间、无分组折叠 |
| 置顶无入口 | `NoteStore.togglePin()` 零调用，列表只读 `pinned` 显示图钉 | 无法置顶 |
| 头像不跨端 | `AvatarStore.select()` 只写 SharedPreferences，`avatar.json` 全项目无读写 | 两端头像互不可见 |
| 设备被踢不登出 | `DeviceKickedException` 只置 `state=failed` | 被下线设备仍持 token 每 45 秒重试 |
| 旧 manifest 迁移丢任务 | `LegacyManifest` 只有 `notes` | 旧清单 todos 永不迁移 |
| 冲突副本不保留本地 | `SyncEngine.kt:257` 只 upload 不 upsert；`conflictCopy` 强制 `purged=false` | 副本多一份、已删笔记可能复活 |
| 同步状态不实 | `syncNotes` 单篇 `runCatching` 吞掉仍显示「同步成功 处理笔记 24 篇」 | 部分失败用户不可见 |

---

## 五、P2：空实现、假数据与静默失败

- `NotesScreen.kt:105` 搜索图标 `onClick = { /* 搜索焦点 */ }`；`MeScreen.kt:134`「关于与帮助」空 lambda；`SyncScreen.kt:58` 自动同步 `Switch(checked=true, onCheckedChange={})` 不控制任何行为。
- `LoginScreen.kt` 「扫码授权登录」与账号登录共用同一个 `onLogin`，无扫码逻辑。
- `MeScreen.kt:110` 容量「2.4 GB / 2 TB」与 12% 进度条写死，`BaiduPanClient.quota()` 零调用；「刚刚」写死。
- `DevicesScreen.kt` 下线确认空 catch；`AppStorage.writeJson()` 空 catch 吞写失败；`AttachmentStore.importImage()` 失败返回 null 无提示。
- `TrashScreen.kt` 「永久移除」文案与 `purged=true` 墓碑实现不符（`deleteRemote()` 零调用，云端文件不删）。
- 编辑器只在 `exec` / 插图 / 返回时保存，纯输入期间不自动保存；`evaluateJavascript(js)` 后立即 `saveBody()` 存在异步竞态。
- `notebooks.json` 合并算法不减 `localRemoved`、不过滤 `names`，`NotebookStore.remove()` 允许删「全部笔记」，可能把矛盾数据写进云端影响 Mac。

---

## 六、本轮（2026-11-13）已修复

针对「远端有数据、Android 登录后为空、上传不成功」：

1. **下载链路加固**（`BaiduPanClient.kt`）
   - 下载失败不再用「列目录查不到」推断文件不存在（网络中断时同样查不到）：只要目录列表里有这个 id，直链 404 / 风控 / 网络抖动一律作为临时错误上抛。
   - `downloadData()` 改为 3 次重试，每次重新取直链并退避（300/600 ms）；`obtainDlink()` 重试时强刷父目录重新拿 `fs_id`，应对 `filemetas` 漏项与列表延迟。
   - `uploadChunk()` 增加返回体 `error_code` / `errno` 校验，避免 HTTP 200 但分片失败拼出不完整文件。
2. **同步决策加固**（`SyncEngine.kt`）
   - `syncOneNote/syncOneTodo` 只有「目录列表里根本没有这个 id 且本篇从未同步过」才认定云端无文件；其它情况下载/解析异常一律上抛 → `syncNotes/syncTodos` 记失败 → 下一轮重试，**不再拿本地内容覆盖云端**。
   - 目录列表里缺文件但本篇曾同步过时，跳过本轮，避免列表抖动导致误覆盖。
   - `publish/publishTodo` 把上传与回读解耦：`create` 成功即视为已写入，回读失败只重试回读、不再 rev+1 重传，消除「连续三次没能写上云端」误报。
   - `syncNotes/syncTodos` 返回 `(成功, 失败)`，有失败篇时状态如实标为 `failed` 并写明「N 篇未同步，下一轮自动重试」。
   - `syncSingleNote` 在同步中改为排队而非静默丢弃。
3. **本地改动上传**（`NoteStore.kt` / `TodoStore.kt` / `SyncCursorStore.kt`）
   - 所有用户写操作标脏并触发 1.5 秒防抖同步；同步引擎写回统一走不标脏的 `upsert`，避免同步风暴。
   - `SyncCursorStore` 读写按忽略大小写归一 key，避免云端大写 UUID 与本地小写 id 不匹配导致游标丢失。
4. **登录即时同步**（`MainActivity.kt`）
   - OAuth 回调成功后立即 `syncRound()`，不再等 45 秒轮询；同步失败时轮询间隔缩短到 10 秒。
5. **防止写坏 Mac 原稿**（`EditorScreen.kt`）
   - Markdown 笔记在手机端只读、不回写正文（元信息行有提示）；命令与取 HTML 合并进同一次 `evaluateJavascript` 消除保存竞态；插图失败给出 Toast。

---

## 七、剩余待办（按优先级）

1. **Mac 端 `BaiduPanClient.swift` 增加 `filemetas` 下载兜底**，否则 Mac 仍读不到云端正文并可能反向覆盖（P0，跨端阻塞项）。
2. 对齐 `QUICK_SYNC_NOTE_ID` 两端初始内容/格式/笔记本（P0-4）。
3. 编辑器补 Markdown 纯文本编辑能力，替代当前「只读」保护。
4. 补齐标签 / 笔记本 / 置顶 / 任务详情 / 表格单元格编辑 UI（这些业务层方法已写好，接线成本相对低）。
5. `avatar.json` 云同步、`quota()` 接线、自动同步开关真实化、被踢自动登出、回收站只读预览、彻底删除清理云端文件。
6. `notebooks.json` 合并算法对齐 Mac `NotebookMerge`，禁止删「全部笔记」。
7. 编辑器输入期间自动保存、附件 `uploadedAttachments` 即时回写、`ImagePathScanner` 接入。

---

## 八、口径与局限

- 桌面专属交互（右键菜单、菜单栏快捷键、hover 浮窗、三档侧栏、TextKit 1 表格 gutter、NSMenu 单元格菜单、窗口管理、`table-debug.log` 等）28 项标为「不适用（移动端）」，不计入缺失；但 Mac 具备的**业务能力**在 Android 无实现仍一律算未覆盖。
- 「部分覆盖」包含「业务层已实现但 UI/触发链路未接入」。
- 本报告为静态代码审计 + 主机侧百度接口实测 + 真机日志分析；按项目约定**不做自动化 UI 测试**，功能表现由用户手工验收。
- 审计时点 `origin/main` 为 `ca7d900`；并行会话可能同时改动其它模块，报告不覆盖审计之后的改动。
