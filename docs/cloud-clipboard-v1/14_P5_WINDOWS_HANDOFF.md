# P5 Windows 候选交接

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
