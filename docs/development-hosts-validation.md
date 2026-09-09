# 多主机环境准备验证 — 2026-09-09

## 结果

本次完成本地仓库和环境入口准备。主机互联、首次提交/GitHub 接入、原生 Mac/Linux 执行及产品门禁未完成，不据此宣称三机模式已联通。

| 检查 | 结果 | 范围 |
| --- | --- | --- |
| 初始 Git preflight | 已核实 | 原目录、上级及子目录无 Git 元数据；不存在可比较的提交基线 |
| 本地 Git 初始化 | PASS | repo root 为当前项目根目录；`main` 尚无提交；0 tracked files、0 remotes |
| 原文件保护 | PASS | 对初始 55 个源码/配置/文档文件做 SHA-256 比较；只有获准修改的 AGENTS.md 与 .gitignore 变化，其余 53 个一致 |
| Windows setup | PASS | PowerShell 7 和 Windows PowerShell 实际执行；现有 .NET SDK 10.0.400 符合 global.json；未安装依赖 |
| Windows status | PASS | 两种 PowerShell 实际输出 unborn main 和候选文件；明确提示首次提交前无法进行 Worktree 交接 |
| PowerShell 语法 | PASS | PowerShell parser 无错误 |
| macOS/Linux 脚本语法 | PASS，仅静态检查 | 使用现有 Git for Windows sh 执行 `-n`，不是原生主机验证 |
| macOS/Linux 错平台和非法 action | PASS | 在 Windows 返回预期失败码 1；非法 action 返回 2；未连接主机、执行部署 |
| 忽略规则 | PASS | 17 个秘密/产物/本地文件路径被忽略；6 个示例/源文件/共享环境路径允许提交 |
| Shell 换行规则 | PASS | 三个平台入口均指定 text/eol=lf，实际文件字节也无 CR |
| 源文件秘密检查 | 未发现模式命中 | 初始 55 个文件及最终 56 个 Git 候选文本文件的启发式 token/私钥/秘密赋值扫描；最终候选无主机绝对路径模式命中，不能替代专用 secret scan 或人工审核 |
| 构建/Core action/业务测试 | NOT RUN | 本次只修改环境准备文件；未运行 test-core，不刷新原测试门禁 |
| Win32/UI/托盘/自启动/iPhone E2E | NOT RUN | 需要另行执行对应真机流程 |
| Mac mini/Linux 原生 setup | NOT RUN | 尚未在目标主机执行 |
| Mobile MCP/WDA、VPS 部署和健康检查 | NOT RUN | 本次明确排除 |
| Codex 自动 setup/工具栏 actions | NOT RUN | `.codex/README.md` 提供命令；尚未由 App 生成/启用环境配置 |
| GitHub、初始 commit、push、Worktree | NOT RUN | 没有提供目标 remote；不擅自创建或覆盖远端 |

## 验证期间修正

首次 ignore 探针通过 PowerShell 文本管道传入 `git check-ignore --stdin`，路径尾部被附加 CR，造成 `.env.example` 等结果误报。已改为逐项直接传递路径参数，17 个忽略项和 6 个允许项全部通过；未据此修改正确的忽略规则。

## 保留与排除

配置修改前备份和哈希基线仅在忽略目录 `.local/development-hosts-preflight/`；不进入 Git。历史计划、原测试报告、依赖来源文档、需求汇总、工具及原始证据均保留原文件。含机器信息的历史记录采用精确忽略项隔离，之后可单独整理脱敏共享版。

未暂存、提交、推送，未配置 remote，未重写历史。最终 status 的 `??` 同时包含初次纳入 Git 的原有源码和本次新增文件，并不表示这些原有源码由本次新建。
