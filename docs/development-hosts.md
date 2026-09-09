# ContinuityBridge 多主机开发

## 本次准备与实际边界

2026-09-09 preflight 时，项目目录没有 `.git`，上级和子目录也未发现 Git 元数据；因此没有可比较的 HEAD、分支、remote 或已提交基线。本次仅初始化本地 Git，初始分支为 `main`，尚无提交、remote 或 GitHub 连接。GitHub 上是否另有同名项目未核实，不能据此认定远端不存在。

保留现有 `src/`、`tests/`、`docs/` 和解决方案结构，未改业务代码、目标框架或依赖。现有八个工程均为 `net10.0-windows`，App 使用 Windows Forms 和 `win-x64`；Core 的名称不代表当前工程可跨平台运行。现有 Api 不能直接视为 Linux Cloud Relay。

本次只准备规则、忽略策略、脚本和交接流程。不安装重大依赖、Mobile MCP、WDA；不配对设备，不更改 Tailscale/防火墙，不启动或部署服务。

## 主机职责

| 主机 | 长期职责 | 当前入口与限制 |
| --- | --- | --- |
| Windows 主开发机 | Windows Client；Win32 Clipboard；托盘、自启动；自动测试和真实桌面验证 | 保留当前 solution/src/tests。现有 SDK 由 `global.json` 定义。真实 UI 测试需单独任务授权。 |
| Mac mini | USB 连接 iPhone；后续 Mobile MCP/WDA；快捷指令 E2E；截图、UI tree、点击、滑动、输入 | 本次只准备只读检查。Xcode、签名、设备信任及自动化工具另行配置；不运行现有 Windows 客户端。 |
| Linux VPS | 后续 Cloud Relay；Docker/服务部署；日志与健康检查 | 本次不连接 VPS。Relay 的可移植工程、镜像和部署方案尚未实现；不得部署现有 Windows Api。 |

未来确有代码时，再以小范围任务增加 iPhone 测试或 Relay/部署目录。现在不创建空业务工程、不搬迁现有目录。

## 本地主机设置与平台 setup/action

三台主机各自 clone 同一个经过确认的 remote；每个 clone 都有自己的 `.git`、工具链、缓存、凭证和构建输出。不要通过网盘、SMB 或目录镜像同步整个工作目录。主机名、地址、SSH 配置、Apple 签名身份、UDID 和密钥不写入仓库。

命令从仓库根目录调用；脚本内部按自身位置定位 checkout，支持含空格或中文的路径：

| 平台 | 只读 setup | 手动 action |
| --- | --- | --- |
| Windows | `pwsh -NoProfile -File ./scripts/dev/windows.ps1 -Action setup` | `pwsh -NoProfile -File ./scripts/dev/windows.ps1 -Action status` |
| macOS | `sh ./scripts/dev/macos.sh setup` | `sh ./scripts/dev/macos.sh status` |
| Linux | `sh ./scripts/dev/linux.sh setup` | `sh ./scripts/dev/linux.sh status` |

setup 成功只证明所报告的基础条件成立，不代表实验室或部署环境已就绪。macOS/Linux 的工具检查仅检查命令存在，不触发 Xcode 安装、设备发现、Docker daemon、健康 URL 或远端连接。错平台和无效 action 返回失败。

Windows 按 `CONTINUITYBRIDGE_DOTNET` → 当前 checkout 的 `.tools/dotnet/dotnet.exe` → PATH 选择已有 SDK，验证 `global.json` 当前 `latestPatch` 策略，不修改全局 PATH。新 worktree 不自带 `.tools/`；可以由本机环境变量指向已有 SDK，但不把这个绝对路径提交 Git。缺少 SDK 时停止并报告，另行授权安装。

可选 Windows Core action：

```powershell
pwsh -NoProfile -File ./scripts/dev/windows.ps1 -Action test-core
```

此 action 需要已有 NuGet restore assets，使用 `--no-restore`；会构建并运行 Core 测试，不触碰交互式剪贴板测试。首次依赖恢复应另行检查来源并使用现有 lock files 的 `--locked-mode`，不自动更新依赖锁。Core 通过不代表 Windows 真机、iPhone 或 Relay 通过。本次不要求运行业务测试。

`.codex` 适合共享这些相对命令。见 [接入说明](../.codex/README.md)。目前只提供命令表；由当前桌面 App 设置界面生成正式环境文件，尚未激活自动 setup/actions。不要在共享配置中加入 MCP 登录信息或主机专属路径。

## Git 工作流

1. **首次接入**：确认目标 GitHub URL、所有者和可见性；若远端已有历史，先只读检查，不直接推送当前无历史目录覆盖它。必要时在独立目录 clone 并逐项比较。保留当前目录全部文件。
2. **首次提交前**：核查下面的文件排除清单；对候选内容做密钥扫描和人工审核，显式选择文件暂存、检查 staged diff。不要 `git add .`，不要强制添加私密报告。创建经过审核的初始提交后才能建立 Worktree。初始基线可记录已知门禁未通过，不宣称产品完成。
3. **日常开发**：从最新稳定 `main` 建立短生命周期 `feature/<topic>` 或 `fix/<topic>`；也可在 Codex 中创建本机 worktree。开发前检查 dirty 状态，保留不属于任务的修改；不创建常驻 windows/mac 分支。
4. **跨主机交接**：推送经过审核的 feature 提交；测试主机 fetch 后在自己的 clone/worktree 检出该精确 SHA。未提交文件不会随 Git 传输，不能把不同工作树的结果合并成同一版本的 PASS。
5. **合并**：在 PR 中记录影响的平台、执行命令、结果、缺失验证和风险。满足相关门禁后合入 `main`，通过正常快进同步；若分叉或有本地改动，先处理差异，不强制覆盖。分支清理先确认工作已合入且无未保存内容。
6. **部署阶段（将来）**：仅部署明确授权且已验证的提交/tag，记录回滚目标。生产 clone 不承载临时开发修改。首次远端接入、push、PR 或部署均未在本次执行。

## 跨主机测试流程

1. Windows 提交待测版本，记录 SHA、工作树状态、SDK 和测试命令。执行与变更相关的自动测试，再按单独任务运行真实 Win32 Clipboard、托盘和自启动验证。
2. Mac mini 检出同一 SHA，记录 macOS/iOS/工具版本。后续工具与设备准备完成后，以无隐私文本验证 iPhone ↔ Windows 快捷指令流程及 UI 操作；仅连通 USB 或列出设备不能判定 E2E PASS。
3. Relay 实现且获准部署后，VPS 使用同一受测版本或有明确映射的镜像，核对健康检查、日志、重启和失败恢复；不能用本地客户端测试替代这些证据。
4. 每份结果记录：日期、SHA、工作树是否干净、主机角色、平台/工具版本、用例、预期/实际、PASS/FAIL/BLOCKED/NOT RUN、脱敏证据位置。只分享必要的脱敏摘要；原始截图、UI tree、日志和剪贴板内容留在各主机忽略的 `artifacts/`。
5. 保留原 Phase/Gate 约束；本次环境检查不更新 G2 或后续阶段结论。恢复真实剪贴板验证前，先检查本地原始计划和证据；其他主机使用前需另行整理脱敏共享版。

## 提交保护与 preflight 发现

- 已枚举当前目录（含隐藏项、工具和产物），未出现访问错误。未发现 `.env`、命名为私钥/凭证的项目文件；`.tools/dotnet` 下两份 PEM 位于 SDK trustedroots，属于工具文件，整目录不提交。
- 对排除工具、bin/obj 和原始 artifacts 后的 55 个源代码/配置/文档文件进行常见 token、私钥标记和秘密赋值模式检查，未命中。该结果是启发式扫描，不是专用扫描器认证；未审计 SDK/二进制和原始证据内容，也没有 Git 历史可扫描。
- 历史计划、预检、依赖来源文档和需求汇总含本地路径。原样保留这些文件及 `TEST_REPORT.md`，用精确 `.gitignore` 项暂时隔离；业务源码和 `NuGet.Config` 未发现需改写的机器路径（NuGet URL 的模式误报已核实）。后续单独准备脱敏版本再纳入 Git，不影响本地查阅。
- `.tools/`、`bin/obj`、`artifacts/`、publish/TestResults、IDE/系统缓存、签名文件、环境文件、`.local/` 和意外的 `%SystemDrive%` 目录被忽略。示例 `.env.example`/`.env.template` 允许提交，但必须只放占位值。
- `AGENTS.md` 已移除过时的固定浏览器插件路由和本机绝对路径，保留浏览器保护、真机证据与阶段门禁。旧版配置备份及 55 文件 SHA-256 基线保存在忽略的 `.local/development-hosts-preflight/`。
- 忽略规则不保护已经跟踪的文件，也无法阻止 `git add -f`；首次发布前必须检查 staged diff。当前没有暂存、提交或 push，所有候选文件仍为 untracked。

## 本次验证记录

详细执行结果见 [环境准备验证](development-hosts-validation.md)。Mac mini、Linux VPS、iPhone E2E、Mobile MCP/WDA、业务测试及部署必须单列 NOT RUN，不能因脚本语法检查通过而改成 PASS。
