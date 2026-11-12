# 同笔记 · Android 版（android_tonote）

以 `WorkLog-App端UI设计.html` 为蓝本的 Android 版「同笔记」：把笔记、任务、图片附件直接写入你自己的百度网盘 `/apps/tong_log`，数据开放、离线可用、跨端同步（与 macOS 版同笔记数据格式互通）。

## 技术栈

- Kotlin 2.0.21 + Jetpack Compose（Material 3）
- 架构：单模块分层 —— `data/model`（模型）、`data/store`（本地存储）、`data/codec`（HTML/表格/图片路径编解码）、`data/net`（百度网盘 client）、`data/sync`（同步引擎）、`ui`（界面）
- 网络：OkHttp 4 + kotlinx.serialization + kotlinx.coroutines
- minSdk 26（Android 8.0）/ compileSdk & targetSdk 35 / JDK 17

## 目录结构

```
app/src/main/java/com/tong/log/app/
├── TongNoteApp.kt          # Application 初始化
├── MainActivity.kt         # 入口 + OAuth 回调 + 底部导航
├── data/
│   ├── model/              # Note / Todo / SyncCursor / DeviceRecord / 头像
│   ├── store/              # NoteStore / TodoStore / NotebookStore / AttachmentStore / AvatarStore
│   ├── codec/              # RichTextHTMLCodec / RichTextTableCodec / ImagePathScanner / MarkdownConverter
│   ├── net/                # BaiduPanConfig / Models / TokenStore / Client（分片上传）
│   └── sync/               # SyncRules / SyncCursorStore / DeviceSession / SyncEngine
└── ui/                     # Login / Notes / Editor(WebView富文本) / Todo / Me / Sync / Trash / Devices
```

## 配置百度网盘凭证

1. 复制模板：`cp app/src/main/assets/baidupan.secret.example.json app/src/main/assets/baidupan.secret.json`
2. 填入百度开放平台应用的三项：`appKey` / `appSecret` / `appID`。
3. 回调地址需配置为 `tongnote://oauth/callback`（与 macOS 版共用同一套凭证与回调 scheme）。

`baidupan.secret.json` 已在 `.gitignore` 中忽略，不会进入版本库。

## 编译运行

```bash
# 需 JDK 17（可用 Android Studio 自带 JBR 或本机 JDK 17）
export JAVA_HOME=/Users/wukong123/Library/Java/JavaVirtualMachines/jdk-17.0.18+8/Contents/Home
./gradlew assembleDebug          # 产出 app/build/outputs/apk/debug/app-debug.apk
```

首次构建会下载 Gradle 与依赖（已配置阿里云/腾讯镜像加速）。

## 功能清单（对照移动端设计稿 6 屏）

| 屏 | 实现 |
|---|---|
| 登录 | 百度网盘 OAuth（系统浏览器 + `tongnote://` 回调），token 落本机 |
| 笔记 | 笔记本 chips 筛选、搜索、三种排序、固定卡片（快同步/快任务）、FAB 新建 |
| 编辑器 | WebView contenteditable 富文本：标题/加粗斜体/列表/引用/代码/链接/文字色/背景色/图片/表格，正文即受控 HTML |
| 任务 | 四状态分组、快捷添加、截止时间与逾期 |
| 我的 | 账号/存储/同步/设备/回收站/头像 |
| 同步与存储 | 按篇同步、同步状态、云端目录树、账号安全 |

## 与 macOS 版的格式兼容

- 正文：受控 HTML（`<table data-fullwidth>`、`<td data-align>` / `data-bg>`）
- 笔记/任务：JSON（`rev` / `updatedAt` / `purged` 墓碑 / `contentFormat`）
- 附件：`attachments/<noteId>/<uuid>.<ext>` 相对路径
- 同步：按篇 `notes/<UUID>.json`，`rev + updatedAt + cursor` 三方合并，冲突副本
