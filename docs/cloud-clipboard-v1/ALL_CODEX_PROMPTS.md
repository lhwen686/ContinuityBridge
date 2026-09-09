# 全部分阶段 Codex 提示词

只执行当前阶段，不要一次发送并运行本文件全部内容。设备与顺序见03_DEVICE_RUNBOOK.md。

# Codex 公共执行约束

本包是用户对 ContinuityBridge 云端剪贴板 v1 的实施任务。只执行当前阶段；在该阶段授权范围内完成实际工作、验证和交接，不停在“建议计划”或“是否继续”。不自动跨越生产部署、凭据、不可逆修改的授权边界。

## 已确定的产品范围

文本/图片双向；无普通文件；iPhone 手动；Windows 自动；公网 HTTPS/WSS，无 LAN/Tailscale 依赖；v1 使用现有4核8GB VPS，但保留 Cloudflare 后端替换空间；云端全局最新一项30分钟，图片20MB；限额可配置；v1明文经TLS，v2预留真正E2EE。

设计默认：20MB=20,000,000字节；静态PNG/JPEG；文本默认1,000,000 UTF-8字节；云端正文内存暂存、重启清空；过期不清设备剪贴板。用 capabilities 发现配置。遇到安全/真实系统限制可以提出有证据的最小调整；不能重新询问整套已确定需求或静默削减图片双向要求。

## 现场与来源

先确认实际 OS/用户/仓库根/branch/HEAD/dirty 状态和现有工具，不从聊天 UI 位置推断执行端。读取仓库 AGENTS.md、相关目录约束、docs/development-hosts.md 和本阶段引用文件。私人路径/地址/UDID/凭据只留本机，不输出共享摘要。当前远端曾核查为720e0d9；实际若更新，检查差异，绝不强制回退。

现有工程均是 Windows 目标，Mac/Linux不能运行真实Windows客户端；Relay必须独立可移植。不要把历史“尚未首次commit/remote”当当前事实，也不要将历史阶段限制误用为用户本次具体任务未授权。保留仍有效的安全约束；确有冲突时指出具体文件/条款和影响，先完成无冲突部分，不悄悄改走另一条路线。不得覆盖系统/工具自身权限和安全限制。

相关官方技术资料从07_OFFICIAL_SOURCES.md选择性复核。对不可用工具说清缺失/错误；不捏造 CLI 参数、MCP tool 名称、GUI 控件、文件导出或测试结果。可搜索本机已有项目记忆工具，但不能以记忆代替当前文件，也不擅自保存记忆。

## 自主执行、分工与验证

合理的低风险可逆实现细节自行决定并写入 ADR。真正影响范围/安全/外部服务的缺失信息才集中问一次；先完成可审阅准备。独立只读审计/独立模块可委派，多个 agent 不同时写同一文件/主机剪贴板、操纵同一手机或部署同一服务。

按变更风险运行有意义的测试，不用只验证实现细节的空测试凑通过数；测试通过后不无理由反复重跑无关全套。失败要诊断原因；不能删测试、吞错误、改期望掩盖问题。区分PASS/FAIL/BLOCKED/NOT RUN；模拟、HTTP种子和编译成功不能替代真机E2E。

## Git、环境与秘密

保持当前用户修改；无reset --hard、clean、force push、盲目git add .。本阶段若允许代码修改，可在现有已验证私有origin创建短期任务分支、显式暂存相关文件、常规commit/push作为交接；不合并main或修改仓库可见性。测试交接精确SHA；worktree属于本机，ignored配置/凭据/工具不跨机同步。

遵守global.json/锁文件；Windows按CONTINUITYBRIDGE_DOTNET → checkout .tools/dotnet/dotnet.exe → PATH查已安装SDK。已有已锁依赖的常规restore/build/test在实现阶段允许；新增依赖需必要性/许可/来源审查。安装SDK/系统工具、修改Xcode签名或设备配对需单独明确许可，不能自动以最新版覆盖可用环境。

不修改用户代理、hosts、系统DNS、VPN、防火墙、SSH安全策略；需要的部署端口/证书变更由部署计划单独批准。不创建NOPASSWD:ALL，不索取聊天中的密码/私钥，不公开WDA/MCP/Codex App Server。不重启/关闭用户浏览器、不创建临时个人浏览器profile、不动扩展/拦截器、不读cookie绕过登录。

密钥/设备标识/原始剪贴板/截图UI tree/个人日志不进入Git。测试只用合成fixture，原始证据在ignored artifacts/，共享摘要脱敏。纯快捷指令里的token不是Keychain保险库，配置页面由用户输入秘密，自动化暂停截图。没有保存/推送/运行的动作不得称已完成。

## 结束输出

先给本阶段结论，再给实际主机角色、branch/SHA/dirty、变更文件、执行命令与退出状态、测试结果、剩余阻塞、下一阶段的精确交接项。避免输出token/生产正文。没有真实验收就写NOT RUN；生产变更前展示计划ID、差异和回滚目标。用户只需处理真正的授权/设备权限/缺失事实，不应手工替代已经可自动完成的常规实施。


---

<!-- prompts/P0_WINDOWS_BASELINE.md -->

# P0 — Windows：建立新的实施基线与契约

你现在应在我的 **Windows 本地 ContinuityBridge 项目**中执行。先证明实际执行端是 Windows，再读 `docs/cloud-clipboard-v1/prompts/COMMON.md`、仓库 AGENTS.md、docs/development-hosts.md 以及本包 00/01/02/03/08 文档。

## 目标

完成可交接的云端剪贴板 v1 设计基线，使后续 Mac/VPS 不再依赖旧聊天推断需求。本阶段只改文档、契约/测试计划和必要的文档忽略配置，**不实现业务、不触碰当前剪贴板、不部署、不重装工具**。

## 工作

检查现有仓库root、remote可见性、branch、SHA、dirty、SDK路径、global.json和锁文件。不要强行把当前仓库变回720e0d9。运行已有只读 setup/status；读代码确认实际模型/Win32/API/App/测试完成度。

更新 AGENTS.md 和 development-hosts 中已过时的“没有初次提交/remote”描述为有日期的历史记录，写明本次新的分阶段授权；保留浏览器保护、凭据隔离、只读setup、平台门禁。不能把环境已安装或旧报告通过当真机已通过。

重点检查：全工程net10.0-windows；旧API不具备Linux部署资格；旧StateCoordinator 24h幂等缓存是否持有完整正文；Windows uint序号回绕；历史2GB文件/Tailscale路径。逐条更新迁移清单。

将用户九条需求和本包的补充默认值形成ADR，明确20MB十进制、全局单槽位、30min从提交计时、重启内存清空、过期不清设备、离线冲突处理。把02协议细化为可机器校验的OpenAPI/Schema或等价契约，规范If-Match/Idempotency-Key、etag JSON、错误响应、capabilities与未来加密边界；不需要为未实现模块创建空业务工程。

写清各阶段文件所有权、SHA交接、staging/production gate和P4先行的理由。为后续验证生成测试目录/案例清单可以，但不要添加ProjectLoads式占位测试冒充覆盖。依赖/代码改动若非本阶段必要，列入P2/P5。

可创建本机短期feature/cloud-clipboard-v1分支，显式暂存本阶段安全文档/契约，核查staged diff和秘密后commit并推送现有私有origin的同名分支。已有分支/未提交工作先保留，不强制覆盖。不合并main。

## 完成标准

事实基线、可执行契约、迁移清单、测试门禁已写入仓库并正常提交；给Mac/VPS的共享文件不存在秘密或机器私有地址；业务代码/真机/部署未执行。报告新SHA及下一步P1/P2入口。域名尚未给出不阻止完成本阶段。


---

<!-- prompts/P1_MAC_LAB_PREFLIGHT.md -->

# P1 — Mac mini：核验已安装的真机实验室

你现在应运行在我的 **Mac mini**。使用本机既有 ContinuityBridge clone，实际路径现场核实且不写入共享报告。用户已安装Xcode和Mobile MCP；不要重新安装一遍。先读COMMON与仓库规则；本阶段核验实验室，不实现Windows客户端、不部署VPS。

## 授权与边界

允许只读检查开发工具和已有设备连接；在我提供的USB iPhone上，只对无私人数据的测试页面做小范围截图/UI tree/点击/输入验证，测试结束恢复页面。需要解锁、信任、Developer Mode、签名账号或粘贴许可时让我完成，不索取密码/UDID到聊天、不改签名/重新配对/重装SDK。启动已存在且配置正确的本地WDA/USB转发可以先报告命令与本地监听范围；若涉及sudo、签名或系统更改，停在该步骤请求具体批准。

## 工作

确认真实macOS执行端、checkout、branch/HEAD/dirty。检查现有Xcode选中路径、版本、CLI tools、Mobile MCP实际配置/版本/可用tool列表、已有go-ios、WDA项目/运行状态与签名有效性。不要把Mac运行现有net10.0-windows工程列作测试。

依次建立证据：能枚举真实iPhone → 本地WDA健康 → Mobile MCP列出设备并返回真实UI tree/截图 → 在安全测试页面进行一次点击/输入并核对实际变化。先发现工具再使用，不能从README猜当前tool名字。排除“只连上模拟器”“只获得Mac屏幕”“返回缓存截图”。

go-ios所需USB tunnel/port forward依照已安装版本和上游真机指南核对。这不是重新引入产品LAN/Tailscale依赖。所有WDA/MCP端口仅本机/受控USB范围，不开公网。记录但不自动改动遥测/全局配置。

检查免费Personal Team等签名有效期风险并写入实验室维护说明，不据此要求购买开发者会员或开发原生iOS产品。只有工具安装不代表UI可用，只有UI可用不代表剪贴板E2E可用。

原始日志、截图、UI tree和设备标识仅存ignored artifacts。报告只含脱敏工具版本、每一级PASS/FAIL/BLOCKED/NOT RUN、具体阻塞与P4需要的最少人工步骤。P0尚未同步时本地保存报告即可，不并行改共享AGENTS/契约。


---

<!-- prompts/P2_WINDOWS_RELAY.md -->

# P2 — Windows：实现可移植 Relay 与协议测试

在Windows开发会话执行，读COMMON、01_ARCHITECTURE、02_PROTOCOL、08_CODE_MIGRATION和P0提交的契约/ADR。目标是实现真实可运行、可测试的Relay，**不要只给计划**。当前阶段不部署VPS、不改真实剪贴板、不制作iOS原生App。

从P0已提交基线出发，保留用户变更。新增实际需要的Contracts(net10.0)与Relay(net10.0 ASP.NET Core Web)，不引用Windows-only工程。保留现有Windows SDK约束和项目布局；可修正解决方案包含关系，但不要把全部项目改目标平台。

实现capabilities、全局latest状态、text JSON/image二进制上传、鉴权正文下载、条件清空、WSS提示、最小健康检查。服务端随机设备token映射身份，配置不含真实secret。TTL/20MB/文本限制由配置和capabilities统一。内存正文、重启新epoch、原子替换、有界并发/缓冲、取消/异常清理、Cache-Control:no-store。

落实If-Match/CAS、同设备幂等键+正文指纹、丢包重试、同键异内容409、旧项不可读取410、不允许重试续期/复活。幂等缓存只留结果元数据，不得复用旧带Snapshot正文的StateCoordinator。上传未完整校验前不替换旧项；分块上传同样强制大小。新图片/文本共用一个槽位。

只实现none加密模式，保留版本化envelope升级位置并拒绝未知mode；不要提交伪E2EE。服务端图片验证受限，不用System.Drawing或无界像素解码。

编写真实HTTP/进程和有意义的状态测试：Unicode不截断、size±1边界、流式超限、畸形图、TTL fake clock、并发提交、幂等不持有旧正文、下载时替换/过期、epoch重启、token撤销、WSS重连。加不依赖.NET内部对象的黑盒契约测试，便于未来Cloudflare适配。

编写Linux镜像构建所需Dockerfile和配置模板、明确portable项目/测试入口；只在已有本地工具允许时验证，缺Docker则标Linux镜像NOT RUN，等待P3真实Linux测试。不要将Windows编译成功称为Linux运行通过。不要自动安装系统依赖。

整理fixture生成方式（非私密文本、透明图、合法20MB边界图），写清P4如何用API注入fixture。对新第三方依赖记录理由、许可证与上游来源。完成测试与修复后显式提交并push任务分支，交付SHA-A、运行命令、测试摘要、P3部署输入；不合并main。


---

<!-- prompts/P3_VPS_STAGING.md -->

# P3 — VPS：按指定 SHA 部署或升级 staging

你现在应通过SSH运行在我的VPS上。先确认Linux/用户/工作目录/实际仓库，不在Windows本地误跑生产命令。读COMMON、06_DEPLOYMENT、02_PROTOCOL与本次交接的精确candidate SHA。现有VPS为用户自用4核8GB，实际资源、OS和服务必须检查。

## 本次默认授权

先只读检查并准备可审核的staging部署方案；不创建公开服务、不改防火墙/SSH/代理/DNS、不重启已有服务。用户明确批准具体planId后，才执行该计划。已有明确授权覆盖本次更新时，引用其范围；不能把“请设计”当远端上线授权。

## 工作

核查现有Docker/Compose、可信HTTPS入口、域名、端口占用、sudo可用性、现有业务、部署路径、磁盘/内存/时钟。需要sudo而未授权就说明未验证部分，不要求把sudo密码贴到聊天、不设置NOPASSWD:ALL。

只部署P2/P5交付的干净、明确SHA。未存在clone时确认已有GitHub认证/安全的只读获取方式，不能从别台复制私人认证目录。生产工作树不承载临时代码修复；修复走任务分支重新交接。

完成计划：镜像构建输入、不可变版本、配置/secret路径、loopback应用端口、反向代理路由/TLS、内存限额、restart策略、预期新增监听、对现有服务影响、健康检查、回滚。已有反向代理优先复用，不能抢80/443。Docker映射loopback，不能仅以ufw状态判断无暴露。禁止关闭TLS验证。

批准后执行：构建/部署指定Relay Linux版本 → 鉴权/无鉴权/size/TTL/小fixture smoke → 验证证书/外部访问与loopback限制 → 检查脱敏日志 → 输出真实版本与回滚命令。记录Linux实际运行测试，不用Windows的结果代替。

这是staging。首次SHA-A不用QA控制器；P5后升级SHA-B时，若已实现独立QA sidecar且本次计划批准，可只在测试入口启用带租约/角色令牌的受限fixture mailbox；不能放入production或公开任意命令接口。

域名、端口批准、精确SHA等无法从现有环境确定时集中问最少问题，同时完成其余只读准备。原始域名/IP/token只存本机.local/或安全配置，不进共享repo。交付staging可用性、SHA/digest、secret已安全配置的状态、P4/P6入口与PASS/FAIL/BLOCKED；不要输出真实密钥。


---

<!-- prompts/P4_MAC_SHORTCUT_SPIKE.md -->

# P4 — Mac mini：先打通真实 iPhone 图片快捷指令

在Mac mini现有项目执行，使用P1已验证的Mobile MCP/WDA控制USB真iPhone。读COMMON、04_IPHONE_SHORTCUTS、02_PROTOCOL、05_TEST_AND_AUTOMATION。staging必须已按SHA-A上线；手机需可正常HTTPS访问。不要重装已可用工具。

## 目标

实际制作并验证“CB 发送”“CB 取回”，证明图片最终可粘贴，而不是只返回成功JSON。此阶段允许在指定测试App/测试便笺中使用合成fixture、创建/调整测试快捷指令和保存脱敏动作说明；不读取私人照片/聊天记录，不触碰生产token，不大改Windows源码。

先发现当前Shortcuts真实动作名/变量类型与MCP能力。用当前UI或系统正式可用工具创建快捷指令，不猜动作ID。CB配置由用户本机录入staging地址/token，配置页面不得截图记录秘密。没有Keychain辅助能力就如实说明纯快捷指令token存储边界。

依次验证：长文本JSON上传不截断；GET状态里的完整etag可原样作为If-Match；UUID幂等键；真正图片对象转换与大小读取；image二进制File请求体；手机收到下载后写真实图片剪贴板；在目标测试便笺中粘贴透明PNG/JPEG。最后测接近20MB合法图片和超限错误。未经真机结果不得声称20MB稳定可用。

手机“发送”的源必须来自真实剪贴板，不用上传文件动作替代；“取回”最后复制的必须是正文/图片变量，不是HTTP预览、字典、URL或文件名。测试GET后新项替换、到期、认证错误时保留原剪贴板。若HTTP错误处理存在系统限制，记录真实行为并用操作后核验补足，不能悄悄复制错误正文。

可用API合成fixture为手机取回准备云数据，但报告标为API→iPhone，不称Windows原生端到端。Mac上shortcuts run只能证明Mac行为；用于自动化运行的shortcuts://URL必须实际在iPhone打开。WDA剪贴板get/set可能有前台限制，按实际工具能力验证，不能把空返回当正确结论。

可以制作测试专用CB测试校验快捷指令，后续只在QA测试租约下用于fixture oracle；不得变成生产自动外传剪贴板入口。

交付：真实动作顺序/变量绑定/实测系统版本、支持类型和大小、失败行为、去secret的可导入.shortcut模板（只有真实导出并验证才交付）、证据摘要。无法可靠导出时给真实动作步骤并标注导出BLOCKED，禁止JSON改名伪造。把模板/文档放独立短期分支正常commit/push供Windows整合；不并行改协议。若确需协议最小调整，先提出可复现证据与兼容修改，再统一到候选版本。


---

<!-- prompts/P5_WINDOWS_CLIENT.md -->

# P5 — Windows：完成自动同步客户端与受限测试 runner

在Windows当前用户会话的项目中执行。读COMMON、01/02/05/08和P4结果。先整合已审核的Mac快捷指令/契约变更到集成分支，使未来所有主机使用同一候选SHA。P4手机图片关键能力未通过时，不通过扩大到原生iOS App来绕过范围，应先解决实际阻塞。

## 目标

完成真正可用的Windows托盘客户端：本地文本/图片复制自动上传，远端更新自动写入真实Windows剪贴板；无需本机入站API或Tailscale。允许在预先说明的测试窗口使用合成fixture覆盖剪贴板；不能触碰私人数据/关闭浏览器，不能把后台SSH会话当交互桌面。

复用STA消息窗口、OpenClipboard重试和隐私格式规则。图片读取支持当前实际格式，输出注册PNG及目标App所需DIBV5/DIB；bitmap按规定编码，透明度/方向正确，不能只写路径/URL/Base64。CF_HDROP普通文件不发送。编码/网络脱离STA，限制解码像素和内存。

实现文本/图片类型模型、localGeneration、原子本地应用与operation标记、自写不回传、有限去重、serverEpoch/revision、CAS、断线重连与HTTP降级。下载期间本地新复制要使旧下载失效。网络重试保留同一请求键，不把旧离线候选无条件覆盖新云内容；不上传启动前剪贴板，不因云端TTL去清空本地。覆盖uint序号回绕和相同内容过期后重复制。

实现可用托盘：配置baseURL和安全凭据、连接/错误状态、暂停/恢复、清空云端、退出、单实例、可选登录自启。第一次启用自动上传需清晰告知；不修改全局Windows云剪贴板、代理/防火墙。运行在用户会话，不安装交互式Windows Service。凭据进入OS保护存储，生产日志无正文/hash/token。

实现最小测试设施：一个用户显式启动、可见、可停止的Windows TestAgent + 独立staging QA sidecar协议。只接受已提交fixtureId对应的固定动作，不接受任意命令/路径/URL/正文；分controller/runner短期令牌、run租约、candidateSHA、单会话、超时。各端主动HTTPS连接，不依赖LAN。不在产品运行时常开，也不将QA控制器打进production部署。

运行有意义的单元、HTTP、Windows真实剪贴板/目标测试窗口检查；产生可部署候选SHA-B和Windows构建包（若当前阶段可完成）。打包前确认无QA凭据/源秘密/个人数据。无代码签名证书时如实说明未签名，不关闭系统安全防护。

显式提交/push任务分支，交付P3更新staging所需SHA-B、测试agent启动方式、fixture清单、已执行与NOT RUN矩阵。不要声称已经完成尚待P6的iPhone↔Windows E2E。


---

<!-- prompts/P6W_WINDOWS_TEST_AGENT.md -->

# P6-W — Windows：启动真实桌面测试端

在Windows **当前登录用户的交互式桌面**执行。读COMMON与05_TEST_AND_AUTOMATION。候选SHA-B必须由P5交付且P3 staging同版本；P6-M的Mac控制端也将检出同一SHA。不运行在WSL、服务Session0或只有SSH的其他会话。

本次允许在指定测试窗口使用合成fixture覆盖并验证剪贴板。我应先保管当前剪贴板；只在本机内存尽力备份/恢复支持格式，不把私人备份写磁盘/发云/发聊天。遇到非fixture内容停止该用例，不能回传原文。

核查进程位于正确用户session、单实例、候选SHA/dirty、staging origin与test lease。按照P5真实存在的命令启动可见Windows TestAgent，连接受限QA入口；token从OS/本机安全输入读取，不打印。原始日志仅ignored artifacts。

agent只可执行fixture白名单、核查真实Clipboard格式/完整文本/图片像素、在指定安全测试窗口粘贴、报告脱敏摘要。无任意shell/文件/URL能力，不自动把它注册为永久开机服务。

成功准备后输出READY及非秘密runId/candidateSHA/租约截止/停止方式，让用户切到Mac执行P6-M。若当前Codex无法让进程在会话之外持续运行，提供并实际验证最小的可见测试runner启动入口，不声称后台已经存在。不要靠睡眠假装维持任务或承诺聊天结束后由模型后台工作。

Mac完成或租约到期后runner应自动退出控制状态并释放资源。最后按本地备份能力恢复，确认产品客户端测试配置没有被误切为生产。


---

<!-- prompts/P6M_MAC_E2E.md -->

# P6-M — Mac mini：自动执行跨设备真实 E2E

你运行在Mac mini项目，使用已验证Mobile MCP/WDA操作USB真iPhone。读COMMON和05测试矩阵。Windows P6-W必须READY，staging与两端均对应同一候选SHA-B；否则先核查，不把异版证据拼接。

目标是通过受限QA mailbox协调Windows真实桌面runner，自动完成两个方向的文本/图片复制粘贴。只操作合成fixture和指定测试App；禁止私人数据、任意shell远程控制或修改生产服务。一个手机同一时间只允许本会话操作，不并发跑其他UI agent。

每个场景顺序为：QA run准备fixture/状态 → 在源设备执行真实复制 → 实际产品发送/自动上传 → 实际产品取回/自动接收 → 目标原生剪贴板核验和真实粘贴 → 收集独立oracle → PASS/FAIL。不得直接调用服务器上传接口替代源端复制，又把它算E2E。

Windows→iPhone：让Windows TestAgent把指定fixture放进真实Clipboard，等待产品客户端上传；在真iPhone执行CB取回、在测试便笺粘贴，并用合成测试校验步骤核对完整文本或图像。

iPhone→Windows：在真iPhone测试页面/图片中复制fixture，执行CB发送；等待Windows产品自动接收，runner核验原生格式与粘贴。不要从Mac剪贴板或API上传替代手机源动作。

执行矩阵中的关键双向、长文本、透明图、近20MB、超限、替换、竞争、回环、重连、TTL、权限/错误场景。部分可由L0/L1证明的恶意/边界项目引用同SHA报告，但每项注明证据层。至少一次实际30分钟staging过期验证；短TTL和fake clock只能补充，不冒充真实等待。若会话窗口无法完成等待，记录NOT RUN和可执行的继续步骤，不谎称已等满。

WDA direct clipboard因前台限制失效时用已验证的产品快捷指令/受控fixture oracle，不能根据截图猜完整性。图像传输校验byte hash；必要系统重编码时解释并校验像素/尺寸/alpha。系统权限/解锁由用户完成，暂停自动化而非绕过。

发现代码bug先生成最小复现与所属模块，不在Mac改Windows-only代码并宣称通过；交回Windows修复形成新SHA，更新staging，重跑受影响矩阵。截图/UI tree留本机ignored artifacts，共享摘要脱敏。

结束时关闭QA run/租约，交付每项PASS/FAIL/BLOCKED/NOT RUN、候选SHA、测试环境与实际证据、剩余人工L3项目。只有两个方向真正成立才写双向E2E PASS；P7是否可进入依明确门禁判断。


---

<!-- prompts/P7_VPS_PRODUCTION.md -->

# P7 — VPS：生产发布与回滚

在VPS SSH项目执行，读COMMON、06_DEPLOYMENT与最终候选SHA的P6报告。本阶段先准备生产变更计划，**用户批准精确planId前不改变生产状态**。

确认两端关键E2E、Linux运行、大小/TTL/鉴权/并发测试对应当前候选版本。存在严重FAIL或缺双向图片证据时不宣布可发布；整理阻塞和已完成工作。

核查生产入口/证书、当前服务、容器配置、secrets、上一版本与回滚方式。使用已经受测的不可变镜像digest或可追踪到同SHA的构建；不能部署latest标签碰运气。staging与production使用独立token/state/配置。

检查生产不包含、不启用、不路由QA mailbox/测试控制能力，不部署WDA/MCP，日志/缓存/代理缓冲符合隐私约束。说明内存存储在更新或回滚时清空云端临时项，既有本地剪贴板不清空。初次生产启用应让用户知道这一取舍。

给出文件/服务/端口diff、停机与清空影响、健康检查、secret发放方法、回滚目标；需要新增端口/DNS/证书操作必须在计划中清楚列出，不用宽泛sudo授权替代。

收到具体批准后执行发布，检查可信HTTPS、无token拒绝、两类设备token权限隔离、基本合成文本/图片smoke、最新单槽位、实际生效配置、脱敏日志。完成可安全执行的回滚演练或明确记录未执行原因；不得恢复过期正文或复用staging凭据。

只报告密钥已安全存储/设备待录入状态，不在聊天/终端输出token。提交脱敏release manifest和生产操作说明；真实主机信息留本机。交付P8启用Windows生产配置所需的安全路径和版本，最终日常跨网测试仍由P8确认。


---

<!-- prompts/P8_WINDOWS_RELEASE.md -->

# P8 — Windows：用户安装包与最终日常验收

在Windows本地交互会话执行，读COMMON与P6/P7同版本发布结果。目标是把已测候选交付为可使用、可退出、可回退的产品，不是再次改架构。

确认Windows二进制对应生产Relay支持的协议与受测SHA；整合过的新提交必须说明是否影响二进制/测试。构建便携发布包或已批准的安装形式，包含版本、SHA、首次配置、暂停/退出/自启关闭、卸载/回滚说明。包内无测试token/私有配置、QA控制器、截图和日志。无代码签名证书如实标未签名，不要求关闭SmartScreen。

在用户批准的测试窗口安全配置生产BaseURL与独立Windows token，iPhone生产token由用户本地输入，自动化不截图秘密。先做小合成文本/图片两方向确认，再测试暂停/恢复/退出/单实例。设置可选登录自启须单独明确用户选择，不默认改全局系统服务。

最后做L3：用户拔掉iPhone USB并关闭Mac上的WDA/Codex或让Mac关机；iPhone切蜂窝/另一网络，Windows使用自己的网络。此时人工触发两次产品快捷指令，Windows保持自动同步。记录结果，证明Mac、USB、GitHub、Codex、LAN/Tailscale均不在产品数据路径。改变网络/关闭Mac这种会切断测试控制的操作由用户执行，不伪装成Mobile MCP还能继续控制。

若网络到VPS失败，先按DNS/TLS/HTTP/运营商路径分层给证据，不改用户代理/VPN/防火墙作为默认补救。记录真实耗时，不承诺未测的一秒体验。

完成后停止所有QA runner/sidecar测试租约，确认没有遗留控制端口、自动化测试任务或测试配置。明确产品运行需要的只有Windows客户端、iPhone快捷指令和VPS；iPhone日常使用不要求保持WDA签名有效。

交付最终包路径、hash、版本/SHA、安装与首次使用步骤、回滚方式、完整验收状态。只有证据齐全才建议合并main并形成release；合并/发布使用正常Git流程，不force push，外部发布/上传前核对当前用户授权。不把尚未进行的网络/开机验证标PASS。
