# 分阶段所有权、行为验收与 SHA 交接

状态：P0 已建立清单；下面的产品验收全部 **NOT RUN**，不能把待实现案例数计为通过数。[05](05_TEST_AND_AUTOMATION.md) 的 T/I/S/E/A/W/D 矩阵仍完整适用。

## 文件所有权与门槛

| 阶段 / 责任端 | 独占修改范围（未来目录仅为归属，不由 P0 创建空工程） | 入场及退出要求 |
|---|---|---|
| P0 / Windows | AGENTS、development-hosts、本包设计/契约/验收文档 | 核实现场；审阅、扫描、正常提交/push 私有 feature 分支；不启动后续阶段 |
| P1 / Mac | 本机 artifacts；独立分支的 `docs/cloud-clipboard-v1/lab/` 脱敏报告 | 明确执行 P1；现有工具→设备→真实 UI→安全点击逐级证据；不并行改共享 ADR/契约 |
| P2 / Windows | 新 Contracts/Relay、portable tests、镜像模板/依赖说明；Windows 集成端统一维护契约 | P0 SHA；完整 L0/L1、SDK/锁/新依赖审核；交 SHA-A 和 Linux 构建入口；未测 Linux 单列 NOT RUN |
| P3 / VPS | 独立部署分支的部署模板/脱敏报告；真实 secret/地址只存本机 | 收 SHA-A；只读准备具体 planId。批准后才 staging 写入；验证 TLS、隔离、Linux 运行及回滚；不改业务源码 |
| P4 / Mac | 独立短期分支的 `shortcuts/` 实际模板、`lab/` 动作及证据摘要 | P1 UI 可用 + 已批准上线的 SHA-A staging；实际 ETag、长文本、双向手机图片操作、近20MB、错误不污染；Windows 审阅整合 |
| P5 / Windows | Core/Windows/App、对应测试、受限 TestAgent 与独立 QA sidecar | P4 关键图片能力 PASS；整合 Mac 提交后实现完整客户端，交 SHA-B 与 fixture 清单。P4 阻塞则先修复证据所指问题，不自行扩大到原生 iOS App |
| P3 升级 / VPS | staging 配置/部署报告 | 明确 SHA-B、镜像/配置 diff、回滚目标；已有授权须明确涵盖本次更新，否则批准新 planId；QA 仅 staging |
| P6-W / Windows；P6-M / Mac | 各自主机 artifacts、脱敏报告；Mac 为唯一场景控制端 | 两端和 staging 同 SHA-B；runner READY、租约和 fixture 一致；L2 双向真实源复制/目标粘贴，完整矩阵；退出测试控制 |
| P7 / VPS | 脱敏 production release manifest、操作说明 | 同候选 P6 关键项 PASS、Linux/TTL/鉴权/并发通过；具体生产计划单独批准；无 QA 组件/路由；回滚演练 |
| P8 / Windows | 发行包/使用回滚说明、L3 报告 | P7 已批准上线且兼容受测版本；日常双向跨网、退出/暂停/自启、脱离 Mac/USB；合并/外部 release 另核授权 |

代码修复回到模块所有者，不在 VPS 生产树热修或在 Mac 修改 Windows-only 代码后宣称通过。共享契约/ADR 由 Windows 串行整合；Mac 提出带真实证据的最小兼容调整。一个桌面/手机只允许一个活动测试操作者。以上是协作职责，不是本次已启动其他任务。

P4 先于 P5 是为了先消除 iPhone “剪贴板图片对象→File 二进制→下载图片→实际粘贴”的系统风险，兼查 HTTP 非成功响应与完整 ETag。Apple 的请求体能力说明不能替代这条真机链路；P4 的 API→iPhone 证据也不能替代 P6 Windows→iPhone。

## 补充的可实施行为案例

这些应成为 P2/P5 的真实测试，而不是 P0 占位工程。每项都需要前置状态、操作、期望状态/副作用和独立证据；使用合成 fixture。

| ID / 归属 | 操作与必须断言 | 阶段 / 层 |
|---|---|---|
| C01 / M01 | portable 工程依赖图不得到达 net10.0-windows/WinForms/System.Drawing；Linux 真正启动 Relay、HTTP/WSS 成功 | P2 静态+构建；P3 L1 |
| C02 / M04 | GET 空状态取得 ETag；缺条件428、缺/非法UUID400、弱/通配/多etag400；旧条件412；错误无状态变更 | P2 L1 |
| C03 / M04 | 两设备同条件并发一成一412；同设备同键同指纹并发只提交一次；相同键不同正文/媒体/方法/原条件409；不同设备键隔离 | P2 L0/L1 |
| C04 / M03 | put A→put B→用原键重试A：result=A、state=B、available=false、replayed=true；A下载410，B不变，TTL不延长；撤销token后重放401 | P2 L0/L1 |
| C05 / M03 | 超容量/TTL淘汰、重启后用原条件重试；失败412且正文不复活。检查缓存对象图无 text/byte[]/Snapshot 引用；过期/取消/替换后无历史正文持有 | P2 L0/L1 |
| C06 / R7 | 假时钟 expiry−最小刻度可读、expiry不可读；并发GET/后台清理只递增一次；空DELETE仍递增；旧条件不能重用 | P2 L0/L1；P6真实30分钟补充 |
| C07 / R8 | 文本 UTF-8 limit−1/limit/+1；JSON转义封装单独计数；PNG合法20,000,000可收、20,000,001拒绝；无Content-Length分块同样限制；失败保留旧槽位 | P2 L0/L1 |
| C08 / R1 | 空文本、中文/emoji/CRLF/空白原样；NUL、非法UTF-8、孤立代理项、重复JSON属性拒绝；声明PNG实为JPEG/畸形图失败；APNG/GIF拒绝；像素/并发上限有界 | P2 L0/L1 |
| C09 / R7 | 下载A时替换/过期，中断旧响应或在有界截止前结束；新下载A=410；客户端长度/hash/最终状态核验失败不写本地；Range请求不产生206/历史访问 | P2 L1；P4/P5/P6 L2 |
| C10 / R9 | capabilities只有none；文本/图片未知mode415、未来keyId/nonce字段拒绝；伪造sourceDeviceId无效；所有读取/事件鉴权；无授权不读大正文，日志错误不回显秘密 | P2 L1；P3代理审计 |
| C11 / M06 | 启动前正文不自动发；离线120秒内且原云状态未变可提交；超龄/无已知状态/新epoch/远端已变不补传；下载中本地新复制禁止旧写；在线412只允许一次新键竞争 | P5 L0/L2；P6 L2 |
| C12 / M05–M07 | 模拟序号 max−1→max→0→1、重复/乱序/无权限0、自写回环；原生uint只作变化证据，本地generation维持串行意图；同hash新动作在云过期后可再次发送 | P5 L0 + 真Win32 L2（回绕模拟不能声称OS真实回绕） |
| C13 / R4 | WSS提示丢失/乱序/重复、进程新epoch、撤销关闭、HTTPS轮询降级；只查元数据不重复下载旧大图；暂停取消队列和网络，无自动补发旧内容 | P2 L1；P5/P6 L2 |
| C14 / QA | 错角色/过期租约/错误SHA/未知fixtureId拒绝；无shell/任意正文/路径/URL；租约到期自动退出；production镜像/路由/配置均无QA | P5 L0/L1；P6/P7实际核验 |
| C15 / R8 | 改TTL/字节上限后capabilities一致；旧项期限不追溯延长；无效配置在启用前失败（含JSON限额容纳转义、幂等TTL≤正文TTL、并发内存预算） | P2 L0/L1；P3实际配置 |
| C16 / TestAgent 安全 | 未启用/未验证身份租约时零接管；完整有界独立内存快照；未知格式/失败/竞争不覆盖；锁内 CAS；停止/关闭/到期/错误先排空后恢复；外部新复制保留；超时无迟到恢复；私人备份不进 QA/自动上传 | P5 fake/L0 与本机 HTTPS；P6 经确认的真实桌面另测，不能互代 |

staging 至少一次实际等待30分钟的到期验证不可用短TTL/假时钟替代。合法20MB fixture 要有可解释生成方式，不随意尾补字节；客户端重编码时记录运输字节校验和解码像素/宽高/alpha/方向校验。L2 每个方向须记录真正源设备复制和目标应用粘贴，不用截图猜长文本完整性。

## 报告和 gate 规则

结果字段：日期、source SHA、dirty状态、主机角色、OS/工具版本、fixture清单版本/hash、case ID、证据层、预期/实际、PASS/FAIL/BLOCKED/NOT RUN、脱敏证据索引。截图/UI tree/原始日志/地址/设备标识/生产正文留本机 ignored artifacts；共享报告仅合成数据及必要摘要。

缺业务实现/没运行记 NOT RUN；运行失败记 FAIL；需要权限/工具或外部条件记 BLOCKED；断言有证据成立才 PASS。既有占位测试、旧G2子集、Schema正反例、构建成功均不能替代真实HTTP/Linux/Win32/iPhone证据。P7 前缺双向图片/鉴权/TTL/并发或严重FAIL就停止在报告与可审阅准备；不可用跳过来放行。

## 精确版本交接

1. 发送方显式暂存本阶段 allowlist，检查 diff、秘密、业务文件不越界；正常 commit/push 私有任务分支，不合并 main。交付完整 SHA，接收方不能只依赖会移动的分支名。
2. 接收方先查真实 OS、自己的仓库根、HEAD、分支和 dirty，核实 origin；使用已存在认证执行 `git fetch origin feature/cloud-clipboard-v1`。不要复制其他机器凭据或整个目录。
3. 将完整交接 SHA 作为 `git show` / `git cat-file -t` 的真实参数核实；仅在干净的独立 clone/worktree 检出它（如 `git switch --detach <handoff-sha>`，替换占位符后执行）。有改动则先保留并使用另一个已准备的工作树，不强制切换。
4. P0 SHA 只含设计契约。Mac 收到后可在用户明确执行 P1 时按 `prompts/P1_MAC_LAB_PREFLIGHT.md` 核验；P2 由 Windows 按 `prompts/P2_WINDOWS_RELAY.md` 实现。VPS 现在只接收文档，**P0 SHA 不可部署**；等 SHA-A 与明确 P3 任务再核查部署。
5. P4 模板/报告提交经 Windows 整合才产生 SHA-B；P3 升级、P6 两端全用它。修复再产生 SHA-C，交新版本、部署/回归受影响项，不拼接异版PASS。二进制/镜像记录 source SHA、digest、平台、协议、配置schema及回滚目标。

本报告所在提交的完整 SHA 可由 `git log -1 --format=%H -- docs/cloud-clipboard-v1/10_P0_BASELINE.md` 查到；不要在同一提交文件内填它自身的未来 SHA。最终交付消息提供已实际提交并核实的完整 SHA。真实主机路径和生产入口不放进交接包；域名缺失不阻止 P0 完成。
