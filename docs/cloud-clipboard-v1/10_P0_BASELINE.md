# P0 Windows 事实基线与验证报告

日期：2026-09-10。范围：已完成当前代码审阅、过时事实修订、ADR、机器契约、迁移与分阶段验收定义；未实现产品。最终交接版本使用本报告所在的实际 Git 提交，见 [SHA 接收规则](11_ACCEPTANCE_AND_HANDOFF.md)。本报告不写主机名、用户名、私人绝对路径、设备标识或真实部署入口。

## 实际现场

| 核查项 | 现场结果 |
|---|---|
| 执行端 | Windows 本地；RuntimeInformation.OSDescription 为 Microsoft Windows 10.0.26200；已现场核对用户/主机与仓库根，私人值仅留本机 |
| 仓库根 | 当前 ContinuityBridge checkout；Get-Location 与 git rev-parse --show-toplevel 一致 |
| 起始分支 / HEAD | main / `720e0d9112c330b6d809c14648141981797e038d` |
| origin | `lhwen686/ContinuityBridge`；gh repo view 返回 PRIVATE，默认 main；git ls-remote 的 main 同以上 SHA |
| 起始 dirty | 仅 `?? docs/cloud-clipboard-v1/`；全部既有跟踪文件与暂存区无改动 |
| 本阶段分支 | `feature/cloud-clipboard-v1`；创建前本地/远端同名分支均不存在；不合并 main、不强制回退 |
| 已有 SDK | 按既有脚本选择 checkout-local `.tools/dotnet/dotnet.exe`，10.0.400；未安装或修改 PATH |
| SDK 策略 | global.json: 10.0.400、latestPatch、allowPrerelease=false |
| 工程/锁 | 4个 src + 4个 tests 工程均 net10.0-windows；8份跟踪的 packages.lock.json 均可解析、lock version=2、Windows target；App另有win-x64 target；未改锁/依赖/目标框架 |
| 可用工具 | 现场找到 git、gh、pwsh、Python、Node、dotnet；实际文档生成用已有 Python 标准库，契约验证用已有 pwsh/Test-Json；不安装额外包 |
| 记忆与来源 | codex-mem 搜索 ContinuityBridge 返回空；补查已有本地历史索引后，仍以当前源码和仓库/本地原始报告确认事实；未保存新记忆 |

本地原始导入包与待修改既有文档逐文件备份到 ignored `.local/`，保留原始 hash。原导入 MANIFEST 的21个文件全部匹配。共享 MANIFEST 随本次文档更新重新生成，覆盖本包全部交付文件（不含自身），按 Git 规范化 LF 文本计算，避免跨主机 CRLF 显示转换被误判为内容变化。

## 已核实的完成度与缺口

核心模型仍是 Text/TailscaleUserLogin；StateCoordinator 组合本地写入和状态队列。24h幂等缓存持有 RemoteClipboardResult→Snapshot→完整Text，重复键不验证正文；两处uint <=存在回绕风险。Windows层有真实STA/消息泵/稳定读取/隐私标记/重试代码，但只有文本数据路径。Api仅AssemblyMarker，App仅ApplicationConfiguration.Initialize，尚无真实Relay/托盘生命周期。

Core现有13个测试方法包含队列、启动、去重、隐私与回环；Api/Integration各一个ProjectLoads不证明接口。Windows存在交互式测试与手工G2及显式门禁，不能把文件名PlaceholderTests的全部内容都当占位。本次只读这些测试，未执行。旧TEST_REPORT的G2仍为NOT PASS；本次不改历史结果、不访问真实剪贴板。

逐条证据及 P2/P5 归属在 [迁移清单 M01–M11](08_CODE_MIGRATION.md)。九条需求与默认值在 [ADR-001](09_ADR_V1_BASELINE.md)。Schema/HTTP契约及行为验收分开，避免将结构校验视作已实现系统。

## 执行与验证

| 命令/检查 | 退出 / 状态 | 证据与实际边界 |
|---|---|---|
| git rev-parse、status、branch、remote、ls-remote；gh repo view --json nameWithOwner,visibility,defaultBranchRef | 0 / PASS | 上表事实；没有 fetch 回退或覆盖现有工作 |
| `pwsh -NoProfile -File scripts/dev/windows.ps1 -Action setup` | 0 / PASS | Windows checkout与SDK10.0.400；输出主动声明业务/设备NOT RUN |
| `pwsh -NoProfile -File scripts/dev/windows.ps1 -Action status` | 0 / PASS | 起始main/HEAD/仅导入目录untracked |
| 源码、八份csproj/锁与历史计划只读检查 | PASS | 事实核实；不是测试执行 |
| `pwsh -NoProfile -File docs/cloud-clipboard-v1/contracts/validate.ps1` | 0 / PASS | 46个结构正反例、全部本地JSON引用、8个HTTP操作/条件头、默认capabilities |
| 新增尾换行反例的首次执行 | 1 / FAIL，已修复 | 普通正则 `$` 接受末尾换行；ETag负例真实失败。已改为跨引擎严格结束断言，增补etag/revision/UUID/itemId四个反例，以上46例通过 |
| 完整OpenAPI官方meta-schema认证 | NOT RUN | 未安装额外验证器；当前是已有Test-Json的结构案例与本地引用/接口清单检查 |
| 文档链接、提示词汇总一致性、MANIFEST、秘密/私人路径扫描 | 0 / PASS | 33个显式候选文件，30个本地链接，11个提示词正文与汇总一致，30项MANIFEST规范化hash匹配；启发式模式扫描无命中，不是专用扫描器认证 |
| 显式 `git add -- <33文件allowlist>`、`git diff --cached --check`、暂存内容审阅/扫描 | 0 / PASS | 暂存集等于allowlist；暂存blob与规范化工作树一致；再次检查8个HTTP入口/15个Schema和既有两文档diff；无业务/工具/配置改动 |
| restore/build/dotnet test、Relay HTTP/WSS运行、Win32/托盘/自启、Mac/iPhone、VPS/网络/部署 | NOT RUN | 全部在本次授权之外；未安装工具、未访问当前剪贴板、未启动服务 |

只读检索曾因 PowerShell 路径中的 rg 通配符未展开返回错误；改用目录+`-g`重新完成检索，不影响源码或最终判断。

提交前复核采用保存在 ignored `.local/` 的一次性 Python 标准库脚本，显式候选为以下33个文件；脚本检查链接、汇总、hash、暂存blob与工作树一致、秘密模式，以及 src/tests/solution/SDK/锁/脚本/忽略配置无差异。最终暂存审阅与commit/push的实际结果由结束交付消息及Git记录给出；未成功的操作不得称已完成。

```text
AGENTS.md
docs/development-hosts.md
docs/cloud-clipboard-v1/00_START_HERE.md
docs/cloud-clipboard-v1/01_ARCHITECTURE.md
docs/cloud-clipboard-v1/02_PROTOCOL.md
docs/cloud-clipboard-v1/03_DEVICE_RUNBOOK.md
docs/cloud-clipboard-v1/04_IPHONE_SHORTCUTS.md
docs/cloud-clipboard-v1/05_TEST_AND_AUTOMATION.md
docs/cloud-clipboard-v1/06_DEPLOYMENT.md
docs/cloud-clipboard-v1/07_OFFICIAL_SOURCES.md
docs/cloud-clipboard-v1/08_CODE_MIGRATION.md
docs/cloud-clipboard-v1/09_ADR_V1_BASELINE.md
docs/cloud-clipboard-v1/10_P0_BASELINE.md
docs/cloud-clipboard-v1/11_ACCEPTANCE_AND_HANDOFF.md
docs/cloud-clipboard-v1/ALL_CODEX_PROMPTS.md
docs/cloud-clipboard-v1/MANIFEST.json
docs/cloud-clipboard-v1/contracts/README.md
docs/cloud-clipboard-v1/contracts/default-capabilities.json
docs/cloud-clipboard-v1/contracts/openapi.json
docs/cloud-clipboard-v1/contracts/schema-cases.json
docs/cloud-clipboard-v1/contracts/schemas.json
docs/cloud-clipboard-v1/contracts/validate.ps1
docs/cloud-clipboard-v1/prompts/COMMON.md
docs/cloud-clipboard-v1/prompts/P0_WINDOWS_BASELINE.md
docs/cloud-clipboard-v1/prompts/P1_MAC_LAB_PREFLIGHT.md
docs/cloud-clipboard-v1/prompts/P2_WINDOWS_RELAY.md
docs/cloud-clipboard-v1/prompts/P3_VPS_STAGING.md
docs/cloud-clipboard-v1/prompts/P4_MAC_SHORTCUT_SPIKE.md
docs/cloud-clipboard-v1/prompts/P5_WINDOWS_CLIENT.md
docs/cloud-clipboard-v1/prompts/P6M_MAC_E2E.md
docs/cloud-clipboard-v1/prompts/P6W_WINDOWS_TEST_AGENT.md
docs/cloud-clipboard-v1/prompts/P7_VPS_PRODUCTION.md
docs/cloud-clipboard-v1/prompts/P8_WINDOWS_RELEASE.md
```

## 变更与停止点

修改范围为 AGENTS.md、docs/development-hosts.md 和完整 `docs/cloud-clipboard-v1/` 文档/契约包。保留导入提示词原有授权，只做本机路径脱敏及对应汇总同步；未改 src/tests、solution、global.json、锁、工具或忽略规则。没有 reset/clean/force push/盲目git add .。

P0 没有域名或设备权限阻塞；真正未验证的是后续产品功能。交接 Mac 的 P1 入口及 Windows 的 P2 入口见 [11](11_ACCEPTANCE_AND_HANDOFF.md)。VPS 收到 P0 只读设计，等待 P2 SHA-A 和明确 P3 任务；staging/production 分别需具体计划审批。本次停止在 P0，不自动打开、启动或委派其他阶段。
