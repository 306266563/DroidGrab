# PayloadDumper Plus 增强版 · PRD

- 版本：v1.1（按 2026-10-27 反馈细化：界面 / 下载器 / 线刷包 / 计划）
- 形态：Windows 桌面 GUI（C# / .NET 8，自包含单文件 exe）
- 交付物：`PayloadDumperPlus.exe` + `README.md`

---

## 1. 问题

| # | 现状痛点 |
|---|---|
| P1 | 手机端（Termux / App 版）提取 OTA 分区慢，CPU 弱、内存小、写盘慢，动辄十几分钟 |
| P2 | 现成的 `payload_dumper.ps1` 只是个壳，实际靠 `payload-dumper-go.exe`，单线程顺序提取，多核机器吃不满 |
| P3 | 原版只认卡刷包（payload.bin），线刷包（tgz 里的 images/、super.img）得手工 tar 解包再跑 lpunpack |
| P4 | 命令行参数记不住，进度看不见，大包跑一半断了只能重来 |

## 2. 成功标准（验收线）

0. **链接直提【硬需求 · 不可砍】**：只粘一条 URL，自动完成「识别 → 多线程下载 → 解包 → 解析 → 提取 → 校验 → 落盘」，中间零手工步骤，对齐现版 `payload_dumper.ps1` 的用法且更快、有进度、能续传。
   - 支持：OTA zip 直链、裸 `payload.bin` 直链、线刷包 `.tgz/.tar/.zip` 直链
   - 类型识别靠 magic 不靠后缀（`PK` / `1F 8B` / `CrAU` / `3A FF 26 ED`），后缀仅作提示
   - 跟随 301/302、支持带 query token 的 CDN 链接、可自定义 UA / Referer / Cookie
   - 断点续传：断网 / 关程序后重开继续，已完成分片不重下
1. **卡刷包**：OTA zip 或裸 payload.bin（本地 / 链接两条路），导出全部分区 img，`sha256` 与包内记录一致
2. **线刷包**：`.tgz/.tar/.zip` 线刷包导出 `images/` 全部镜像；`super.img` 自动拆出 `system/vendor/product/odm/system_ext/...` 独立 img
3. **速度**：下载最高 32 线程；提取按核数并行。8 核 NVMe 上相对单线程 ≥3x（xz/bz2），raw ≥2x
4. **稳**：提取中途强杀，重开可续跑；单个分区失败不拖垮整包，失败原因单独列出
5. **看得见**：总进度 + 当前分区 + 实时速度 + 剩余时间，日志可导出

## 3. 范围

### 3.1 输入源（硬需求）

- 本地 OTA zip / 裸 `payload.bin`
- 本地线刷包 `.tgz / .tar / .zip`（含 `tar.md5`）
- **URL 直链**：内置多线程分段下载器（HTTP Range，1~32 连接）+ 断点续传，粘链接即全自动
- 一条流水线：`识别 → 下载 → 解包 → 解析 → 提取 → 校验 → 完成`，任一步失败可单独重试

### 3.2 payload 解析

- `CrAU` v1 / v2 头，手写 protobuf wire 解码读 manifest（字段号严格对齐 `update_engine/update_metadata.proto`）
- 操作类型：`REPLACE`(raw)、`REPLACE_XZ`、`REPLACE_BZ`、`ZERO`、`DISCARD`
- 多、乱序 `dst_extents` 散写；分区可含数千个 operation
- `new_partition_info.hash`（整分区 sha256）可选校验；单 operation `data_sha256_hash` 默认关（拖速度）

### 3.3 线刷包范围（细化）

**包结构覆盖**

| 类型 | 识别方式 | 处理 |
|---|---|---|
| 小米/红米官方 `.tgz` | gzip + tar，内含 `images/*.img` | 流式解 tar，抽 `images/` 全部镜像 |
| 一加/OPPO/realme 等 `.zip` 线刷包 | `PK\x03\x04`，内含 `images/` 或根目录 `.img` | 流式解 zip |
| 三星 `*.tar.md5` | tar + 尾部 32 字节 md5 + 文件名 | 识别尾部校验块后按普通 tar 处理 |
| zip 里套 tar（双层包） | 递归 magic 探测 | 支持一层嵌套 |
| 散装目录（MTK 线刷包） | 用户选目录 | 扫描并列出目录下全部 `.img`，可选复制到输出 |

**镜像处理**

- `sparse`（magic `0xED26FF3A`）→ 展平为 raw（chunk 类型 raw / fill / don't-care / crc32 全支持）
- `super.img` → 解析 liblp 元数据（geometry 在展开后偏移 4096，分区表在 8192），拆出各逻辑分区
  - 输出命名：默认 `system.img / vendor.img ...`；与 images 原有同名冲突时自动前缀 `super_`
  - 可选「保留 super.img 原样」（默认保留）
- 原样输出其余 `.img`，不改名不改内容

### 3.4 明确不做

| 不做 | 原因 |
|---|---|
| 增量 OTA（`SOURCE_COPY`/`SOURCE_BSDIFF`/`BSDIFF`/`PUFFDIFF`/`ZUCCHINI`/`LZ4DIFF_*`） | 需要旧版原厂镜像作 patch 源；识别到直接报「这是增量包，请用全量包」并跳过该分区 |
| metadata 签名验证 | 需厂商公钥，验了也无判定意义 |
| 镜像重打包 / 修改 / 刷机 | 只做提取端 |
| ext4 / erofs 文件系统级浏览（进 img 看文件） | 另一套工程，v1 不碰 |
| 华为 `UPDATE.APP` | 私有格式，另立项目 |
| Linux / macOS 版、手机端 | 只做 Windows；Core 层不绑 WinForms，日后可加 CLI |

## 4. 界面与交互（细化）

### 4.1 布局

```
┌──────────────────────────────────────────────────────────────────────┐
│  Payload Dumper Plus                    [最小化] [最大化] [关闭]      │
├──────────────────────────────────────────────────────────────────────┤
│  [📂 选文件] [📁 选目录]   ┌────────────────────────────────────┐    │
│                            │ 粘贴 URL / 拖入文件，回车开跑       │    │
│                            └────────────────────────────────────┘    │
│                            [开始]  [暂停]  [取消]                     │
├──────────────────────────────────────────────────────────────────────┤
│  下载线程 [━━━━━━━●━━━] 32      提取线程 [━━━●━━━━━━] 8               │
│  输出目录 [D:\extracted\ota_xxx              ] [浏览]                 │
│  ☑ 校验分区 sha256   ☐ 保留 super.img   ☐ 顺序写模式(HDD)             │
├──────────────────────────────────────────────────────────────────────┤
│  总进度 ███████████████░░░░░░  68%   1.24 GB/s   剩 00:42             │
│  当前：system · REPLACE_XZ 解压中 · 分片 812/2048                     │
├──────────────────────────────────────────────────────────────────────┤
│  分区        大小       状态      耗时     校验                       │
│  boot        64.0 MB    完成      0.4s     OK                         │
│  system      1.82 GB    提取中 68% 12s     -                          │
│  vendor      812 MB     等待      -        -                          │
├──────────────────────────────────────────────────────────────────────┤
│  日志 …                                              [导出] [清空]     │
└──────────────────────────────────────────────────────────────────────┘
```

- 默认尺寸 1080×720，最小 900×600，可缩放；Per-Monitor DPI 感知（高分屏不糊）
- 暗色为默认主题，跟随系统亮暗；Windows 11 下套用深色标题栏（DWM 属性）
- 字体：微软雅黑 UI / Segoe UI，等宽日志用 Cascadia Mono（缺失回退 Consolas）

### 4.2 交互

- **拖拽**：任意文件、目录、甚至浏览器拖来的链接 → 自动进队列
- **队列**：多任务排队（粘贴多行链接 = 多个任务），一次只跑一个，其余等待；显示队列位置
- **Cl 快捷键**：`Ctrl+V` 粘链接开跑、`Ctrl+O` 选文件、`Ctrl+E` 导出日志、`F5` 重试失败项、`Del` 从队列移除
- **分区表**：双击行 → 打开该 img 所在目录；右键 → 复制路径 / 重试 / 删除输出
- **失败**：整行标红 + 原因气泡（如「增量包，需旧镜像」），不弹窗打断
- **完成**：底部出现「打开输出目录」按钮，路径可一键复制
- **设置持久化**：线程数、输出目录、UA/Referer、主题 → `%LOCALAPPDATA%\PayloadDumperPlus\settings.json`
- **不弹模态框**：所有提示走顶部横幅（可关闭），避免卡住自动化

## 5. 下载器细节（细化）

| 项 | 规则 |
|---|---|
| 默认并发 | **32**（用户明确要满档）；总长 < 32×4MB 时自动降到 `⌊总长/4MB⌋` |
| 探测 | `HEAD` 取 `Content-Length` / `Accept-Ranges` / `ETag` / 最终 URL；HEAD 被拒(403/405) → `GET Range: bytes=0-1` 探测 |
| 不支持 Range | 单流回退，横幅提示「该服务器不支持分段，速度受单连接限制」 |
| 分段 | 等分，最后一片吃余数，最小片 4MB，段内 1MB 缓冲；全链路 `long`（>4GB 安全） |
| 落盘 | 直接写目标文件对应 offset（`FileStream` + `Seek` 各写各段，无合并拷贝）；缓存目录默认 `%TEMP%\PayloadDumperPlus\<url哈希>\`，可改到指定盘 |
| 空间预检 | 可用空间 < 包大小 × 1.2 → 拒绝启动并提示 |
| 状态文件 | `state.json`：`url / finalUrl / etag / lastModified / totalBytes / segments[{start,end,done}] / version`；原子写（temp + rename），每完成一片刷一次 + 每 2s 心跳 |
| 续传失效判定 | `ETag` 或 `Last-Modified` 变化 → 状态作废、整包重下（防拼出坏包） |
| 重试 | 单片失败 3 次（300/900/2700ms 退避）；仍失败 → 该片二分重排（自适应切碎，避免大段卡死） |
| 限速自适应 | 连续 3s 吞吐 < 峰值 40% 且并发 > 4 → 横幅提示「服务器限速，建议降至 N」，一键降档 |
| 代理 | 默认跟随系统代理；可填 `http://127.0.0.1:7890` 手动代理 |
| 请求头 | 默认 Chrome UA（部分 CDN 拦非浏览器 UA）；UA / Referer / Cookie 可改，Cookie 存设置但**不写日志** |
| 错误分类 | 403/404/超时/DNS/SSL/空间不足/服务器不支持 → 各自一句人话原因，不甩堆栈 |
| 其他 | 支持 `Content-Disposition` 取真实文件名；无则用 URL 末段；再退化为 `download_<哈希前缀>` |

## 6. 关键设计决策

| 决策 | 选择 | 理由 / 放弃了什么 |
|---|---|---|
| 技术栈 | **C# .NET 8 + WinForms** | `BrotliStream` 内置、`RandomAccess.Write`/`FileStream.Seek` 支持无锁并行写、单文件发布成熟、Win 原生控件。放弃 Python（打包体积 / 启动慢）、放弃 Go（GUI 生态弱，Fyne 做表格日志面板更费劲） |
| 压缩库 | **SharpCompress**（xz / bz2 / tar / zip）+ 内置 Brotli | 一个包覆盖四种容器，少引依赖 |
| protobuf | **手写 wire 解码器**（~200 行） | 只读 manifest，字段固定；免装 protoc、免引 `Google.Protobuf`。风险：字段号错 → 合成样本 + 真实包 sha256 双校验兜住 |
| 32 线程落点 | 两条通道：**下载 32 线程**（IO 等待型，真吃满带宽）、**提取按核数并行**（CPU 型，上限也放 32） | 单分区 xz 解压在 8 核上 32 线程收益递减，所以滑块控「并发度」，提取默认=物理核数。不虚标 |
| 写盘 | 直接定位写目标文件 offset | 避免临时块 + 合并的二倍写放大。HDD 上随机写会抖 → 提供「顺序写模式」开关 |
| 单文件 exe | self-contained（约 60~80MB） | 目标机不一定有 .NET 8 运行时。代价是体积，值得 |

## 7. 架构

```
PayloadDumperPlus/
├─ Core/            # 无 UI 依赖，可单测
│  ├─ Proto/        # 手写 protobuf wire 解码
│  ├─ Payload/      # CrAU 头 / manifest / InstallOperation / 提取调度
│  ├─ Sparse/       # Android sparse 展平
│  ├─ Super/        # LP metadata 解析、逻辑分区拆分（移植 tools/lpunpack.py 逻辑）
│  ├─ Archive/      # zip / tar / tgz / tar.md5 流式读取
│  ├─ Download/     # HTTP Range 分段下载 + 续传
│  └─ Progress/     # 统一进度事件
├─ App/             # WinForms：主窗、队列、分区表、日志、设置
└─ Tests/           # 合成 payload 生成器 + 断言
```

**提取调度**：分区级并行（受滑块限制）→ 分区内按 `data_offset` 顺序读 blob → 投递解压池（并发=核数）→ 结果按 `dst_extents` 定位写。全程有界 Channel 背压，队列满则阻塞，避免大包吃爆内存。

## 8. 限制与风险

| 风险 | 影响 | 对策 |
|---|---|---|
| 真实样本未到手 | 解析字段一错全错 | ① 自建合成 payload 生成器（覆盖 REPLACE/REPLACE_XZ/REPLACE_BZ/ZERO/多分区/多 extent/碎片 extent）；② 用你给的 OTA 直链做终验 |
| 手写 protobuf 解码 bug | 崩溃或静默错位 | 双层校验 + 解析失败报「偏移/字段号」 |
| 增量包 | 用户误用 | 明确报错，不产出残缺 img |
| HDD / 慢盘 | 并发写反变慢 | 「顺序写模式」开关 |
| 厂商魔改 / 加密 payload | 解析失败 | 明确报错，不猜 |
| 32 线程触发 CDN 限速 | 下载反而慢 | 限速自适应 + 一键降档 |

**性能预期（估算，实测为准）**：raw 分区 NVMe 上 1~2GB/s 量级；xz 分区受单核解压 × 并行核数限制，8 核约 300~500MB/s；tgz 解包受 tar 单流限制，仅解压可并行。

## 9. 开发计划（改成一口气做完）

不再分 M1~M6 六道验收，**一轮连续做到能用的 exe**，中间只冒两次泡：

1. **冒泡 A**：Core 解析 + 提取引擎在合成样本上跑通（sha256 对得上）——此时把自测日志给你看
2. **冒泡 B**：`PayloadDumperPlus.exe` 出来，附 README + 真实包实测结果

顺序：骨架 → protobuf/payload → 提取引擎 → sparse/super → 归档 → 下载器 → GUI → 打包 → 真实包终验。中途出方向性问题我立刻停下来问，不闷头跑完再翻车。

## 10. 仍需你提供

1. **OTA 直链**（已确认你给）——到手我就把下载器一起验了
2. 程序名 `PayloadDumperPlus` 是否可用（不满意现在说）
3. 输出目录默认值：暂定「包所在目录 / `extracted_<包名>`」

---

## 现状校准（2026-11-04）

本节不改动上面任何原文，只做逐条核对。字段含义：**原文怎么写** = PRD 当时的设计；**现在是什么** = 本次交付（0.6.0）源码实测；**处理建议** = 后续文档或代码该怎么处理。

核对基准：`src/PayloadDumperPlus.Core/**`、`src/PayloadDumperPlus.App/**`、`CHANGELOG.md`（0.5.29–0.5.37）。源码引用相对项目根，行号取当前工作区。

**重要限定（2026-11-04，task-7 落地后）**：整包分段下载器已删除——`Download/SegmentedDownloader.cs` 从 795 行精简到 191 行，只剩探测、HTTP 客户端构造与代理解析（`:56-61` 类注释）；「固定 32 分段」「下载到缓存目录再解」「`.pdstate.json` 断点续传」都不再存在。真实下载路径有两条：① 按需直读 HTTP Range，并发对象是同时在飞的 Range 请求数，闸门在 `IO/IRandomReader.cs`（`EnterRequest` `:365-377` / `ExitRequest` `:379-386`），上限 1–32 由界面滑块或 `PDP_CONNECTIONS` 设定（后者在运行时覆盖，每次取许可前重读，终值夹在 1–32），按实测吞吐自动升降档（`:388-499`）；② 线刷包 `tar` / `tgz` 单连接顺序流式解压（gzip 流不可随机跳读）。下表 2.5、2.8、2.10、3.11–3.18 中描述旧下载器的条目已按此逐条更新。

### 1. 产品与命名

| # | 位置 | 原文怎么写 | 现在是什么 | 处理建议 |
|---|---|---|---|---|
| 1.1 | :5 | 交付物 `PayloadDumperPlus.exe` + README | 实际产物是 `DroidGrab.exe`（`src/PayloadDumperPlus.App/PayloadDumperPlus.App.csproj:14,26,27`；`dist/DroidGrab.exe`） | 保留原文作历史；README 与发布说明统一用 DroidGrab |
| 1.2 | :196 | 程序名 `PayloadDumperPlus` 待确认 | 0.5.33 已更名 DroidGrab 并加入应用图标（CHANGELOG 0.5.33） | 标记为已决，可关闭该待办 |
| 1.3 | :3 | 版本 v1.1 | 程序版本已提到 **0.6.0**：`src/PayloadDumperPlus.App/PayloadDumperPlus.App.csproj:41-43` 的 `Version` / `AssemblyVersion` / `FileVersion` 均为 0.6.0（0.5.x 时代 csproj 只写到 0.5.34、CHANGELOG 已到 0.5.37） | 文档统一按 0.6.0 表述，已落实 |

### 2. 已实现、与原文一致

| # | 位置 | 原文怎么写 | 现在是什么 | 处理建议 |
|---|---|---|---|---|
| 2.1 | :22 | 按 magic 判断类型，不靠后缀 | `Archive/PackageInspector.cs:30,33,34,46` | 无需处理 |
| 2.2 | :42-45 | 解析 REPLACE/REPLACE_XZ/REPLACE_BZ/ZERO/DISCARD；`new_partition_info.hash` 校验；单 op `data_sha256_hash` 默认关 | `Payload/PayloadExtractor.cs:38,40,446,517-521` | 无需处理 |
| 2.3 | :61-65 | super 拆分，同名冲突加 `super_` 前缀，保留 super.img 默认保留 | `Archive/LineFlashExtractor.cs:196-198`；`Pipeline/ExtractPipeline.cs:89-90` | 无需处理 |
| 2.4 | :67-76 | 明确不做：增量 OTA、metadata 签名、重打包、ext4/erofs 浏览、华为 UPDATE.APP、rawprogram.xml、Linux/macOS/手机端 | 与实现一致；增量包在 `PayloadExtractor.cs:189` 明确跳过并说明原因 | 无需处理 |
| 2.5 | :133 | ETag / Last-Modified 变化则状态作废、整包重下 | 续传状态与 `DownloadState` 已随整包下载器删除，Core 内 grep `pdstate` / `DownloadState` 为 0 命中；探测仍会取回 `ETag` / `LastModified`（`SegmentedDownloader.cs:164-168`），但不再据此作废任何状态 | 该条随机制删除，文档不写「断点续传」 |
| 2.6 | :118 | 完成后可「打开输出目录」 | `MainForm.Layout.cs:396`；CHANGELOG 0.5.36 修过点击无反应 | 无需处理 |
| 2.7 | :136-137 | 默认跟随系统代理；Chrome UA；UA/Referer/Cookie 可改，Cookie 不写日志 | `MainForm.Layout.cs:149,183,193`；`SettingsDialog.cs:31-34`（提示「留空 = 内置 Chrome UA」「不写日志」）；内置 Chrome UA 131.0.0.0（`SegmentedDownloader.cs:37-38`）；代理三态 System / Direct / Custom（`:79-93`）；CHANGELOG 0.5.8 | 端口示例可改为 7897，其余一致 |
| 2.8 | :138 | 错误分类，各自一句人话 | `SegmentedDownloader.cs:127-138`（探测超时与 403/404 文案）；`IO/IRandomReader.cs:167-311`（Range 读取失败与重试文案） | 无需处理 |
| 2.9 | :145-148 | 技术栈 C#/.NET 8 + WinForms；SharpCompress + Brotli；手写 wire 解码；下载 32 / 提取按核数 | `Proto/WireReader.cs`；`Compression/Decompressor.cs:24-27`；`Pipeline/ExtractPipeline.cs:72-75`；并发上限 1–32（`IRandomReader.cs:313-331`） | 默认值已改为按核数推荐（下载 `clamp(核数×2, 8, 16)`、提取 `clamp(核数, 4, 32)`） |
| 2.10 | :149 | 直接定位写目标文件 offset | 分区镜像仍为定点写（`PayloadExtractor.cs:566-583`）；远程读取走 `IRandomReader.cs:167-177` 的按块随机读；旧整包下载器的 `RandomAccess.WriteAsync` 分片路径已删除 | 无需处理 |
| 2.11 | :150 | 单文件 exe 约 60~80MB | 实测 `dist/DroidGrab.exe` = 73,228,972 字节（约 69.8 MB，0.6.0 构建） | 估算已由实测值取代 |
| 2.12 | :176 | 增量包明确报错，不产出残缺 img | `PayloadExtractor.cs:189` | 无需处理 |

### 3. 已改掉或已推翻

| # | 位置 | 原文怎么写 | 现在是什么 | 处理建议 |
|---|---|---|---|---|
| 3.1 | :107 | 默认尺寸 1080×720，最小 900×600 | `MainForm.cs:89-90`：`ClientSize = 1060×730`，`MinimumSize = 900×660` | 更新为实际值 |
| 3.2 | :107 | Per-Monitor DPI 感知 | 已实现：csproj 声明 `ApplicationHighDpiMode=PerMonitorV2`；`app.manifest` 里只保留 `longPathAware` 与 `supportedOS`（两处同时声明会触发 WFAC010 警告） | 已落实，文档按此写 |
| 3.3 | :108 | 暗色为默认主题，Win11 下套用深色标题栏 | 实际为浅色主题（`Ui/Theme.cs` 的 CardFill / LogText；CHANGELOG 0.5.32「日志区浅色」、0.5.35 统一 CardFill）；未搜到 DWM 深色标题栏调用 | 已推翻：改为记录「浅色自绘主题」并说明理由 |
| 3.4 | :109 | 微软雅黑 UI / Cascadia Mono（缺失回退 Consolas） | UI 字体是微软雅黑 UI（`PickListDialog.cs:49` 显式 `Microsoft YaHei UI`，主窗体走 `Theme.Ui(...)`）；日志实际用 `Consolas 9F`（`MainForm.Layout.cs:343-350`） | 把「Cascadia Mono」改成 Consolas，其余一致 |
| 3.5 | :86-89 | 布局含「[选文件] [选目录] [开始] [暂停] [取消]」 | 实际为「浏览…」「选择…」「开始提取」「取消」（`MainForm.Layout.cs:46,49,53,61,64`），没有「暂停」；取消点击后状态立即转「正在取消…」（`MainForm.cs:135`） | 标注：暂停未实现 |
| 3.6 | :91 | 下载线程 32、提取线程 8 | 实际默认下载 `clamp(核数×2, 8, 16)`、提取 `clamp(核数, 4, 32)`，滑块范围 1–32（`MainForm.Layout.cs:137,139,225-227`；`AppSettings.cs:13-15`） | 更新默认值描述；上限 32 不变 |
| 3.7 | :95-96 | 总进度条带速度与剩余时间、「当前：system · REPLACE_XZ 解压中 · 分片 812/2048」 | 已实现两行进度区：第一行粗体 + 进度条 = 当前分区进度，第二行灰字 = 速度 + 总量（`MainForm.Run.cs:250-266`）；完成时状态栏与日志给出耗时与平均速度（`:143-145`）。「剩余时间」与「分片 N/M」未核实 | 基本一致：去掉未核实的两项，或补实现 |
| 3.8 | :98-103 | 分区表列为 分区/大小/状态/耗时/校验；日志区带 [导出] [清空] | 结果表固定六列：分区 / 镜像、大小、状态、进度、耗时、备注（`MainForm.Layout.cs:336-341`，拆分出的 super 行同样六列）；日志区无 [导出]/[清空] 按钮 | 更新列名；日志导出改用环境变量 `PDP_LOG`（`MainForm.Helpers.cs:225-229`），如需按钮另开任务 |
| 3.9 | :119 | 设置持久化到 `%LOCALAPPDATA%\PayloadDumperPlus\settings.json` | 实际优先写 exe 同目录 `settings.json`，不可写才退到 `%LOCALAPPDATA%`（`AppSettings.cs:5,20-24`；CHANGELOG 0.3.0） | 更新为「优先 exe 同目录，绿色版不写注册表」 |
| 3.10 | :120 | 不弹模态框，所有提示走顶部横幅 | 完成时弹模态小窗 `DoneDialog`（`DoneDialog.cs:8,47`；CHANGELOG 0.3.0），错误直接写日志与备注列 | 已改：按实际情况记录 |
| 3.11 | :126 | 默认并发 32；总长 < 32×4MB 时降到 `⌊总长/4MB⌋` | 旧整包下载器的固定 32 分段已删除；现在并发上限是「在飞 Range 请求数」，1–32 由界面滑块或 `PDP_CONNECTIONS` 设定（`IRandomReader.cs:313-331`，`PDP_CONNECTIONS` 运行时覆盖、每次取许可前重读），GUI 初值 `clamp(核数×2, 8, 16)`（`MainForm.Layout.cs:136`）、出厂配置值 16（`AppSettings.cs:14`） | 更新：并发对象改为在飞 Range 请求数 |
| 3.12 | :127 | `HEAD` 探测，HEAD 被拒(403/405) → `GET Range: bytes=0-1` | 从不发 HEAD，直接 `GET Range: bytes=0-0`（`SegmentedDownloader.cs:106-112`；CHANGELOG 0.3.3、0.5.6） | 更新；理由：部分 CDN 的 HEAD 多一次 TLS 往返 |
| 3.13 | :128 | 不支持 Range → 单流回退 + 横幅提示 | 未实现单流回退；按需直读路径在服务端不按 Range 返回时报错，不静默降级（`IRandomReader.cs:167-311`） | 已改：明确报错而非降级下载 |
| 3.14 | :129 | 等分分段，最小片 4MB，段内 1MB 缓冲 | 远程随机读块 2 MB、LRU 缓存上限 384 MB（`IRandomReader.cs:70-71`）；旧的固定 32 分段与 1 MB 下载缓冲随整包下载器删除 | 更新为实际值 |
| 3.15 | :130 | `FileStream` + `Seek` 各写各段；缓存目录 `%TEMP%\PayloadDumperPlus\<url哈希>\` | 分区镜像仍为定点写（`PayloadExtractor.cs:566-583`）；整包分片写入与缓存目录机制已随整包下载器删除，Core 的 `PipelineOptions` 已无 `CacheDirectory`（App 层的 `CacheDirectory` 字段与设置项也已删除，在 `src/` 的 `.cs` 源文件里搜 `CacheDirectory` / 「下载缓存目录」为 0 命中）；PRD 旧路径 `%TEMP%\PayloadDumperPlus` 已不再使用 | 更新：按需直读，无中间包缓存 |
| 3.16 | :132 | 状态文件 `state.json`，原子写 temp+rename，每完成一片刷一次 + 每 2s 心跳 | 续传状态与 `DownloadState` 已随整包下载器删除，Core 内 grep `pdstate` / `DownloadState` 为 0 命中；不提供断点续传 | 该条随机制删除，README 与 FEATURES 都不写「断点续传」 |
| 3.17 | :134 | 单片失败 3 次（300/900/2700ms 退避）；仍失败则二分重排 | 远程读取最多 4 轮尝试，退避 500ms×轮次（`IRandomReader.cs:250,296,302`）；**没有二分重排**；旧 `SegmentedDownloader.cs:74` 的「二分重试」注释已随文件精简消失 | 更新为实际值 |
| 3.18 | :135 | 限速时横幅提示「建议降至 N」+ 一键降档 | 实际为自动升降档：吞吐下滑 >30% 减半、回升 >20% 逐档加回、连续三次采样 <400 KB/s 且档位 >1 再降一档（`IRandomReader.cs:388-499`）；设置里的「自动降档」关闭后只按用户设定（`:394-395`）；档位变化写日志（`MainForm.Run.cs:63`）；无横幅、无一键降档按钮 | 已改：自动档位取代提示 |
| 3.19 | :149 | HDD 上提供「顺序写模式」开关 | 未搜到该开关 | 未实现：要么补做，要么从设计与文档中移除 |
| 3.20 | :181 | 性能预期：raw 1~2GB/s；xz 8 核约 300~500MB/s | 仓库内没有对应的实测记录 | 未验证：补测前不得作为结论引用 |
| 3.21 | :27 | 8 核 NVMe 上相对单线程 ≥3x（xz/bz2）、raw ≥2x | 无实测数据 | 未验证：标注为待测目标 |
| 3.22 | :152-168 | 架构目录含 `Core/Progress` | 实际 `Core` 下为 Archive / Compression / Download / IO / Payload / Pipeline / Proto / Sparse / Super；进度条在 App 层（`App/ProgressStrip.cs`） | 更新目录树 |

### 4. 未实现或未核实

| # | 位置 | 原文怎么写 | 现在是什么 | 处理建议 |
|---|---|---|---|---|
| 4.1 | :113 | 拖拽任意文件、目录、浏览器链接 | 仅核实到文件拖入（`MainForm.cs:95,120`；`MainForm.Run.cs:15-18`）；目录与浏览器链接未核实 | 待确认 |
| 4.2 | :114 | 多任务队列，粘贴多行链接 = 多任务 | 源码未搜到队列实现 | 未实现 |
| 4.3 | :115 | 快捷键 `Ctrl+V` / `Ctrl+O` / `Ctrl+E` / `F5` / `Del` | 未搜到全局快捷键 | 未实现（滑块支持方向键与 Home/End/PageUp/PageDown，`Ui/FlatSlider.cs:287-331`） |
| 4.4 | :116 | 双击行打开目录；右键菜单复制路径/重试/删除 | 双击已实现且更精确：在资源管理器里定位并选中该镜像（`MainForm.Helpers.cs:11-30`，`explorer.exe /select`），找不到对应文件时退回打开输出目录（`:32`）；右键菜单未搜到 | 双击一致；右键菜单未实现 |
| 4.5 | :117 | 失败整行标红 + 原因气泡 | 失败整行已标红（`MainForm.Run.cs:351,367`，`ForeColor = Color.FromArgb(200, 60, 60)`）；失败原因写入备注列与日志（`:162-164`、`:227-233`）；未搜到独立的气泡控件 | 标红一致；气泡以备注列呈现 |
| 4.6 | :20 | 跟随 301/302 重定向 | 未见显式配置，依赖 `HttpClient` 默认行为 | 待确认：建议显式配置并写进文档 |
| 4.7 | :25-29 | 验收线含「日志可导出」 | 无导出按钮；可用环境变量 `PDP_LOG` 落盘（`MainForm.Helpers.cs:213-229`） | 部分实现：明确为「日志可写盘」 |
| 4.8 | :36-38, :47-65 | 输入源含三星 `tar.md5`、MTK 散装目录 | 源码内未搜到 `.tar.md5` 处理与散装目录扫描 | **已确认不支持**：已从 README 与 `docs/FEATURES.md` 的输入格式中移除，只在 README 的「已知限制」保留一句 |
| 4.9 | :131 | 空间预检阈值 = 包大小 × 1.2 | 倍数（×1.2）未核实；整包下载器精简为 191 行后，需重新确认空间预检是否仍然存在 | 待确认 |
| 4.10 | :139 | 文件名取 `Content-Disposition`，无则 URL 末段，再退化 `download_<哈希前缀>` | 已核实取 `Content-Disposition`（`SegmentedDownloader.cs:167-168`）；退化链未核实 | 部分待确认 |
| 4.11 | :183-196 | 开发计划「冒泡 A / 冒泡 B」与「仍需你提供」 | 0.5.37 已发布，计划段落已完成使命 | 归档，不再作为现行计划 |

### 5. 交叉引用

- 功能与优势的逐条展开见 `docs/FEATURES.md`。
- 面向使用者的输入格式、命令行、输出物与限制见 `README.md`；系统要求以 README 为准（Windows 10 1607 及以上 / Windows 11，x64；开发与实测环境为 Windows 10 IoT Enterprise LTSC 2021（19044），Win11 未做实机验证）。
- 早前记录的三个过期注释（`IRandomReader.cs` 的「4MB 分块」与「60 秒单次尝试」、`SegmentedDownloader.cs` 的「二分重试」）已随两个文件重写消失：现在块大小常量是 2 MB（`IRandomReader.cs:70`）、单次尝试 30 秒（`:235`）、最多 4 轮尝试（`:250,296`）。

### 6. PRD 未描述、本次已实现

| # | 位置 | 原文怎么写 | 现在是什么 | 处理建议 |
|---|---|---|---|---|
| 6.1 | —（PRD 无此设计） | 提取中关闭窗口会直接退出 | 关窗前弹确认框：标题「还在提取」，正文「正在提取，确定要退出吗？没下完的文件会被丢掉。」，按钮「是 / 否」且默认焦点在「否」；选「否」取消关闭，选「是」取消提取并最多等 3 秒收尾；关机 / 注销不弹框（`src/PayloadDumperPlus.App/MainForm.cs:187-224`，只处理 `CloseReason.UserClosing`） | 已补进 README 与 `docs/FEATURES.md`（4.7） |
| 6.2 | —（PRD 未写解析错误行为） | 未知参数会被当成输入源 | 不认识的 `--xxx` 弹「不认识的参数」提示并以退出码 2 退出，参数后面的值不再被当成输入源；`--out` / `--parts` 的值也不会（`src/PayloadDumperPlus.App/Program.cs` 的 default 分支） | 已补进 README 命令行说明与 FEATURES 待确认第 1 条 |
| 6.3 | :107（PRD 有「Per-Monitor DPI」承诺） | 承诺 Per-Monitor DPI 但未说明落点 | 由 csproj 的 `ApplicationHighDpiMode=PerMonitorV2` 声明；`app.manifest` 只保留 `longPathAware` 与 `supportedOS`（Win10/Win11、8.1、8），并以 `asInvoker` 运行、不提权 | 见 3.2，已落实 |
