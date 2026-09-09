# P2 Relay 实现与 SHA-A 交接

日期：2026-09-10。主机角色：Windows 开发，Windows build 26200，.NET SDK 10.0.400、Python 3.12.6。从干净的 P0 `d9deae2b6d841dfdc17c8cadcad9b226dff7344d` 开始；仍在私有 `feature/cloud-clipboard-v1` 上提交，不合并 main。SHA-A 是本报告所在的最终实现提交，以发送方最终消息和接收方 Git 核实为准，不能用 P0 SHA 部署。

## 实际实现

- `src/ContinuityBridge.Contracts`：net10.0，协议版本、JSON 约定、正文之外的 DTO。
- `src/ContinuityBridge.Relay`：net10.0 ASP.NET Core Web，全部八个契约操作。现有八个 Windows 工程、SDK/global.json 和原 Core StateCoordinator 保留；可移植依赖图不经过它们。
- 单进程、单组、单槽内存正文；text/image 相互替换。强 If-Match、设备范围 UUID 幂等键、带长度分隔的 SHA-256 请求指纹。校验成功后在同一锁中结算过期、查询幂等、比较条件和提交。
- 幂等记录只含指纹、操作、结果元数据、最初到期时间；有容量/TTL 淘汰，不保存 byte[]/正文/旧 Snapshot。重放返回原 result 与当前 state，不续期、不复活。
- 下载只访问当前未过期 itemId，长度/hash 与元数据一致。替换/清空/到期取消在途旧下载；下载最大 30 秒，调用方仍必须下载后核验云状态和本地 generation。已传输或已进入网络缓冲的内容不能召回。Range 被忽略，完整 200，旧项 410。
- 设备凭据由服务端 provisioning 工具随机生成，映射服务端设备 ID；服务端只加载 token 的 SHA-256。鉴权在大正文读取前，提交前复核，撤销不会开放重放。全部 v1 接口含 events 均鉴权。
- WebSocket 发送 ≤4 KiB 的 changed 提示，初连/重连带当前 epoch/revision，约 250ms 合并变化，15 秒重复当前提示兼作心跳；没有历史消息队列。REST 为权威。连接上限与发送、关闭超时有界。
- v1 只有 none。未知 crypto mode 返回 415；文本额外 envelope 字段拒绝；升级位置仍是独立版本协议，没有伪 E2EE。
- 所有响应 no-store；错误固定形状、不回显输入。关闭默认 ASP.NET 请求日志 provider，避免 URL/item ID/正文/凭据进入日志。P3 需要另审代理日志。

## 本阶段实施决策（ADR-002）

限额来自 `RelayOptions` 同一实例和 capabilities。数值配置启动时校验，不热更新；更改配置须重启，会清空正文、幂等记录并生成新 epoch。不存在配置修改追溯延长旧正文的行为。设备注册文件单独热读，约 250ms 后台刷新；请求也刷新，损坏/缺失/重复设备配置失败关闭鉴权。使用原子替换文件，已撤销的 WSS 发送关闭帧并最多等待 2 秒握手。

默认 upload=2、download=4、WSS=16，普通连接最多 128、每 HTTP/2 连接最多 16 streams；无上传/下载排队。上传 30 秒、下载 30 秒，超额并发 429。正文流逐字节计数，不信 Content-Length；Kestrel 总正文限额关闭，因为其 chunked 限额还计帧开销，实际正文由认证后、已取得并发名额的固定容量读取器强制限制。没有请求落盘或无界消息缓冲。

启动校验的保守 payload 预算为 `uploads × (6 × max(imageBytes,jsonBytes) + 8 × textBytes) + (downloads+1) × max(imageBytes,textBytes)`；默认 356,000,000 bytes，不得超过配置的 512,000,000。此数不包括 CLR、TLS 和 Kestrel 固定开销，不代表进程 RSS 上限；P3 要测真实 RSS，容器限额需另留余量。图片结构最多 65,536 chunks/markers，像素最多配置值（实现硬上限 64M），避免时间和元数据数组无界。

PNG 验签名、块边界/类型/顺序/CRC、尺寸/色型/位深、palette/tRNS、禁止动画、IDAT zlib/Adler、逐行过滤标记和精确展开字节数，支持 Adam7；只用 16 KiB 展开窗口，不重建像素。JPEG 验 SOI/EOI、marker 长度、8-bit baseline/progressive frame、尺寸、采样、量化/Huffman 表结构和 scan/entropy 边界；不验证全部熵编码/像素语义，也不承诺识别一切坏图。其他 JPEG 编码及 APNG/GIF 拒绝。**P4/P5 客户端仍须受限解码，失败不能写入剪贴板。** 这保留 PNG/JPEG 产品范围，同时明确服务端验证边界。

## 运行与可移植测试入口

Windows 仓库根：

```powershell
./scripts/dev/test-relay.ps1
```

脚本按 `CONTINUITYBRIDGE_DOTNET` → checkout `.tools/dotnet/dotnet.exe` → PATH，使用现有 checkout NuGet 缓存（若存在）和锁文件；不安装工具。执行真实本地 loopback 进程、合成证书 TLS/WSS、状态测试及 Python 黑盒；不会访问系统剪贴板。已有 restore 时可用 `-SkipRestore`。

Linux/Mac 的独立 checkout（只有已安装且满足 global.json 的 SDK/Python 时）：

```sh
sh scripts/dev/test-relay.sh
```

该脚本是显式测试动作，不属于 setup。单独构建入口为 `dotnet build ContinuityBridge.Portable.slnx -c Release`；服务器发布入口：

```sh
dotnet restore src/ContinuityBridge.Relay/ContinuityBridge.Relay.csproj --locked-mode
dotnet publish src/ContinuityBridge.Relay/ContinuityBridge.Relay.csproj -c Release --no-restore -o artifacts/relay-publish -p:UseAppHost=false
```

本机单独手动启动示例（生成的是新的本地测试组；已有目录会拒绝覆盖）：

```powershell
$sdk = './.tools/dotnet/dotnet.exe'
& $sdk artifacts/relay-publish/ContinuityBridge.Relay.dll --provision .local/relay-test
$env:Relay__DeviceFile = (Resolve-Path .local/relay-test/devices.json).Path
$env:ASPNETCORE_URLS = 'http://127.0.0.1:5087'
& $sdk artifacts/relay-publish/ContinuityBridge.Relay.dll
```

工具创建两套 32-byte CSPRNG token 与随机设备 ID，将原 token 只写 `client-tokens.json`，服务端 `devices.json` 只含 ID/hash/revoked。不在 stdout 打印秘密。生产必须选择受限目录（Windows 审核 ACL；Unix 创建文件模式 0600），客户端文件不挂载给 Relay、不分享。把设备对应记录设 revoked=true 后原子替换注册文件可撤销；重新生成新 token 对应新记录轮换。没有远程管理/配对路由。

## Fixture 与 P4 注入

```sh
python tests/relay-blackbox/fixtures.py artifacts/relay-fixtures
```

生成合成 Unicode 文本、2×2 透明 PNG、2×2 JPEG、19,999,999/20,000,000/20,000,001-byte PNG，以及名称/长度/SHA-256 manifest。大图为 2500×1999 RGBA，alpha 包含 0/85/170/255。使用合法无压缩 zlib scanlines，再在 IEND 前放有长度与 CRC 的私有 ancillary `npAD` 块精确补足字节；不是在 PNG 尾部随便补垃圾。fixture 无个人图文。边界图传输 hash 必须与本机 manifest 相同；客户端重编码另记录尺寸、像素、alpha 与运输 hash。

P4 在已批准 staging 使用安全方式把入口及一个合成测试设备 token 加载为当前进程环境变量 `CB_BASE_URL`、`CB_TOKEN_A`。不要把 token 放命令行、共享脚本、截图或模板。然后：

```sh
python tests/relay-blackbox/inject.py artifacts/relay-fixtures/transparent.png
python tests/relay-blackbox/inject.py artifacts/relay-fixtures/rgba-20000000.png
python tests/relay-blackbox/inject.py artifacts/relay-fixtures/unicode.txt
```

每条命令先校验 fixture manifest，GET 当前状态，再用原样 If-Match 和新 UUID 提交到测试槽。竞争错误直接失败，不夺回状态。iPhone 使用 CB 取回→实际粘贴，并核验错误不污染旧剪贴板。API 注入只证明 API→iPhone，不能算 Windows→iPhone E2E；P4 还需真正 iPhone 图片上传。20,000,001 fixture 是预期 413 的负例，不能作为成功注入。

独立黑盒：安全加载 `CB_BASE_URL`、`CB_TOKEN_A`、`CB_TOKEN_B` 后运行 `python tests/relay-blackbox/run.py`。只用 Python 标准库和线上协议，无 .NET 对象、调试路由或内部状态；支持 HTTPS/WSS 默认可信证书，也支持明确 `CB_CA_FILE` 的测试 CA，绝不跳过 TLS 校验。会改写并最终清空测试组，要求两 token 属于同组，默认 20MB 配置和 upload≥2。未来 Cloudflare 适配可直接使用。

## 本次验证记录

最终代码运行 `scripts/dev/test-relay.ps1 -SkipRestore`：退出 0，**17/17 .NET 测试 PASS、0 skipped；9/9 黑盒组 PASS**。.NET 测试覆盖 fake clock 的到期前一 tick/精确到期、并发 sweep、CAS/同键串行、缓存淘汰、旧条件/epoch、旧正文弱引用可回收与缓存对象图、取消/空清空/无效配置，以及真实 HTTPS/WSS 进程的撤销/重连/重启、未读即丢失的 HTTP 响应重试、断开与超时上传、并发名额恢复、慢下载替换/到期和变更限额。原始 TRX 位于 ignored `artifacts/relay-test-results/relay.trx`。

| 命令/检查 | 结果及边界 |
|---|---|
| `dotnet restore ContinuityBridge.Portable.slnx --locked-mode`（脚本使用现有 NuGet cache） | PASS / exit 0；锁定依赖，不改变旧项目；第一次使用默认空 cache 的 restore 等待已停止，改用仓库原有 cache 后正常 locked restore 成功 |
| `dotnet build ContinuityBridge.Portable.slnx -c Release --no-restore` | PASS / exit 0，0 warnings、0 errors；静态项目引用仅 net10.0 |
| `scripts/dev/test-relay.ps1 -SkipRestore` | PASS / exit 0，17 个 .NET 状态/真实进程测试，9 组独立 wire tests；本机 TLS 使用每次新建并精确校验证书的合成 loopback 服务 |
| `dotnet publish … -c Release --no-restore -p:UseAppHost=false` | PASS / exit 0，Windows 上 framework-dependent 发布；**不是 Linux 执行证据** |
| `contracts/validate.ps1` | PASS / exit 0，46 Schema 正反例及引用/8 HTTP 操作库存；其 NOT RUN 输出仅是该离线脚本本身的范围 |
| `fixtures.py` + 已安装 Pillow 独立 load | PASS / exit 0，三张 size±1 大 PNG 均为 2500×1999 RGBA、alpha extrema 0..255；小 PNG/JPEG 也实际解码；不算 iPhone 粘贴 |
| `inject.py` 对本机真实 Relay 注入透明 PNG、20MB PNG、Unicode 文本 | PASS / exit 0，manifest 校验和 API 提交通过；P4 真机未运行 |
| 当前 staged 31 文件 allowlist + 启发式 secret scan + `git diff --cached --check` | PASS，未发现 token/私钥/密码字面量/凭据 URL 或 artifacts/secrets 路径；本机无 gitleaks/trufflehog，不称为专用扫描器认证 |
| Docker/Linux 容器、VPS/公网代理 TLS/WSS、实际等待 30 分钟、Win32/iPhone/Shortcuts、生产部署 | **NOT RUN**；本机缺 Docker，Linux 交给 P3；不安装系统依赖，不连接 VPS，不更新历史 G2 结论 |

已修复并回归的首轮失败：Kestrel chunk framing 被计入边界限额而误拒绝合法 20MB；WSS 撤销后过早 Abort 丢失 close frame。最终测试无未修复 FAIL。测试没有模拟物理网络断包；丢响应用真实 TLS 连接提交、独立连接确认已提交、从不读取提交响应即断开，再用原条件/原键重试。未测全套公网丢包/乱序或客户端轮询策略，后者属于 P5/P6。

## P3 部署输入与仍需验收

- 文件：`deploy/relay/Dockerfile`、`.dockerignore` allowlist、`relay.env.template`、拒绝直接使用的 `devices.template.json`，及本报告/锁文件/portable tests。构建命令为 `docker build -f deploy/relay/Dockerfile -t continuitybridge-relay:sha-a .`，标签由 P3 换成真实 SHA-A。
- SDK 基础标签 10.0.400、ASP.NET 运行时标签 10.0；**P3 必须核实目标 Linux 架构对应标签、补丁与 digest，并记录 source SHA→image digest**。本机没有 Docker，不把 Dockerfile 文本审查或 Windows publish 当镜像构建通过。
- 镜像只含 Contracts/Relay 发布文件，没有 QA sidecar、Windows-only DLL、测试证书、token、fixture 或原始日志；非 root 用户，内部 HTTP 8080。P3 计划应只向受控反代绑定入口，提供公网可信 HTTPS/WSS；不得直接公开裸 HTTP listener。
- 单实例单设备组，不可多副本或负载均衡到独立内存槽。独立 staging/production credentials/实例。受限文件挂载包含设备 hash 映射。P3 需明确主机配置、代理不缓存/不缓冲落盘、不记录 Authorization/正文/hash、swap/dump、内存限额、restart 行为和回滚目标。
- P3 在精确 SHA-A 运行 portable tests、外部 HTTPS/WSS 黑盒、真实配置修改/撤销、重启、容器限制和至少一次实际等待 30 分钟到期。生产仍需 P6/P7 门槛。用户审批具体 P3 plan 后才部署；P2 没有执行 VPS、网络配置、Docker、真剪贴板或 iPhone 操作。

## 依赖与来源

Relay/Contracts 新增 **零第三方 NuGet 运行时依赖**；框架使用既有 .NET/ASP.NET Core 10，MIT，[runtime 上游](https://github.com/dotnet/runtime)、[ASP.NET Core 上游](https://github.com/dotnet/aspnetcore)。状态/进程测试复用仓库中央锁定的 MSTest 4.0.2（MIT），理由是保持现有测试工具，许可证与上游见 [testfx/LICENSE](https://github.com/microsoft/testfx/blob/main/LICENSE)。新增工程有 packages.lock.json，未更新旧工程版本。

Python 黑盒/fixture 工具只需标准库（PSF，[Python license](https://docs.python.org/3/license.html)），没有 pip 安装或服务端依赖。已安装 Pillow 12.1.1 只用于本次独立解码验证及一次性生成合成 JPEG 字节，不是构建/测试运行的强制依赖；[上游许可证](https://github.com/python-pillow/Pillow/blob/main/LICENSE) 为 MIT-CMU 与历史 PIL 许可。

结构检查依据 [PNG 规范](https://www.w3.org/TR/png-3/)、[JPEG T.81](https://www.w3.org/Graphics/JPEG/itu-t81.pdf)；服务器限制与 WebSocket 行为复核 [Kestrel](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel?view=aspnetcore-10.0)、[WebSocket 官方文档](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/websockets?view=aspnetcore-10.0)。没有把这些资料或编译成功计作 Linux/手机真实验收。
