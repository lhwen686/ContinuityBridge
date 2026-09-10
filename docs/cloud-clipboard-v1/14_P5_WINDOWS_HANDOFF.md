# P5 Windows 候选交接

## 当前：TestAgent 安全修复候选

本轮从干净 `0784cbedbba68054cac2927a8b6ba1504dc7ae30` 建立 `codex/p5-testagent-safety`。候选完整 SHA 由最终交付与包内 `manifest.json/sourceSha` 给出，不在提交内自填未来 SHA。**仅本地修复；P6-W 暂停，真实 QA/VPS/P6-M/本次真实桌面剪贴板均 NOT RUN；不是 P6-W READY。** 下文原 P5 PASS 是历史记录，不转移给本次改动后的原生路径。

修复：移除首次 QA 验证前写 sentinel 的行为；补全服务身份/角色/绑定 session/租约验证；建立完整格式集合的独立有界内存快照，未知/超限/竞争拒绝接管；在 OpenClipboard 内 CAS 与所有权检查；统一取消、排空、条件恢复和超时关闭；恢复正文禁止上行。本次还通过本机真实 HTTPS 查出并修复 QA POST 被 RequestDelegate 重载丢弃 JSON、只返回空200的问题，回归会实际解析响应，不能再只看状态码。

TestAgent 阻止日常产品 App 与其并行运行。P6 所需自动同步改为 runner 内复用同一个 CloudSyncEngine、fixture 白名单桌面和独立的 staging 内存配置；恢复前停止并等待同步，不修改产品设置/凭据/自启。本次未增加 NuGet 包，测试项目新增 TestAgent 项目引用及对应锁图。

验证入口仍是默认 `scripts/dev/test-windows-cloud.ps1`（不加 `-DesktopFixtures`）。新增 `TestAgentSafetyTests` 使用 fake Clipboard 内存、合成文本/PNG/JPEG/DIB、假时钟、延迟任务、故障注入及本机临时 HTTPS QA；覆盖授权、身份/租约、未知/混合格式、独立快照、竞争、部分发布、停止/到期/错误、排空超时、恢复上传策略与 fixture 过滤。实际计数及干净候选复核由最终 TRX 交付；编译/回归失败的开发证据留在 ignored `artifacts/p5-safety/`，不计 PASS。

开发审阅回归：Core 24/24、Windows P5 28/28（其中新增安全17项）、契约49例 PASS，均零跳过；最终精确 SHA 另做干净候选复核。首次调用旧测试脚本沿用固定目录，覆盖了历史 `artifacts/p5/final/core.trx` 与 `windows-http-image.trx`；本轮已将默认输出改为每次独立目录，并提供 `-ResultsDirectory`。不把被覆盖的旧 TRX 当作仍然保留的原始证据；旧报告结论仍是历史记录。

剩余限制：仅 runbook 明确支持的完整格式集合，备份最多96,000,000字节/16种格式；不备份任意 OLE/HTML/RTF/文件。CF_BITMAP 只能稳定转 DIB 时备份。原生同步 API 不可强制中断，超时跳过恢复并保持隔离，不承诺 Win32 多格式事务、强杀/断电或第三方管理器的隐私行为。真实系统格式转换、真实用户复制与窗口停止/关闭场景需要用户另行确认后执行，当前 NOT RUN。

**给 P3：** 从自己的干净 checkout fetch 本修复分支并核实最终完整 SHA，准备新计划 ID，分别构建该 SHA 的 Relay 与独立 staging QA 镜像（传 CANDIDATE_SHA），记录 digest、配置/代理差异和当前运行镜像的回滚目标。Relay 业务端点没有因本修复增加变化；QA 新返回 service/role/sessionId，旧空响应不可用。只准备并审阅计划；本轮未连接或部署 VPS，不推断旧批准涵盖新版本。

**给 P6-W：** P3 经授权部署同 SHA、Mac 候选一致后，先确认用户桌面测试窗口及重要内容保管，再从新包双击 TestAgent EXE；本机填 QA runner 信息，勾选本次 staging 同步并填其独立凭据，人工授权开始。按 [QA runbook](windows/QA_RUNBOOK.md) 检查身份租约、快照和隔离状态；不能仅因本修复已完成输出 READY。`-DesktopFixtures` 是旧历史组合场景，没有本次恢复安全 gate，不能用作本次真机替代验证。

## 以下为原 P5 历史交接

本报告属于 `feature/p5-windows-client`。完整 SHA-B 以本文件所在最终交付提交和包内 `manifest.json/sourceSha` 为准，禁止用移动分支名代替候选。P3 更新 staging 和 P6 两端必须检出同一个完整 SHA。P5 没有部署 staging/production，没有运行 iPhone↔Windows E2E。

## 集成与实现

已从干净 P2 `190c75680206eea8ae54dcbe3fbc017587adf6e8` 整合 P4 `40e2901`（真实快捷指令及报告），并合入 P3 `d321750` 的脱敏模板/记录。P4 两个 `.shortcut` SHA-256 与安装说明一致，没有改动其字节。P4 真机 PASS 来自其交接报告，本机没有重跑 iPhone，也未在 Windows 解密重新审计 AEA1 内部结构。

新增云同步路径使用 Core/Windows/App，旧 Tailnet StateCoordinator 和历史 G2 测试保留并隔离；App 不再引用占位 Api。现有八个工程仍为 Windows 目标。新 QA Protocol/Sidecar 可移植；TestAgent 仅 Windows 用户会话。未引入新 NuGet 包，使用现有 SDK/锁定 MSTest、框架内 WinForms/WPF WIC、Win32 凭据管理器。

本地 generation 由当前 DWORD 的变化驱动，不用 uint 大小排序；WM 通知立即失效旧工作，正文读取保留 75ms OLE settle 和有界 OpenClipboard 重试。下载长度/hash/最终状态再核验，写入条件与 operation marker、自写序号在一次 OpenClipboard 内完成。Win32 多格式发布本身不提供系统事务：先分配全部内存，失败清除部分已发布句柄，不承诺灾难性原生失败可以恢复任意旧 OLE 数据。

编码和网络脱离 STA；只缓存一个在途正文，不持久化离线正文。断网未知结果用同一 key/条件/正文重试，在线 CAS 只允许一次新 key 竞争，旧离线候选不抢占。TTL/清空不清本地；已观察状态只保留当前元数据，重复制相同内容可以新提交。WSS 只唤醒 REST，正常 OS TLS；失败时 HTTP 轮询和退避。暂停取消活动网络和候选。

兼容新增认证端点 `GET /v1/identity`，从服务器认证身份返回 deviceId。旧 P4 请求和 capabilities 不变；P5 客户端遇到尚无该端点的旧服务拒绝启动数据同步。OpenAPI/Schema 与用例同步更新。客户端图片上限还受本机 16MP/估算320MB 工作内存预算限制；不缩图绕过。

## 本机验证范围

实际主机：Windows 11 build 26200、当前用户 Session 1、UserInteractive=true；SDK 10.0.400。初期开发验证在上述集成提交的 dirty 工作树执行；最终交付以干净候选重新执行的 TRX 和包内 manifest 记录为准，不把开发时旧 SHA 当最终候选。

| 检查 | 当前证据与范围 |
|---|---|
| Core 单元 | PASS：启动不上传、旧本设备云项、同内容过期重复制、序号回绕模拟、下载竞争、同键重试、最多一次 CAS、离线状态/年龄/epoch、暂停/清空 |
| Windows 图片单元 | PASS：PNG/JPEG 字节、DIBV5 alpha、上下方向/调色板、8种 EXIF 变换算法、合法20,000,000字节/超限、尺寸炸弹；算法模拟不称真实设备回绕/全部 EXIF App 验证 |
| 真实 HTTPS | PASS：本机临时 Kestrel+限定测试证书指纹，双身份正文完整性、CAS/重放/401；产品 TLS 未关闭验证 |
| QA HTTP/租约 | PASS：错角色、未知字段/路径fixture、错误SHA、重复会话、固定结果、租约/动作截止。是本机 sidecar，不是已部署 staging QA |
| 真实 Windows 窗口 | PASS：合成源编辑控件复制、长文本上传/读回、目标文本粘贴，PNG/JPEG→RichEdit图片粘贴，CF_BITMAP上传像素、PNG/DIBV5/DIB落地，文件忽略、私密DWORD、旧写入失效、自写无回传、清云/暂停保留本地 |
| TestAgent 窗口 | 已核查可见初始态、候选SHA、未启用状态、停止控件和关闭；staging READY/远程动作运行留待 P6 |
| 产品设置/单实例 | PASS：当前桌面初始告知、凭据遮蔽、同步/自启默认关闭、第二实例提示。真实登录自启与已连接托盘交互完整矩阵仍 NOT RUN |
| P3 Linux/容器/线上QA | NOT RUN by P5；需批准并执行 SHA-B 升级计划 |
| 登录/注销后的自启、外部目标App完整矩阵、其他Windows版本 | NOT RUN |
| iPhone↔Windows、蜂窝跨网、Mac关机/断USB、日常稳定性 | NOT RUN，P6/P8 负责 |

真实窗口首轮 FAIL 已保留。现场发现 CF_DIB 的系统合成 DIBV5 携带额外掩码字节，优先读取合成格式导致像素偏移；改用源格式顺序。另修复自写序号在锁外记录的竞争窗口，并恢复 OLE 延迟发布所需的读取 settle。修复后重新执行整轮，不删除失败证据、不降低像素断言。

原始证据只在 ignored `artifacts/p5/`：TRX、合成 bitmap 诊断、目标窗口图。测试证书/令牌随机生成，只在 ignored TLS 测试目录，绝不进入包或 Git。旧 G2 报告保持原结论。

## 重现与交付

```powershell
pwsh -NoProfile -File docs/cloud-clipboard-v1/contracts/validate.ps1
pwsh -NoProfile -File scripts/dev/test-windows-cloud.ps1 -SkipRestore
# 仅在已明确公告的当前桌面合成窗口内加 -DesktopFixtures
pwsh -NoProfile -File scripts/dev/test-relay.ps1 -SkipRestore
# 干净提交后：
pwsh -NoProfile -File scripts/dev/package-windows-cloud.ps1
```

默认测试脚本不触碰真实剪贴板。已有依赖恢复用 `--locked-mode`；开发时复用了 checkout 的既有 NuGet cache。联网漏洞审计的实时结果不由离线缓存证明。具体执行计数、SHA-B、ZIP hash 和未签名状态由最终交付记录/manifest 给出。

提交前已通过 Core 24 项、Windows 图片/HTTPS/QA/OS 凭据/fixture 清单 11 项、Relay 17 项及黑盒 9 组，均零跳过；契约验证 49 例。干净候选的同组复核和真实桌面组合场景结果应连同包交付。源文件按显式清单审阅并执行 `scripts/dev/audit-candidate.py --staged`：这是当前启发式扫描与依赖边界检查，不声称专业扫描器认证。原 `MANIFEST.json` 是 P0 历史文档清单，不作为 SHA-B 当前文件完整性证明。

包分为产品客户端和 staging TestAgent，不混装。产品无 QA controller/fixture/sidecar 依赖，无本地入站 API 或 Tailscale；TestAgent 不随产品常驻。使用说明见 [Windows](windows/USER_GUIDE.md)，启动/fixture/协议和 P3 升级输入见 [QA runbook](windows/QA_RUNBOOK.md)。

P3 应提供独立升级计划：完整 SHA-B、Relay/QA 镜像 digest、`/v1/identity` 变化、只在 staging 的 QA HTTPS 路由及资源/短期租约配置、原 SHA-A 运行镜像/配置回滚目标。现有 SHA-A 的 P3 批准不自动等于 SHA-B 升级授权。P7 生产不得包含 QA compose、路由或凭据。

官方实现依据：[Win32 剪贴板](https://learn.microsoft.com/en-us/windows/win32/dataxchg/using-the-clipboard)、[BITMAPV5HEADER](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ns-wingdi-bitmapv5header)、[Windows 凭据](https://learn.microsoft.com/en-us/windows/win32/api/wincred/ns-wincred-credentiala)。实际兼容性以本报告的测试范围为限。
