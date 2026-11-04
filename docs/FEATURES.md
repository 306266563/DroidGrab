# 特色与优势：DroidGrab 相对原版 payload_dumper

本文逐条对比本版本与手机端 Python 工具 `payload_dumper`（下称「原版」）。

**依据口径**

- 本文所有代码引用均相对项目根目录，格式为 `路径:行号`，行号与版本号取 0.6.0 源码。文中出现的 0.4.x、0.5.x 是 CHANGELOG 里的历史版本号，用于说明某项改动从哪一版开始。
- 原版源码不在本仓库内。凡「原版怎么做」一栏没有明确依据的，一律标注**待确认**，不做推测。
- 仓库内可用于说明原版行为的记录只有两处：重写前的 `README.md:51-59` 对比段、`CHANGELOG.md` 的版本说明；文中以「重写前的 README」和版本号标注。

---

## 一、性能

### 1.1 下载：逐 operation 串行请求 → 按需分块拉取 + 在飞请求受控

- **原版怎么做**：每个 `InstallOperation` 单独发一次 HTTP Range 请求，且串行执行（重写前的 README:55）。
- **本版本怎么做**：只读取 manifest 和真正要提取的数据块，不下载整包（CHANGELOG 0.2.0；`src/PayloadDumperPlus.Core/Archive/HttpStreamingExtractor.cs:29`）；远程读取以 2 MB 为单位按需请求区间（`IO/IRandomReader.cs:70`）。并发对象是同时在飞的 HTTP Range 请求数，闸门在 `IRandomReader.cs`：`EnterRequest`（`:365-377`）取许可并记录实测峰值、`ExitRequest`（`:379-386`）归还，许可只在一次 HTTP 尝试期间持有；上限由界面滑块设定（1–32，也接受环境变量 `PDP_CONNECTIONS`，`:313-331`）。单次请求空闲 30 秒无数据即超时重连（`:235,267`），每读到一段数据就把空闲计时往后推（`:286-287`）。本地回环 HTTP Range 服务实测：上限 1 时实测峰值 1、上限 4 时实测峰值 4（22.06 MB 样本分 12 块并发读，两次读回内容与源文件逐字节一致）；`PDP_DEBUG=1` 时输出 `[pdp] 远程读取并发：Range 请求实测峰值 N（调度上限 N）`（`:520`）。
- **好在哪**：网络往返不再串行；慢链路上磁盘与流量按实际要提取的分区算，不必先拿到完整包；并发上限是实测生效的，不是纸面设置。
- **依据**：重写前的 README:55；`HttpStreamingExtractor.cs:29`；`IRandomReader.cs:70,235,250,279-288,296-302,313-331,365-386,520`；CHANGELOG 0.2.0、0.5.9、0.5.15。

### 1.2 并发档位自适应，滑块是上限

- **原版怎么做**：串行执行，不涉及并发档位（重写前的 README:55-56）。
- **本版本怎么做**：界面上限与自动档位分开——滑块是上限（1–32），GUI 初值 = `Math.Clamp(Environment.ProcessorCount * 2, 8, 16)`（`src/PayloadDumperPlus.App/MainForm.Layout.cs:136`），出厂配置值 16（`src/PayloadDumperPlus.App/AppSettings.cs:14`）；`PDP_CONNECTIONS` 在运行时覆盖，每次取许可前重读（`IRandomReader.cs:314` 的 `UserCap()`），终值 `Math.Clamp(wanted, 1, 32)`；程序按实测吞吐自动升降：下滑超过 30% 减半、回升超过 20% 逐档加回、连续三次采样低于 400 KB/s 且档位大于 1 时再降一档；档位永远不超过用户设定，设置里的「自动降档」关掉后只按用户设定跑（`IRandomReader.cs:388-499`，其中 `:394-395` 是开关分支）。运行中拖动滑块立即生效：扩容时先还回缩容吃掉的许可，缩容时把多余许可收走（`:333-363`）；调档写 `[调档] 下载并发调整为 N` 日志（`src/PayloadDumperPlus.App/MainForm.Run.cs:63`）。
- **好在哪**：用户填的是上限而不是写死的线程数，程序在实测带宽允许的范围内自行试探；档位变化对用户可见（滑块同步 + 日志，CHANGELOG 0.5.22）。
- **依据**：`IRandomReader.cs:313-331,333-363,388-499`；`MainForm.Run.cs:63`；CHANGELOG 0.5.18、0.5.19、0.5.22。

### 1.3 提取：单线程顺序写 → 每 operation 独立定点写

- **原版怎么做**：本地文件路径只用 `dstExtents[0]` 的起点做 seek 后顺序写，仍是单线程（重写前的 README:56）。
- **本版本怎么做**：每个 operation 直接在自身偏移上写（`src/PayloadDumperPlus.Core/Payload/PayloadExtractor.cs:574`、`:583`）；连续的 extent 先合并成一段再一次写（`:566-571`）；全部写完后按分区大小 `SetLength`（`:696`）。
- **好在哪**：不同 operation 可以同时写同一文件的不同区域，互不阻塞，也不存在 seek 导致的相互覆盖；连续 extent 合并减少了写调用次数。
- **依据**：重写前的 README:56；`PayloadExtractor.cs:566-583,696`。

### 1.4 提取并行度：串行 → 分区与 operation 两级并行

- **原版怎么做**：串行执行（重写前的 README:55-56）。
- **本版本怎么做**：分区之间并行，分区内 operation 之间并行，用 `SemaphoreSlim` 逐任务准入（`PayloadExtractor.cs:344`）；并发度运行中可改（CHANGELOG 0.5.11、0.5.23）。
- **好在哪**：解压是 CPU 密集型工作，多核机器可以同时推进多个 operation；0.5.23 把并发控制从自旋轮询改为信号量等待，不再空转占 CPU。
- **依据**：`PayloadExtractor.cs:273-274,344,424`；CHANGELOG 0.5.11、0.5.23。

### 1.5 解压后端

- **原版怎么做**：待确认（原版的 xz/bz2 调用方式未在本仓库记录）。
- **本版本怎么做**：`CompressionKind { Xz, BZip2, Brotli }`（`src/PayloadDumperPlus.Core/Compression/Decompressor.cs:7-12`），xz 用 `XZStream`、bz2 用 `BZip2Stream`、brotli 用 `BrotliStream`（`:24-27`）。
- **好在哪**：不宣称自己做不到的事——单一压缩流仍是串行解码，并行收益来自上层的 operation / 分区调度，多核机器上同时推进多个分区。
- **依据**：`Decompressor.cs:7-12,24-27`。
- **注意**：不要把本版本描述成「xz 单流多线程解压」，代码里没有这个能力。

### 1.6 元数据按需读取

- **原版怎么做**：待确认。
- **本版本怎么做**：本地文件与 HTTP 直链实现同一个 `IRandomReader` 接口（`src/PayloadDumperPlus.Core/IO/IRandomReader.cs:10`）；`ReadExact` 只请求需要的字节，注释写明「为了看 64 KB 先下 2 MB 在慢速链路上是纯浪费」（`:16-20`）；远程块大小 2 MB、LRU 缓存上限 384 MB（`:70-71`）。
- **好在哪**：读 zip 尾部、中央目录、manifest 这类小读取不会触发整块预取；慢速链路上省下的是流量和时间。
- **依据**：`IRandomReader.cs:10,16-20,70-71`；CHANGELOG 0.5.7、0.5.9。

---

## 二、输入格式覆盖

### 2.1 包类型按内容判断

- **原版怎么做**：待确认（重写前的 README 未记录原版的识别方式）。
- **本版本怎么做**：按 magic 判断，不靠后缀猜（`src/PayloadDumperPlus.Core/Archive/PackageInspector.cs:30`）：gzip `0x00008B1F`（`:33`）、zip `0x04034B50`（`:34`）、payload `CrAU`（`:46`）。
- **好在哪**：改了扩展名的包也能正确识别，误判率低于后缀匹配。
- **依据**：`PackageInspector.cs:30,33,34,46,53`；PRD:22。

### 2.2 OTA 包：stored 直读 / 压缩存放解临时文件

- **原版怎么做**：待确认。
- **本版本怎么做**：stored 存放的 `payload.bin` 在 zip 内原地读取，不整体解压；被 deflate 压缩存放的才解到临时文件（CHANGELOG 0.1.0；`PackageInspector.cs:111` 临时目录 `%TEMP%\pdp-{Guid:N}`）。直链输入时按 2 MB 块按需拉取并边下边解，只读选中分区的数据块，整包不落地（CHANGELOG 0.2.0；`IO/IRandomReader.cs:70,235-288`、`Archive/HttpStreamingExtractor.cs:29`）。
- **好在哪**：不必先把完整包存到本地——磁盘占用与下载量按实际要的分区算，省磁盘也省一次读写。几 GB 的 OTA 包只取需要的部分即可。
- **依据**：CHANGELOG 0.1.0、0.2.0；`PackageInspector.cs:111`；`IRandomReader.cs:16-20,70-71`；`HttpStreamingExtractor.cs:29`。

### 2.3 线刷包流式解包

- **原版怎么做**：待确认。
- **本版本怎么做**：`tar` / `tgz` / `zip` 边读边解，只落选中的镜像（CHANGELOG 0.2.0）；识别 `.img` / `.img.br` / `.bin`（`src/PayloadDumperPlus.Core/Archive/LineFlashExtractor.cs:48`），排除 `meta-inf/`（`:157-163`）；保留归档内目录层级（`:168-169`）。`tar` / `tgz` 走单连接顺序流式解压——gzip 流不可随机跳读，只有 `zip` 能按需定位。
- **好在哪**：不需要先解出整包再挑镜像，磁盘占用与耗时都按实际需要的镜像计算。
- **依据**：`LineFlashExtractor.cs:48,157-169`；CHANGELOG 0.2.0。

### 2.4 只解指定镜像，命中即停

- **原版怎么做**：待确认。
- **本版本怎么做**：`onlyImages` 非空时只写指定名字（不区分大小写），全部命中后立即中断下载（`src/PayloadDumperPlus.Core/Archive/HttpStreamingExtractor.cs:29`）；命令行 `--parts` 与界面输入框都可指定，`--parts` 优先级更高（`src/PayloadDumperPlus.App/MainForm.Helpers.cs:66`）；传不带扩展名的 `boot` 会自动补 `.img`（CHANGELOG 0.5.3）。
- **好在哪**：只想拿一两个镜像时，不必把几 GB 的线刷包下完。
- **依据**：`HttpStreamingExtractor.cs:16,29,207`；`MainForm.Helpers.cs:66`；CHANGELOG 0.2.0、0.5.3。

### 2.5 super.img 拆分

- **原版怎么做**：待确认。
- **本版本怎么做**：按 LP metadata 拆分，geometry 魔数 `0x616C4467`、偏移 4096（`src/PayloadDumperPlus.Core/Super/SuperImage.cs:46-48`）；`_a` / `_b` 槽位后缀归并成逻辑名（`:24-29`）；默认拆开且默认保留原件（`src/PayloadDumperPlus.Core/Pipeline/ExtractPipeline.cs:89-90`）；拆出的镜像与已有同名文件冲突时加 `super_` 前缀（`LineFlashExtractor.cs:196-198`）。
- **好在哪**：super 包拿到手就能直接得到 `system`、`vendor` 这类可刷的镜像，同时原件不会被静默删除。
- **依据**：`SuperImage.cs:24-29,46-48,77`；`ExtractPipeline.cs:89-90`；`LineFlashExtractor.cs:193-198`。

### 2.6 sparse 镜像展开

- **原版怎么做**：待确认。
- **本版本怎么做**：按 sparse magic `0xED26FF3A` 识别（`src/PayloadDumperPlus.Core/Sparse/SparseImage.cs:10`），支持 `ChunkFill`（`:16`、`:138`）等块类型，展开为 raw。
- **好在哪**：线刷包里常见的 sparse 格式不需要额外工具转换。
- **依据**：`SparseImage.cs:10,16,22,39,138`；CHANGELOG 0.1.0。

### 2.7 直链输入

- **原版怎么做**：待确认。
- **本版本怎么做**：支持 http(s) 直链；探测用单次 `Range: bytes=0-0` 而非 HEAD（`SegmentedDownloader.cs:106-112`、CHANGELOG 0.3.3、0.5.6），总大小优先取 `Content-Range`（`:144-156`，CHANGELOG 0.4.0），文件名取 `Content-Disposition`（`:167-168`）；HTTP 客户端固定使用 HTTP/1.1（`:98-99`，注释说明部分 CDN 在 HTTP/2 下 Range 请求会挂住）。
- **好在哪**：不依赖服务器支持 HEAD，避免部分 CDN 的额外 TLS 往返（`:108-109` 注释）；进度分母一开始就正确。
- **依据**：`SegmentedDownloader.cs:98-99,106-179`；CHANGELOG 0.3.3、0.4.0、0.5.6。

---

## 三、稳定性

### 3.1 空闲超时与重试

- **原版怎么做**：待确认。
- **本版本怎么做**：远程读取单次请求空闲 30 秒无数据即超时重连，每读到一段数据就把空闲计时往后推，连接断开后从已收到的位置继续请求、不从零重来，最多 4 轮（`IRandomReader.cs:235,250,258,267,279-288,296-302`、CHANGELOG 0.5.9、0.5.15）；探测 20 秒超时并给出可直接照做的提示（`SegmentedDownloader.cs:116,127-138`、CHANGELOG 0.4.5）。连接阶段状态文字点号轮播，不再像卡死（CHANGELOG 0.5.6）；连续 20 秒没有新数据时给出重连提示（`src/PayloadDumperPlus.App/MainForm.cs:159`）。
- **好在哪**：慢链路与中途断流不会表现成「一直卡着不动」，失败后也能说清是偏移、超时还是没数据。
- **依据**：`IRandomReader.cs:235,250,258,267,279-288,296-302`；`SegmentedDownloader.cs:116,127-138`；`src/PayloadDumperPlus.App/MainForm.cs:159`；CHANGELOG 0.4.5、0.5.6、0.5.9、0.5.15。

### 3.2 校验

- **原版怎么做**：待确认。
- **本版本怎么做**：分区级 sha256 默认开启（`PayloadExtractor.cs:38`），入口检查 `new_partition_info.hash` 长度（`:446`），不匹配时报出期望值与实际值（`:455`）；单个 operation 的 `data_sha256_hash` 可选，默认关闭并在代码里注明「很慢」（`:40`、`:517-521`）。
- **好在哪**：默认路径下的产物可验证；对一个几 GB 的分区做逐 operation 校验代价高，所以做成开关而不是默认行为。
- **依据**：`PayloadExtractor.cs:38,40,446,455,517-521`；PRD:42-45；CHANGELOG 0.1.0。

### 3.3 失败不静默

- **原版怎么做**：待确认。
- **本版本怎么做**：不支持的操作类型直接抛错并带上类型名（`PayloadExtractor.cs:542`）；增量包明确跳过并给出原因「需要旧版镜像作 patch 源」（`:189`）；`super` 拆分失败只影响该步，其余镜像照常产出（`LineFlashExtractor.cs:136-139`）。
- **好在哪**：宁可报错也不产出看起来正常实际错误的数据。
- **依据**：`PayloadExtractor.cs:189,542`；`LineFlashExtractor.cs:136-139`；重写前的 README:49。

---

## 四、交互

### 4.1 先看清单再决定下什么

- **原版怎么做**：待确认。
- **本版本怎么做**：先读包开头 8 MB 列出镜像清单（`MainForm.Helpers.cs:146`），勾选后才下载并解出，全部命中后自动停止下载（`HttpStreamingExtractor.cs:29`）。
- **好在哪**：慢链路上可以先看清包里有什么、每个多大，再决定要哪些。
- **依据**：`MainForm.Helpers.cs:146`；`HttpStreamingExtractor.cs:29`；CHANGELOG 0.2.0。

### 4.2 分区选择

- **原版怎么做**：待确认。
- **本版本怎么做**：三列表格（勾选 / 名称 / 大小）+ 搜索过滤（`src/PayloadDumperPlus.App/PickListDialog.cs:88-95`），另有「全选」「全不选」「只选小的」「常用」按钮（`:126-129`）；搜索框带一键清空（`:53-59`、CHANGELOG 0.4.9）。
- **好在哪**：几十个分区里挑几个不必逐个找；「只选小的」适合先拿小镜像试通流程。
- **依据**：`PickListDialog.cs:53-59,88-95,124-129`；CHANGELOG 0.4.1、0.4.9。

### 4.3 进度呈现

- **原版怎么做**：待确认。
- **本版本怎么做**：进度区分两行——第一行粗体加进度条显示当前分区进度，第二行灰字显示速度与总量（`src/PayloadDumperPlus.App/MainForm.Run.cs:250-266`）；分区表格固定六列——分区 / 镜像、大小、状态、进度、耗时、备注（`MainForm.Layout.cs:336-341`，拆分出来的 super 行同样六列）；状态含「待机」「准备中…」「正在下载」「收尾」「完成」「已取消」「失败」（`MainForm.Run.cs:95,139,157,162,263`，CHANGELOG 0.5.16）；失败行整行标红（`MainForm.Run.cs:351,367`）；另有日志区（`MainForm.Layout.cs:343-350`）与自绘进度条（`src/PayloadDumperPlus.App/ProgressStrip.cs`）。
- **好在哪**：多分区并行时能看出是哪一个在动、卡在哪一步，而不是只有一个总进度条；失败项在表里一眼可辨。
- **依据**：`MainForm.Run.cs:95,139,143-145,157,162,250-266,351,367`；`MainForm.Layout.cs:336-350`；CHANGELOG 0.4.2、0.5.16、0.5.17。

### 4.4 双击定位结果文件

- **原版怎么做**：待确认。
- **本版本怎么做**：双击结果表某一行，在资源管理器里定位并选中对应的镜像文件（`MainForm.Helpers.cs:11-30`，`explorer.exe /select`）；该文件不存在时退回打开输出目录（`:32`）。
- **好在哪**：提取出十几个镜像时不必手动翻目录。
- **依据**：`MainForm.cs:139`（`_grid.CellDoubleClick`）；`MainForm.Helpers.cs:11-33`。

### 4.5 运行中可调，取消立即生效

- **原版怎么做**：待确认。
- **本版本怎么做**：下载并发上限滑块运行中可调——扩容时先还回缩容吃掉的许可，缩容时把多余许可收走，并以新上限重建采样基线（`IRandomReader.cs:333-363`、`:416-427`，CHANGELOG 0.5.18、0.5.19）；提取并发同样可改（CHANGELOG 0.5.11、0.5.23）；点「取消」后状态立即转「正在取消…」（`MainForm.cs:135`、`MainForm.Run.cs:157`）；卡住提示限频，60 秒最多一条（CHANGELOG 0.5.22）。
- **好在哪**：不用为了改并发重新开始一次提取；取消不会让人怀疑程序没反应。
- **依据**：`IRandomReader.cs:333-363,416-427`；`MainForm.cs:135`；`MainForm.Run.cs:157`；CHANGELOG 0.5.11、0.5.18、0.5.19、0.5.22、0.5.23。

### 4.6 界面实现方式

- **原版怎么做**：手机端界面，形态与交互均不同（重写前的 README:51-59 只记录性能差异，其余待确认）。
- **本版本怎么做**：WinForms 自绘控件：卡片与大圆角、低饱和冷灰底、单一强调色（小米蓝），代码注释写明风格取自小米「贴片」（`src/PayloadDumperPlus.App/Ui/Theme.cs:5-16`）；自绘复选框（`Ui/FlatCheckBox.cs`）、滑块（`Ui/FlatSlider.cs`，含键盘方向键与 Home/End/PageUp/PageDown 支持 `:287-331`）、按钮（`Ui/FlatButton.cs`）；窗体背景为屏幕截图降采样模糊（`MainForm.Backdrop.cs:81` 调 `BlurBackground.Capture`，`Ui/BlurBackground.cs:13,22,32`）；类注释说明了不用系统 Acrylic 的原因——Win10 上会让 WinForms 子控件整个画不出来（`MainForm.Backdrop.cs:6-9`）；表格与日志合并为一张圆角卡片（CHANGELOG 0.5.35）；自绘控件在 `OnPaint` 开头显式清屏以消除重影（CHANGELOG 0.5.30、0.5.31）。
- **好在哪**：视觉一致，控件行为可控；0.5.30/0.5.31 修的是自绘控件的重影问题。
- **依据**：`Ui/Theme.cs:5-16`；`Ui/FlatSlider.cs:287-331`；`MainForm.Backdrop.cs:6-9,65-81`；`Ui/BlurBackground.cs:13-56`；CHANGELOG 0.5.30、0.5.31、0.5.35。

### 4.7 提取中关窗先确认

- **原版怎么做**：待确认。
- **本版本怎么做**：提取进行中关闭窗口会先弹确认框，标题「还在提取」，正文「正在提取，确定要退出吗？没下完的文件会被丢掉。」，按钮是「是 / 否」且默认焦点在「否」（`src/PayloadDumperPlus.App/MainForm.cs:200-205`，`MessageBoxDefaultButton.Button2`）；选「否」则取消关闭（`:207-211`）；确认退出后取消提取，并最多等 3 秒让它收尾（`:213-224`）。关机、注销这类非用户主动关闭不弹框（`:196` 只处理 `CloseReason.UserClosing`）。
- **好在哪**：避免手滑关窗丢掉已经下了一半的镜像，也避免程序在收尾阶段被硬杀。
- **依据**：`MainForm.cs:187-224`。

---

## 五、部署

### 5.1 自包含单文件绿色版

- **原版怎么做**：运行在手机端，依赖 Python 环境。
- **本版本怎么做**：`net8.0-windows` 目标框架（`src/PayloadDumperPlus.App/PayloadDumperPlus.App.csproj:15`）；发布参数（win-x64 / 自包含单文件 / 压缩 / 无调试符号）已固化在 csproj，发布只需 `dotnet publish src/PayloadDumperPlus.App/PayloadDumperPlus.App.csproj -c Release`，产物为 `dist/DroidGrab.exe`。
- **好在哪**：不装 .NET 运行时，拷走即用；命令行不再需要手写一长串发布开关。代价是首次启动要解压原生库，冷启动约 1–2 秒（重写前的 README:81），以及约 70 MB 的体积。
- **依据**：`PayloadDumperPlus.App.csproj:15`；重写前的 README:68-81；CHANGELOG 0.1.0、0.5.37（`dist\DroidGrab.exe`，0.6.0 的发布产物 `dist/DroidGrab.exe` 实测 73,228,972 字节）。

### 5.2 不写注册表

- **原版怎么做**：待确认。
- **本版本怎么做**：设置优先写在 exe 同目录的 `settings.json`，该目录不可写时退到 `%LOCALAPPDATA%\PayloadDumperPlus\settings.json`（`src/PayloadDumperPlus.App/AppSettings.cs:5,20-24`，CHANGELOG 0.3.0）。
- **好在哪**：绿色版把 exe 和配置一起拷走即可迁移；受控目录下也不会因为写不进而丢配置。
- **依据**：`AppSettings.cs:5,20-24`；CHANGELOG 0.3.0。

### 5.3 许可与来源

- **原版怎么做**：独立的手机端 Python 项目（[vm03/payload_dumper](https://github.com/vm03/payload_dumper)），仓库内没有声明开源许可证（无 `LICENSE` 文件）。
- **本版本怎么做**：MIT 许可（仓库根 `LICENSE`，`PayloadDumperPlus.App.csproj:29` 标注 `<Copyright>MIT License</Copyright>`）；代码为 C# 独立实现，未复制原项目源码；程序自 0.5.33 起更名为 DroidGrab 并加入应用图标（CHANGELOG 0.5.33）。
- **好在哪**：许可清晰，允许商用与再分发；来源与致谢写在 README「致谢」一节。
- **依据**：`PayloadDumperPlus.App.csproj:29`；`LICENSE`；CHANGELOG 0.5.33。

### 5.4 系统兼容

- **原版怎么做**：Android 手机端（需 Python 运行环境）。
- **本版本怎么做**：Windows 桌面程序，`net8.0-windows`（`PayloadDumperPlus.App.csproj:15`）。
- **实测范围**：开发与实测环境为 Windows 10 IoT Enterprise LTSC 2021（19044）；Windows 11 未做实机验证。高 DPI 按 Per-Monitor V2 声明（`PayloadDumperPlus.App.csproj` 的 `ApplicationHighDpiMode`），清单里只留 `longPathAware`，并声明 Win10/Win11、8.1、8 的 `supportedOS`（`src/PayloadDumperPlus.App/app.manifest`）；程序以 `asInvoker` 运行，不提权。
- **待确认**：不再涉及 DPI——已由 csproj 的 `ApplicationHighDpiMode`（Per-Monitor V2）声明。Win11 的实际表现仍未实机验证。

---

## 六、待确认清单（需 Lead 判断）

| # | 事项 | 说明 |
| --- | --- | --- |
| 1 | 命令行参数（已核实） | 源码没有 `--threads`，文档不提。`Program.cs` 只解析 `<输入>` / `--auto` / `--out` / `--parts`（同时接受 `--out=目录` / `--parts=a,b` 写法）；`--out` 与 `--parts` 的值会被取走、不会当成输入源；不认识的 `--xxx` 会弹「不认识的参数」提示并以退出码 2 退出，`--xxx` 后面的值也不再被当成输入源。并发改用界面滑块或环境变量 `PDP_CONNECTIONS` 控制（后者在运行时覆盖，终值夹在 1–32）。 |
| 2 | 最低系统版本（已决） | 文档按「Windows 10（1607 及以上）/ Windows 11，x64」写，并注明开发与实测环境为 Windows 10 IoT Enterprise LTSC 2021（19044）、Win11 未做实机验证。`app.manifest` 只声明 Win10/Win11、8.1、8 的 `supportedOS`，没有更细的最低版本声明。 |
| 3 | 三星 `tar.md5`（已确认不支持） | PRD:36-38、47-65 列为输入源，源码内未搜到 md5 校验或 `.tar.md5` 处理；已从输入格式移除，并列入 PRD 校准第 4 节。 |
| 4 | MTK 散装目录（已确认不支持） | PRD 提到的散装目录扫描在源码内未搜到；已从输入格式移除，并列入 PRD 校准第 4 节。 |
| 5 | 性能数字 | PRD:27 的「≥3x」、PRD:170-181 的吞吐估算在仓库内没有实测记录，本文未采用。 |
| 6 | DPI 感知（已实现） | `PayloadDumperPlus.App.csproj` 的 `ApplicationHighDpiMode` 已声明 Per-Monitor V2；`app.manifest` 里刻意不再写 `dpiAware` / `dpiAwareness`（两处同时声明会触发 WFAC010 警告）。 |
| 7 | 「xz 多线程解压」表述 | 源码中单流解码是串行的，并行发生在 operation / 分区层，文档不应写成单流多线程。 |
| 8 | 过期注释 3 处（已随重写消失） | 旧 `IRandomReader.cs:62`「4MB 分块」、旧 `:211`「60 秒单次尝试」、旧 `SegmentedDownloader.cs:74`「分片失败会二分重试」三处注释都已被重写覆盖：现在块大小常量是 2 MB（`IRandomReader.cs:70`）、单次尝试 30 秒（`:235`）、最多 4 轮尝试（`:250,296`），代码中从未有过二分重排。 |
| 9 | 版本号（已落实） | `PayloadDumperPlus.App.csproj:41-43` 的 `Version` / `AssemblyVersion` / `FileVersion` 已全部是 0.6.0，本文按 0.6.0 表述。 |
| 10 | 重写前 README 的两项功能 | 「manifest 记录」与「可选 open 问题定位」在源码内不存在，已在 README 重写时移除。 |
| 11 | 整包分段下载器（已删除） | 「32 分段整包下载 + `.pdstate.json` 断点续传 + 下载缓存目录」机制已不存在：`Download/SegmentedDownloader.cs` 删除整包下载后只剩探测与 HTTP 客户端构造（795 → 191 行），并发改由 `IO/IRandomReader.cs` 的 Range 请求闸门执行，文档已按现状改写。 |
| 12 | 「下载缓存目录」设置项（已删除） | 该设置项与相关字段已从代码中删除：在 `src/` 的 `.cs` 源文件里搜 `CacheDirectory` / 「下载缓存目录」已无命中（只剩编译产物 DLL），`PipelineOptions` 也没有该属性。文档不再出现这个设置项。「下载并发」与「自动降档」两项保留，且并发闸门已真正生效。 |
