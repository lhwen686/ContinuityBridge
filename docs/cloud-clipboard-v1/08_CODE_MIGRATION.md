# 08 已有代码的迁移清单

证据范围：2026-09-10 Windows 本地实际读取，源码 HEAD `720e0d9112c330b6d809c14648141981797e038d`，与私有 origin/main 一致；开始时源码无改动。下列迁移尚未实现，P0 只提交文档与契约。历史报告只表示当时记录，不是当前验收结论。

| 现有部分 | 处理 | 理由 |
|---|---|---|
| `WindowsClipboardAdapter` 的 STA/message window、生命周期 | 保留并回归测试 | 是 Windows 原生能力基础 |
| `Win32Clipboard` 的稳定读取、OpenClipboard 重试、私密格式判断 | 保留，按图片分支扩展 | 图片不能只塞进旧 text 变量 |
| `ClipboardModels` | 改成文本/图片与本地/协议分层 | 目前 string Text、UTF-8 元数据、TailscaleUserLogin 不符合新运行路径 |
| `StateCoordinator` | 拆分/重构，不盲目挪上云 | 同时混有 Windows 写入、队列、版本、幂等与正文缓存 |
| 24h `IdempotencyEntry` 持有 `RemoteClipboardResult.Snapshot` | 必须改变 | Snapshot 带正文；未来 byte[] 后会保留历史大图；与最新一条/30min冲突 |
| WindowsSequence `uint <=` 排序假设 | 增加回绕回归与稳健比较 | 不能遇到序列号回绕后长期丢事件 |
| `ContinuityBridge.Api` | 隔离旧占位，不作为 Linux 后端 | 当前 net10.0-windows 且引用 Windows Core |
| App `Program` 占位入口 | 实现真实常驻托盘生命周期 | 初始化 UI 配置不代表产品已运行 |
| 现有 Core 测试 | 保留有价值案例，随行为有意更新 | 不能把旧模型全删、只为新测试绿灯改预期 |
| Api/Integration ProjectLoads 类测试 | 增补真实接口/进程行为 | 工程能加载不是 HTTP 或 E2E PASS |
| 历史 2GB 文件/Range/Tailscale Serve 设计 | 标为被新 ADR 替代，不实现 | 用户已明确取消普通文件与 LAN/Tailscale 依赖 |
| `AGENTS.md` / `development-hosts.md` | 更新事实/阶段范围，保留安全规则 | “尚无提交/remote”已是历史；现在远端存在已提交基线 |
| `.codex` setup / scripts | 保留只读 setup，新增动作单独入口 | 不能每开 worktree 自动碰真剪贴板/启动 WDA/部署 |

## P0 现场复核与执行归属

文件位置均相对仓库根；行号以以上源基线为准。验收编号见 [11](11_ACCEPTANCE_AND_HANDOFF.md) 和 [05](05_TEST_AND_AUTOMATION.md)。

| ID | 实际代码证据与影响 | 后续阶段 / 必须验收 |
|---|---|---|
| M01 | 八份 csproj 的 TargetFramework 均为 net10.0-windows；App 为 win-x64/WinForms；Api 引用 Core，App 和 IntegrationTests 又引用 Api | P2 新建真正使用的 net10.0 Contracts/Relay 与可移植测试清单；旧 Api 仍有引用，不删除。C01、真实 Linux 运行 |
| M02 | `src/ContinuityBridge.Core/ClipboardModels.cs` 的 Snapshot/Text/Candidate 都是文本模型，包含 TailscaleUserLogin；远端结果携 Snapshot | P2 协议 DTO 不引用旧 Core；P5 分离本地与协议图片模型，移除新数据路径的 Tailscale 身份。T/I、A01 |
| M03 | `StateCoordinator.cs:14` 为 24h，`IdempotencyEntry` 保存完整 RemoteClipboardResult→Snapshot→Text；缓存容量512；清理只在下次 SubmitRemote 时触发 | P2 仅元数据+指纹、有界 TTL/容量、独立清理；不能只把24h改30min。S02、C04、C05 |
| M04 | `StateCoordinator.cs:218` 同键直接返回旧结果，未比对新正文；没有设备隔离或 CAS | P2 建立同设备键/指纹冲突、并发原子判定及 If-Match。S03/S04、C02/C03 |
| M05 | `StateCoordinator.cs:232` 远端写后与 `:273` 本地事件用 uint <= 排序；回绕后会拒绝新值，写后抛错甚至已有副作用 | P5 两条路径均用受本机串行 generation 保护的策略，不能把原生 DWORD 当无限增长时钟。测试 uint.MaxValue 前后、重复/延迟事件、0/无访问权限、远端自写；若采用模差须明确半范围假设及重新基线策略。C12、W03 |
| M06 | `StateCoordinator.cs:60` 会把 startupClipboard 植入最新状态；`:301` 相同 hash 永久抑制到内容改变；`:224` 远端相同内容仍写系统剪贴板 | P5 启动前不自动上传、有限去重、过期后同内容新动作可发、先核验再写。S05/S08、C11/C12 |
| M07 | WindowsClipboardAdapter 已有 STA/消息泵/停止与错误路径；Win32Clipboard 只读写 CF_UNICODETEXT 和操作标记，隐私 DWORD 在取正文前判定，OpenClipboard 有界重试 | P5 保留生命周期与隐私分支，补 PNG/DIBV5/DIB、稳定读取、内存/透明度控制；图片/网络不堵 STA。I01–I08、W03 |
| M08 | App Program.Main 仅 ApplicationConfiguration.Initialize，无 Application.Run/托盘常驻；Api 只有 AssemblyMarker，无 HTTP 路由 | P2 实现 Relay；P5 实现真实托盘、单实例、Credential Manager、可选自启、暂停/退出。W01/W02 |
| M09 | Core 13 个 TestMethod 为队列/文本/回环/启动测试；Api/Integration 各一个 ProjectLoads。Windows 有交互门禁和手工 G2；文件名 PlaceholderTests 不能直接判断其中全部是占位 | 保留有价值现有案例并按新语义有理由更新；P2 真实 HTTP/进程测试，P5/P6 原生验收。P0 未运行任何业务测试 |
| M10 | 忽略的本地旧需求记录确有约2G文件/断点续传提议，旧 FINAL_TECHNICAL_PLAN 明确 Tailnet Receiver；源代码没有相应完整传输服务 | ADR-001 替代这些产品需求；P2/P5 不实现文件/Range/Tailscale 路径；保留本地记录及网络/工具现状。I08、D01/D02 |
| M11 | 旧 TEST_REPORT 的 G2 明确 NOT PASS，后续旧 Phase D 被阻塞 | 保持原报告；新 P0 文档任务可执行，不把旧子集/历史报告视为新 v1 真机通过。P5/P6 重新提供同 SHA 证据 |

P0 已更新 AGENTS/development-hosts 的历史 Git 描述；上述 M01–M10 业务迁移仍为 **NOT RUN**。M11 是证据边界，不是要删除的测试或绕过的门禁。

## 变更原则

不把所有项目一次性改为跨平台目标，不重写整个 repository，不引入 MAUI/Electron/native iOS 产品来回避可验证的快捷指令方案。先完成 P4 的真机探路，再做完整 Windows 图片同步。

旧 `IdempotencyEntry` 的问题不能靠把保留时间从24小时改到30分钟就解决：只保留最新正文要求缓存内容结构本身不再携带旧正文。服务端状态与 Windows 本地写入 coordinator 要有清晰边界。

保持 NuGet 锁文件和 SDK 策略；新依赖要说明必要性、许可证和维护来源。引用官方资料与仓库现场结果；安装的工具、旧测试报告或模拟器截图不代表当前设备真实验证。
