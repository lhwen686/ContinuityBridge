# ContinuityBridge 云端剪贴板 v1：设计与 Codex 实施包

状态：**P0 文档与契约基线；不是已经实现或验收通过的软件。** 2026-09-10 已在 Windows 核查现有代码并建立 ADR、机器契约、迁移与验收清单。提交交接以当前 Git SHA 和 [P0 报告](10_P0_BASELINE.md) 为准；本阶段未实现业务、访问剪贴板、安装工具、连接真机或部署 VPS。

后续 P2 已新增 Relay 实现；运行入口、实现边界、测试结果与 SHA-A/P3 输入见 [P2 交接](12_P2_RELAY_HANDOFF.md)。上面的 P0 状态保留为历史记录，后续结果不改写旧 G2 或手机/Linux 验收。

适用基线：2026-09-10 核查的私有 `lhwen686/ContinuityBridge`，远端 `main` 为 `720e0d9112c330b6d809c14648141981797e038d`。执行时必须重新检查，不得把此 SHA 当成强制回退目标。各主机使用自己的既有 checkout；私人绝对路径、VPS 地址、域名和部署状态只在相应主机核实，不写进共享记录。

## 从这里开始

把本包的整个 `cloud-clipboard-v1` 文件夹放进 **Windows 现有项目的 `docs/`**，得到 `docs/cloud-clipboard-v1/00_START_HERE.md`。不要用本包覆盖整个项目，也不要把旧审阅包重新覆盖到已建立 Git 历史的 checkout 上。

先在 Windows 项目的 Codex 本地会话执行 `prompts/P0_WINDOWS_BASELINE.md`。Mac 可同时执行 `prompts/P1_MAC_LAB_PREFLIGHT.md`；P1 是实验室检查，不需要等待业务代码。其余阶段按 `03_DEVICE_RUNBOOK.md` 的门槛推进。

每次只把**当前阶段**交给对应主机。各提示词会要求 Codex 读取公共约束和必要设计文件，不必把十份提示词一起发送。没有把本包放进项目时，可以直接粘贴该阶段全文，并把公共约束和它依赖的设计文档作为附件提供。

## 文件索引

| 文件 | 用途 |
|---|---|
| `01_ARCHITECTURE.md` | 产品范围、运行架构、状态语义、安全、未来升级 |
| `02_PROTOCOL.md` | 端点、数据约束、并发/幂等/过期语义 |
| `03_DEVICE_RUNBOOK.md` | 谁在什么设备上执行、分支和版本交接、阶段顺序 |
| `04_IPHONE_SHORTCUTS.md` | 两个产品快捷指令的操作逻辑及真机探路 |
| `05_TEST_AND_AUTOMATION.md` | 自动化协调方式、真实剪贴板证据、验收矩阵 |
| `06_DEPLOYMENT.md` | VPS 测试/生产分离、部署审批、回滚、安全边界 |
| `07_OFFICIAL_SOURCES.md` | 本次核查的官方与上游资料、适用范围 |
| `08_CODE_MIGRATION.md` | 已有代码保留、修改、隔离清单 |
| `09_ADR_V1_BASELINE.md` | 九条需求、补充默认值及旧方案替代决策 |
| `10_P0_BASELINE.md` | 实际 Windows/Git/SDK/代码核查与 P0 验证范围 |
| `11_ACCEPTANCE_AND_HANDOFF.md` | 文件所有权、行为案例、分阶段 gate、Mac/VPS SHA 交接 |
| `contracts/README.md` | OpenAPI/Schema、离线校验入口及其验证边界 |
| `prompts/COMMON.md` | 每阶段共用的执行约束 |
| `prompts/P0_…` 至 `P8_…` | 十份分设备阶段提示词，P6 分 Windows/Mac |
| `ALL_CODEX_PROMPTS.md` | 公共约束和全部阶段提示词的汇总，方便查阅；不要一次执行全部 |

## 已确认的需求

只同步文本和图片；图片双向；iPhone 手动发送/取回；Windows 自动同步；不依赖 LAN/Tailscale；v1 用现有 4 核 8 GB VPS，但协议为 Cloudflare 留出适配空间；云端只保留全局最新一条、30 分钟；图片 20 MB；TTL/大小可配置；v1 不做 E2EE，v2 预留版本化升级。

## 本设计补充的默认值，不冒充用户已逐项确认

20 MB 按 **20,000,000 字节**计，服务端通过 capabilities 明示。v1 是静态 PNG/JPEG，不是动图/文件；文本限 1,000,000 个 UTF-8 字节，可配置。云端正文默认**只在内存保存，Relay 重启会清空最新项**。过期只影响云端，不清除设备已粘贴或仍在本地剪贴板的内容。离线期间不持久化待发送正文，不在恢复后无条件复活旧复制记录。

这些默认值已在 [ADR-001](09_ADR_V1_BASELINE.md) 中采用并细化；后续有证据的调整须更新 ADR、契约和受影响验收。必须在生产启用前向用户明确“服务重启会清空临时项”。

## 模型与提示词

在当前 Codex 的模型选择器中选择可用的 GPT-6 Astra；本包推荐工程实现阶段使用较高推理档位，常规检查不必全部用最高档。界面名称和远程功能以用户当前客户端为准。本包是任务提示词，不要求修改 API 参数、账号配置或全局安全设置。写法依据 OpenAI 当前 Astra 模型指南：结果明确、范围明确、授权内持续完成、按风险测试、指出真实阻塞，不靠角色夸张或要求展示思维链。
