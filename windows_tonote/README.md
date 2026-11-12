# 同笔记 Windows

把 Mac 版同笔记迁到 Windows 的源码工程。产品行为以仓库里的三份文档为准：

- `docs/设计文档.md`：分层、JSON、受控 HTML、按篇同步
- `docs/功能点文档.md`：登录、笔记本、列表、编辑器、任务、回收站
- `docs/用户使用文档.md`：三栏界面、自动保存、数据归用户自己

## 这一轮已经能用

- 登录页沿用绿色品牌区。`baidupan.secret.json` 会在启动时读入，文件不进 git。已有令牌时直接进入笔记。「使用百度网盘账号登录」和「浏览器扫码登录」都会打开系统浏览器，授权完成后通过 `tongnote://oauth/callback` 回到本程序。Windows 上启动时会给当前用户登记这个协议。
- 主窗口默认 1120×700，打开在屏幕中间，最小 960×620。
- 三栏：侧栏三档（52 / 112 / 200，点分隔条切换，拖动吸附）、笔记列表、编辑区。
- 本地笔记：新建、改标题、写 Markdown 正文、标签、笔记本、置顶、搜索、三种排序、回收站、彻底删除。
- 快任务：四状态，本地 `todos.json`。
- 笔记文件是 `notes.json`，时间用到秒的 ISO 8601，能读 Mac 写出的文件。富文本笔记只显示可见文字，不会把 HTML 覆盖掉。

Windows 上的数据目录是 `%LOCALAPPDATA%\tonote`。在 Mac 上调试时用 `tonote-windows`，不会写进 Mac 版的 `Application Support/tonote`。

## 还没迁

按篇同步、设备下线、受控 HTML 编辑器、表格、图片附件、使用说明页面。登录和扫码登录已经接上。

## 编译

需要 .NET 8 SDK。

```bash
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$DOTNET_ROOT:$PATH"
cd windows_tonote
dotnet test
dotnet run --project src/Tonote.App
```

打 Windows 程序：

```bash
./scripts/publish-windows.sh
```

产物是 `dist/win-x64/Tonote.exe`。
