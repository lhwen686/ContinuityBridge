# P6-W — Windows：启动真实桌面测试端

在Windows **当前登录用户的交互式桌面**执行。读COMMON与05_TEST_AND_AUTOMATION。候选SHA-B必须由P5交付且P3 staging同版本；P6-M的Mac控制端也将检出同一SHA。不运行在WSL、服务Session0或只有SSH的其他会话。

本次允许在指定测试窗口使用合成fixture覆盖并验证剪贴板。我应先保管当前剪贴板；只在本机内存尽力备份/恢复支持格式，不把私人备份写磁盘/发云/发聊天。遇到非fixture内容停止该用例，不能回传原文。

核查进程位于正确用户session、单实例、候选SHA/dirty、staging origin与test lease。按照P5真实存在的命令启动可见Windows TestAgent，连接受限QA入口；token从OS/本机安全输入读取，不打印。原始日志仅ignored artifacts。

按 `windows/QA_RUNBOOK.md` 使用安全修复后的包：先由用户手动退出日常产品 App，再为本次 TestAgent 配置仅内存的 staging 同步；不修改产品生产配置。初始窗口不读写剪贴板、不联网。人工启用后先验证服务器身份/角色/session/runId/SHA/活动状态/租约，完整备份成功后才写 sentinel。未知格式、超限、竞争或快照失败即停止且不覆盖；不能为了 READY 跳过备份。QA 的 runnerReady 仅表示 session 绑定，不代表此 gate 已通过。

agent只可执行fixture白名单、核查真实Clipboard格式/完整文本/图片像素、在指定安全测试窗口粘贴、报告脱敏摘要。无任意shell/文件/URL能力，不自动把它注册为永久开机服务。

成功准备后输出READY及非秘密runId/candidateSHA/租约截止/停止方式，让用户切到Mac执行P6-M。若当前Codex无法让进程在会话之外持续运行，提供并实际验证最小的可见测试runner启动入口，不声称后台已经存在。不要靠睡眠假装维持任务或承诺聊天结束后由模型后台工作。

Mac完成或租约到期后runner应自动退出控制状态并释放资源。最后按本地备份能力恢复，确认产品客户端测试配置没有被误切为生产。

恢复前必须取消并等待本次命令、原生任务及 staging 同步；只在所有权和序号仍属于本次测试时恢复。外部复制保留并报告跳过恢复，清理超时报告未恢复并禁用再次启用。恢复项不得自动上传；强杀/断电无恢复保证。P5 本地安全修复、mock 回归或新包本身均不能触发本阶段 READY。
