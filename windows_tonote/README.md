# 同笔记 Windows

把 Mac 版同笔记迁到 Windows 的源码工程。产品行为以仓库里的三份文档为准：

- `docs/设计文档.md`：分层、JSON、受控 HTML、按篇同步
- `docs/功能点文档.md`：登录、笔记本、列表、编辑器、任务、回收站
- `docs/用户使用文档.md`：三栏界面、自动保存、数据归用户自己
- `同笔记-Mac端UI设计.html`：配色与三栏布局令牌

## 已经能用

**界面**
- 登录页沿用绿色品牌区；「账号登录 / 扫码登录」打开浏览器，通过 `tongnote://oauth/callback` 回调。
- 三栏：侧栏三档（52 / 112 / 200，点分隔条或拖动吸附）、笔记列表、编辑区。侧栏显示头像和同步状态。
- 设置面板：8 个预设头像、百度账号昵称与网盘配额、同步状态与「立即同步」、已登录设备列表与「下线」、本机数据目录、退出登录。
- 笔记列表：新建、改标题、搜索、三种排序、标签、笔记本、置顶、回收站、彻底删除。
- 快任务：四状态 + 点标题展开详情（标题、备注、计划日期）。

**富文本编辑器（WebView2）**
- `contenteditable` + Avalonia 工具条：正文/标题 1-3、引用、代码、加粗、斜体、下划线、删除线、字号、字色、底色、项目符号、编号、链接、插图、表格 3×3、增删行列、单元格对齐与底色。
- 浏览器产出的 `div` / `font` / `tbody` / 绝对路径图片，先在 JS 侧规整成三端约定的受控标签集，再用 `RichTextHtmlCodec.Encode(Decode(...))` 校验后才写盘。
- 插图走 `AttachmentStore.ImportImage`，只存 `attachments/<noteId>/<uuid>.<ext>` 相对路径；没有 WebView2 运行时时自动退回纯文本框，不会破坏已有 HTML。

**数据与同步（对照 Mac 版逐条移植）**
- 受控 HTML 编解码、表格模型与编解码、Markdown 导入、图片路径扫描、附件仓库。
- 百度网盘：令牌交换/续期、用户信息（`xpan/nas?method=uinfo`）/配额（`api/quota`）、目录列举与删除、`xpan download` 取直链（errno 20020 时降级 `filemetas?dlink=1`）、precreate → superfile2（4MB 分片 + MD5）→ create 覆盖上传；响应按字节 UTF-8 解码，单请求 90 秒超时。
- `SyncEngine`：1.5 秒防抖 + 重入标记、按篇三方合并（rev / updatedAt / cursor 五分支）、发布三次重试并回读校验、冲突时生成「（冲突副本）」、笔记本名单并集、任务按条同步、附件上传下载、旧 `manifest.json` 迁移、`purged` 墓碑。
- 设备会话：每台设备一份 `devices/<id>.json`，被别的设备下线会清令牌回到登录页；头像单独同步到 `avatar.json`。

本地数据在 `%LOCALAPPDATA%\tonote`（可用环境变量 `TONOTE_DATA_ROOT` 覆盖，便于测试）。
`notes.json` / `todos.json` / `notebooks.json` 与 Mac 端字段一致；同步游标在 `note-sync.json` / `todo-sync.json`。
同步状态额外写一份 `sync-status.json`（kind / message / date），便于重启后诊断上一轮同步；状态里的「笔记 N 篇 · 任务 M 条」是**本轮变更数**，0 表示已与网盘一致，不是没有数据。
本地数据为空时不再灌入演示笔记，登录后由同步引擎直接从真实网盘拉取；`NoteSamples` 只在显式调用时使用，避免首次启动把示例数据当成「本地新增」推上云端。
`TONOTE_REMOTE_ROOT` 只允许把远端根改成 `/apps/tong_log/__selftest__/…` 隔离子目录，供功能测试使用。

## 还没迁 / 已知边界

- 表格列宽拖拽（模型已支持 `data-widths`，UI 还没有拖拽手柄）；段落对齐不在三端数据契约里，故意没做。
- 任务备注里的图片目前只能粘贴路径文本，UI 没接图片选择器。
- 富文本编辑器依赖 Windows 的 WebView2 Runtime；其它平台只会有纯文本回退。
- Markdown 批量导入导出、全局快捷键、使用说明内嵌页未迁。

## 真实网盘功能测试

`tests/Tonote.LiveTests/` 是连**真实百度网盘**的分组功能测试，覆盖认证与配额、上传/下载/列举/删除原语、按篇同步引擎、设备与头像、Mac 生产数据跨端解码、UI 层、真实 GUI 进程端到端（A–G 七组），另有一组**生产目录只读验收**（`--prod`，H 组）。

前置条件：`baidupan.secret.json` 放在 `windows_tonote/`（不入库），且本机已完成一次账号授权（`%LOCALAPPDATA%\tonote\baidupan-token.json`）。测试只读真实令牌，不会改它。

```bash
"D:/dev/dotnet/dotnet.exe" run --project tests/Tonote.LiveTests/Tonote.LiveTests.csproj -c Debug -- A B C D E F G
# 只跑某几组：末尾写组名即可，例如 -- C G
# 加 --keep 可保留本次远端沙箱目录，便于排查
# 清理由异常中断残留的历史沙箱（仅在无其它测试实例运行时单独执行）：
"D:/dev/dotnet/dotnet.exe" run --project tests/Tonote.LiveTests/Tonote.LiveTests.csproj -c Debug -- --clean-orphans
# 生产目录只读验收：不设 TONOTE_REMOTE_ROOT，直接读 /apps/tong_log，零云端写入：
"D:/dev/dotnet/dotnet.exe" run --project tests/Tonote.LiveTests/Tonote.LiveTests.csproj -c Debug -- --prod
```

安全护栏：普通分组的所有云端写操作被强制限制在 `/apps/tong_log/__selftest__/<runId>/`，本地数据写到 `windows_tonote/.test-data/`（已 gitignore）；程序启动时会校验远端根确实落在沙箱内，否则拒绝执行；正常结束会自动删除本次云端沙箱。可用 `TONOTE_TEST_ROOT` 改本地测试目录。
`--prod` 是独立分支，刻意不设沙箱变量，只调用列目录/下载接口核对正式数据根与生产网盘是否一致，**不启动同步引擎、不写任何云端文件**，因此不会污染生产笔记。

## 离线单测与故障注入

`tests/Tonote.Core.Tests/` 是**不连网盘的离线单测**，`dotnet test` 即可跑，无需 `baidupan.secret.json` 或已授权令牌：

- `CoreTests.cs`：JSON / 存储 / 回收站 / 笔记本 / 任务 / 可见文本 / OAuth 等基础单测。
- `FaultInjectionTests.cs`（批次 0 同步三铁律回归网）：内置内存假网盘 `FakePan`，注入断网 / 列目录异常 / 下载 404 / `errno 20020` / `31034` 限速 / 回读失败等故障，断言「本地旧数据不覆盖云端、不重复上传、下轮收敛」。

```bash
"D:/dev/dotnet/dotnet.exe" test tests/Tonote.Core.Tests/Tonote.Core.Tests.csproj -c Debug
```

## 编译

需要 .NET 8 SDK 与 WebView2 Runtime。本机 SDK 在 `D:\dev\dotnet`，NuGet 包在 `D:\dev\_nuget_app`：

```bash
export APPDATA="C:/Users/Administrator/AppData/Roaming" \
       ProgramData="C:/ProgramData" PROGRAMDATA="C:/ProgramData" \
       ALLUSERSPROFILE="C:/ProgramData" ProgramFiles="C:/Program Files" \
       CommonProgramFiles="C:/Program Files/Common Files" \
       DOTNET_ROOT="D:/dev/dotnet" NUGET_PACKAGES="D:/dev/_nuget_app"
"D:/dev/dotnet/dotnet.exe" build src/Tonote.App/Tonote.App.csproj -c Debug
"D:/dev/dotnet/dotnet.exe" run --project src/Tonote.App
```

> 编译前先关掉正在运行的 `Tonote.exe`，否则 `Tonote.Core.dll` 被占用会复制失败。

打 Windows 程序：

```bash
./scripts/publish-windows.sh
```

产物是 `dist/win-x64/Tonote.exe`。
