# CLAUDE.md — android_tonote（同笔记 Android 版）

> 项目常驻上下文。只记录模型**猜不到**的约定：跨端契约、同步语义、已知陷阱。
> 通用编码常识（命名、格式化）不在此重复。

---

## 1. 项目是什么

跨端笔记应用的 Android 客户端。数据不落自建服务器，直接写进用户自己的百度网盘 `/apps/tong_log`。

与 `mac_tonote`（Swift）、`windows_tonote`（C#）**共用同一套数据格式**，三端数据必须互通——这是全项目最高优先级的约束。

规模：单模块、单 Activity、纯 Compose，约 4.3k 行 Kotlin / 31 个文件。

---

## 2. 构建

- JDK 17 / AGP 8.7.3 / Kotlin 2.0.21 / Compose BOM 2024.12.01 / OkHttp 4.12.0
- 版本号**只在 `gradle/libs.versions.toml` 里改**，不要在 `build.gradle.kts` 里写死版本字符串
- `settings.gradle.kts` 里的**阿里云镜像不能删**（本机网络环境依赖它，删了依赖拉不下来）
- 命令：`./gradlew assembleDebug`
- 项目**没有** ktlint / detekt。不要"顺手补一套"，见第 9 节
- 唯一的测试是 `app/src/test/.../SyncRulesTest.kt`（34 条，纯 JVM，无 Android 依赖）
  - 跑测试：`./gradlew :app:testDebugUnitTest`
  - **改 `SyncRules` 必须先跑这组测试**。它锁的是跨端合并语义，不是为了刷覆盖率
  - 除非用户明确要求，不要引入 Mockito / Robolectric / Turbine 等测试栈

---

## 3. 分层与依赖方向

```
data/model     纯数据类
data/codec     编解码（HTML / 表格 / 图片路径 / Markdown）
data/store     本地持久化 + StateFlow
data/net       百度网盘 HTTP 客户端
data/sync      同步引擎与合并规则
ui             Compose 界面
```

依赖是**单向**的：`ui` 可以依赖 `data`，**`data` 不允许 import `ui`**。

- `store` 是唯一数据入口，UI 不直接读写文件
- `codec` 保持纯函数，不要把业务逻辑塞进去
- 新增数据能力：先想清楚放 `store` 还是 `sync`，不要顺手开新包

---

## 4. 跨端数据契约（改这里等于改三端）

### 远端结构

```
/apps/tong_log/
├── notes/<UUID>.json
├── todos/<UUID>.json
├── notebooks.json
├── manifest.json
└── attachments/<noteId>/<uuid>.<ext>
```

### 字段语义

- `rev`：服务端版本号，判云端是否变化
- `updatedAt`：ISO8601 字符串，**冲突时的胜负依据**
- `purged`：删除墓碑（布尔）
- `contentFormat`：正文格式标识

### 正文是「受控 HTML」，不是 Markdown

表格靠 `data-*` 属性承载排版，Mac 端按这套规则解析：

- `<table data-fullwidth>`
- `<td data-align>` / `<td data-bg>`

`MarkdownConverter` 只用于导入或兼容旧数据，**不要拿它当主存储格式**。

### 铁律

1. **禁止单端新增必填字段**。新增字段必须可选、有默认值、老数据能正常跑
2. **禁止修改已有字段的语义**（`rev` 的取值规则、`updatedAt` 的格式）
3. **删除走 `purged` 墓碑，不做物理删除**
4. 改动第 4 节任何内容，必须在提交说明里写明"需要 Mac/Windows 端同步****"

---

## 5. 同步引擎红线（`data/sync/`）

- 合并是**三方合并**（`rev` 判变化 + `updatedAt` 定胜负 + `cursor` 记基线），**不是 last-write-wins**。不要把 `SyncRules` 简化成时间戳比较
- `SyncRules.TOLERANCE_SECONDS = 1` 是刻意留的容差，别改
- `SyncEngine.schedule()` 的 **1500ms 防抖** + `needsAnotherRound` 重入标记是防止同步雪崩的，不要"优化"成直接调用 `syncRound()`
- 同步是**按篇**进行的：单篇失败只记该篇，不要中断整轮

### 已知陷阱：OkHttp 响应体必须当场读完

```kotlin
// 错误：use 退出即关闭 response，之后读 body 抛 IllegalStateException: closed
val resp = client.newCall(req).execute().use { it }
resp.body?.string()

// 正确
client.newCall(req).execute().use { resp -> resp.code to (resp.body?.string() ?: "") }
```

这个 bug 曾导致**每轮同步都静默失败**（见提交 `107780b`）。动网络代码前先看 `BaiduPanClient.executeText`。

---

## 6. 网络层（`data/net/`）

- 上传：`precreate` → `superfile2`（**4MB 分片**）→ `create`，与 Mac 端一致
- 下载：优先 `path` 直下；命中 `errno 20020`（路径不在允许范围）后降级 `filemetas`，并用 `downloadByPathBlocked` 记住，避免每篇笔记都先发一个必败请求
- 不要换网络库（Retrofit / Ktor），不要改超时值
- 凭证：`app/src/main/assets/baidupan.secret.json`，**已在 .gitignore 中，禁止提交**。仓库里只有 `.example` 模板

---

## 7. UI 约定

- **颜色只能取自 `ui/theme/Theme.kt` 的设计令牌**（`Green` / `Bg` / `TextPrimary` / `Line` …）。禁止在任何 Screen 里写 `Color(0xFF...)`——设计稿要求与 Mac 端逐像素对齐
- **只有浅色主题**，不要加 `isSystemInDarkTheme()` 分支
- **文案直接写中文字面量**。项目未做 i18n（`stringResource` 使用数为 0），不要为此引入 `strings.xml` 重构
- Screen 都是 `@Composable` + 回调参数（`onBack` / `onOpenNote` / `onSync`），**Screen 内部不持有 `navController`**
- 路由是字符串常量：`"main"` / `"editor/{noteId}"` / `"sync"` / `"trash"` / `"devices"`。新增页面必须在 `MainActivity` 的 `NavHost` 里注册
- 没有 ViewModel 层。状态来自各 `store` / `SyncEngine` 的 `StateFlow`，UI 用 `collectAsState()`

---

## 8. 本地存储（`data/store/AppStorage.kt`）

- 单例 `object` + `lateinit context`，**只在 `TongNoteApp.onCreate()` 初始化一次**
- 根目录 `filesDir/tonote/`，与 Mac 端 `~/Library/Application Support/tonote/` 一一对应
- 全局 `Json` 配置 `prettyPrint = true` / `encodeDefaults = true` / `ignoreUnknownKeys = true` **不要改**——改了之后与 Mac 端写入的文件格式不一致，交叉读写会出问题

---

## 9. 已知"坏味道"，**禁止顺手修**

| 现象 | 为什么别动 |
|---|---|
| 全是 `object` 单例 + 全局可变状态 | 刻意选择的轻量架构。**不要引入 Hilt / Koin** |
| 没有 ViewModel 层 | 同上。补一层等于重写所有 Screen |
| 中文硬编码、无 `strings.xml` | 单语言项目，改造要动 10+ 文件，收益为零 |
| 没有 lint | 现状如此。不要"顺手补 ktlint / detekt" |
| 测试只覆盖 `SyncRules`，其余无测试 | 只有纯 JVM 的合并规则值得测。**不要给 Screen / Store / 网络层补测试**，那需要 Robolectric 或真机 |
| `MainActivity` 里 `when (tab)` 硬编码索引 | 与底部导航顺序强耦合，要改就连导航栏一起改 |
| `EditorScreen` 用 WebView 承载富文本 | 三端共用同一套 HTML，换成原生编辑器会破坏跨端一致性 |

---

## 10. 提交前检查

1. `./gradlew assembleDebug` 必须 0 error
2. 动了 `data/sync/` 下任何逻辑，`./gradlew :app:testDebugUnitTest` 必须全绿
3. 不要提交：`app/build/`、`.gradle/`、`.kotlin/`、`local.properties`、`.idea/`、`baidupan.secret.json`、`*.jks`
4. 若改了第 4 节的数据契约，在提交说明里显式标注影响 Mac / Windows 端
5. 提交信息沿用现有风格：`fix(android): …` / `feat: …` / `docs(android): …`

---

## 11. 不要做的事

- 不升级 AGP / Kotlin / Compose BOM / OkHttp
- 不加新依赖（除明确要求）
- 不删 `settings.gradle.kts` 的阿里云镜像
- 不重排包结构、不拆模块
- 不改远端路径与文件名（`notes/`、`todos/`、`attachments/` 的命名规则）
- 不碰 `mac_tonote` / `windows_tonote` 目录（它们有各自的构建体系）

---

## 12. 已知待办

- 无 lint 检查；单测仅覆盖 `SyncRules`
- `SyncEngine` 的并发/重入路径（`needsAnotherRound`、1500ms 防抖）没有测试，改动时靠人工校验

---

**动手前先说明：打算改哪几个文件、为什么、怎么验证。数据契约或同步逻辑的改动，等确认再动手。**
