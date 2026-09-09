# 03 分设备具体操作流程

## A. 一次性放置与会话选择

1. 在 Windows 现有 ContinuityBridge checkout 中放入 `docs/cloud-clipboard-v1/`。这是文档导入，不覆盖 `src`、`.git`、配置或原始报告。
2. Windows Codex 打开**这个现有本地项目**；选择 Local，或在本机创建专门 Worktree。首次导入文档还没提交时，先在当前 checkout 完成 P0，避免新 worktree 看不到未提交文档。
3. Mac Codex 打开本机既有 ContinuityBridge clone，通常直接使用 Local。需要此包但 Windows 还没推送时，可仅把本包放到 Mac 临时目录作为 P1 参考，不改业务文件。P0 提交后正式从 Git fetch 同步。
4. VPS 阶段从已配置的桌面远程项目进入 SSH 主机；会话执行端应实际输出 Linux、正确用户和仓库根目录。不能只因为聊天窗口在 Windows 就认为命令在 Windows。
5. 所有阶段先确认实际环境。模型选择 GPT-6 Astra（若该客户端可选）；不要求把 API model id 写进全局配置。没有相应界面不阻塞本地 `codex` CLI 流程，但不要伪造远程按钮或自动移交。

本机检查命令（不创建服务）：

Windows 项目根目录：
```powershell
git status --short --branch
git rev-parse --show-toplevel
git log -1 --oneline
pwsh -NoProfile -File ./scripts/dev/windows.ps1 -Action setup
```

Mac：
```bash
# 先在本机既有 ContinuityBridge clone 的仓库根目录执行
git status --short --branch
git log -1 --oneline
sh ./scripts/dev/macos.sh setup
```

若 pwsh 不存在，先检查现有脚本是否支持本机 Windows PowerShell，再使用对应入口；不要为了只读检查自动安装 PowerShell。现有脚本缺少时先读实际 checkout，不能虚构已成功执行。

## B. 阶段表

| 顺序 | 提示词 | 在哪打开 | 命令/工具实际执行位置 | 完成门槛 |
|---|---|---|---|---|
| P0 | `P0_WINDOWS_BASELINE.md` | Windows 现有项目 Local | Windows | 基线、迁移清单、契约与共享文档提交；不改业务 |
| P1，可与 P0 并行 | `P1_MAC_LAB_PREFLIGHT.md` | Mac 现有项目 Local | Mac + USB iPhone | 设备枚举、真实 UI tree/截图/安全点击证据；不是产品 E2E |
| P2 | `P2_WINDOWS_RELAY.md` | Windows 开发分支 | Windows；可移植测试 | 新 Relay/Contracts、真实 API、单元/集成测试、版本 SHA-A |
| P3 | `P3_VPS_STAGING.md` | VPS 的 SSH 远程项目 | VPS Linux | 先审阅部署计划，再批准 staging；部署指定 SHA-A |
| P4 | `P4_MAC_SHORTCUT_SPIKE.md` | Mac Local | Mac 控制真 iPhone + staging | 真机文本/图片 HTTP 与实际图片粘贴打通 |
| P5 | `P5_WINDOWS_CLIENT.md` | Windows Local | Windows 交互式用户会话 | 完整自动客户端、Windows 测试、受限 QA runner、候选 SHA-B |
| 再执行 P3 | 同一 staging 提示词的升级模式 | VPS SSH | VPS | staging 升级到 SHA-B，测试专用协调器隔离上线 |
| P6-W | `P6W_WINDOWS_TEST_AGENT.md` | Windows Local | Windows 当前桌面 | 当前 SHA-B 的交互式 runner READY；保持会话桌面可用 |
| P6-M | `P6M_MAC_E2E.md` | Mac Local | Mac + 真 iPhone；通过 staging 协调 Windows runner | 双向真实复制粘贴与完整矩阵有证据 |
| P7 | `P7_VPS_PRODUCTION.md` | VPS SSH | VPS | 明确批准后部署生产；无 QA 通道；回滚演练 |
| P8 | `P8_WINDOWS_RELEASE.md` | Windows Local | Windows + 用户最后一次手机操作 | 安装/退出/自启/暂停；Mac/USB 脱离后的真实使用验收 |

P4 应在 P5 大规模开发前做。产品最不确定的部分是 iPhone 上“实际图片对象 → 二进制上传 → 下载 → 图片剪贴板”，不是写 HTTP API。P4 只证明手机与 staging 的能力，不冒充整个 Windows 双向 E2E。

## C. 一次可复制的启动方式

在正确主机/项目的 Codex 会话中输入：

```text
执行 docs/cloud-clipboard-v1/prompts/P0_WINDOWS_BASELINE.md。
先读取其中引用的 COMMON.md 与设计文件，再按阶段授权完成工作。
核实实际执行主机与 Git 状态，不执行其他阶段，不只返回计划。
```

将文件名替换为阶段表中当前阶段。P3/P7 会先完成只读部署准备并给出可审核计划，只有用户对具体计划批准后才能改变远端服务。

## D. Git 协作规则

P0 细化的文件所有权、验收门槛和精确 SHA 接收步骤见 [11_ACCEPTANCE_AND_HANDOFF.md](11_ACCEPTANCE_AND_HANDOFF.md)。P0 提交只可作为设计基线，不是可部署 Relay；不要把下面的后续阶段职责当作自动执行授权。

Windows 作为集成责任端，可创建短期 `feature/cloud-clipboard-v1`。Mac 的快捷指令/实验文档变更放在独立短期分支，例如 `test/cloud-clipboard-ios-spike`，由 Windows 审阅整合。不要创建常驻 `windows` / `mac` / `vps` 产品分支。

本包提示词允许在**已验证的现有私有 origin**上创建任务分支、明确暂存变更、常规 commit 和 push；不授权 main 强制更新、仓库改公开或无审批的生产部署。不能盲目 `git add .`。发生已有未提交改动时先隔离/保留，不能 reset/clean。Worktree 是本地主机资源，不从另一主机同步整个文件夹。[S4]

每次跨机交接都传“提交 SHA + 要执行的阶段 + 测试摘要”。接收方 fetch 后在自己干净的测试 worktree 检出精确 SHA。禁止在有未保存修改的当前树上强行 checkout；远端分支不在最新并不意味着可以强制覆盖。被忽略的 `.local/`、`.tools/`、密钥、签名和截图不会随 Git 自动迁移。

P4 新增快捷指令模板/测试文件后必须先整合到候选提交，再让 Windows、Mac 和 staging 针对同一候选版本做 P6。如果测试发现修复，形成新 SHA，重新部署 staging 并重跑受影响测试。最终不可把 SHA-A 的手机测试与 SHA-B 的客户端测试拼成“SHA-B 全通过”。

## E. SSH 与可选远程控制

若桌面有 Remote SSH 入口，先用已有别名测试 `ssh <VPS_SSH_ALIAS>`，再核实远端登录 shell 可以找到并运行已安装/已登录的 `codex`；当前官方说明要求远端 Codex 与项目目录存在。桌面 Settings → Connections 中启用 SSH，选择远端项目目录。[S3]

SSH 别名未知时从用户的现有本机配置进行最小读取，避免显示私钥/秘密；不能猜 hostname 或把说明占位符当成命令运行。远端 Codex 不可用先报告具体状态，再走用户批准的安装/登录；不读取 Windows 凭据自动复制到 VPS。

Mac 不需要为了本项目新增入站公网端口、VPN 或 Tailscale。没有已可用远程控制时，在 Mac 本地运行 P1/P4/P6-M 即可。未来跨设备聊天移交是可选辅助，不等于多个 agent 自动同步进度。不要公开 Codex App Server、MCP 或 WDA 的监听端口。

## F. 只剩哪些用户输入不能自动推断

当前 VPS 的 SSH 接入标识；计划使用的 staging/production HTTPS 域名或现有可信入口；用户是否批准对应服务/证书/必要端口变更；iPhone 第一次解锁、信任、签名/粘贴权限；最后的真实桌面/蜂窝网络验收窗口。

这些信息在涉及的阶段收集。不要在 P0 为等待域名而停止文档和契约工作，也不要让用户再回答已经确认的九条需求。token/密码不要求贴进聊天，使用本机安全输入。
