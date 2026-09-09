# 07 官方与上游资料索引

核查基准：本次 2026 年 9 月设计会话。官方文档可能更新，实施阶段对相关页面复核。设计中的 API、默认参数、QA mailbox 和阶段门禁是本项目建议，不冒称为厂商规定。

P0 于 2026-09-10 选择性复核 S8 以及以下协议/Win32 原始资料。其余条目保留为导入设计包的资料索引，不宣称全部已在 P0 重新验证，也不由工具安装或官方说明推导真机通过。

| P0 复核来源 | 用于本次决策 |
|---|---|
| [Microsoft GetClipboardSequenceNumber](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getclipboardsequencenumber) | 返回 DWORD；无剪贴板访问权限可能返回0；值随内容改变/清空增加。不能将它当无限单调时钟；回绕处理是本项目迁移要求 |
| [RFC 9110 If-Match](https://www.rfc-editor.org/rfc/rfc9110.html#name-if-match) | 强比较和前置条件；本项目约束为单个完整状态 ETag |
| [OpenAPI 3.1.1](https://spec.openapis.org/oas/v3.1.1.html) | 固定该文档格式版本与 JSON Schema 2020-12；不宣称它是最新版 |
| [Apple API 请求](https://support.apple.com/guide/shortcuts/request-your-first-api-apd58d46713f/ios) | 请求体 JSON/Form/File 说明；20MB、错误处理与图片粘贴仍须 P4 真机验证 |

## S1 · OpenAI：GPT-6 Astra Model guidance

https://developers.openai.com/api/docs/guides/latest-model

任务主动性、AGENTS/skill 冲突、分工和比例适当的验证；本包提示词为原创任务指令，不是官方逐字模板。

## S2 · OpenAI：GPT-6 Astra model

https://developers.openai.com/api/docs/models/gpt-6-astra

模型 ID 与推理档位；不代表每个用户客户端已开放相同选择器。

## S3 · OpenAI：Remote connections

https://learn.chatgpt.com/docs/remote-connections

SSH 远端执行、远端 CLI、连接/移交边界与不要暴露 App Server。

## S4 · OpenAI：Git worktrees

https://learn.chatgpt.com/docs/environments/git-worktrees

本地工作树隔离、分支/提交交接；不是跨设备目录同步。

## S5 · OpenAI：AGENTS.md

https://learn.chatgpt.com/docs/agent-configuration/agents-md

持久项目约束与作用域；更新旧事实不等于删除安全边界。

## S6 · Mobile Next：mobile-mcp

https://github.com/mobile-next/mobile-mcp

上游工具能力与使用方式，以实际安装版本/工具发现为准。

## S7 · Mobile Next：Getting Started with iOS Real Device

https://github.com/mobile-next/mobile-mcp/wiki/Getting-Started-with-iOS-Real-Device

真机所需 WDA、go-ios、USB 转发/开发者环境。

## S8 · Apple：Request your first API in Shortcuts

https://support.apple.com/guide/shortcuts/request-your-first-api-apd58d46713f/ios

Get Contents of URL 的 JSON/Form/File 请求体；不保证任意20MB工作流已验证。

## S9 · Apple：Run shortcuts from the command line on Mac

https://support.apple.com/guide/shortcuts-mac/run-shortcuts-from-the-command-line-apd455c82f02/mac

macOS shortcuts CLI；不要混同 iPhone 执行。

## S10 · Apple：Run a shortcut using a URL scheme

https://support.apple.com/guide/shortcuts/run-a-shortcut-from-a-url-apd624386f42/ios

在 iPhone 打开 shortcuts:// URL 运行已存在的快捷指令。

## S11 · Appium XCUITest：Get/Set Clipboard

https://appium.github.io/appium-xcuitest-driver/latest/guides/clipboard/

真机 WDA 前台限制；工具存在不等于可任意读取后台剪贴板。

## S12 · Microsoft：Clipboard formats

https://learn.microsoft.com/en-us/windows/win32/dataxchg/clipboard-formats

Windows 多格式剪贴板、标准/注册格式。

## S13 · Microsoft：Interactive services

https://learn.microsoft.com/en-us/windows/win32/services/interactive-services

服务和交互式用户会话边界。

## S14 · Cloudflare：Durable Objects limits

https://developers.cloudflare.com/durable-objects/platform/limits/

当前存储值/行大小约束，不能直接保存20MB图片到单一存储值。

## S15 · Cloudflare：Workers KV FAQ

https://developers.cloudflare.com/kv/reference/faq/

KV eventual consistency 不适合单独充当权威 latest 状态协调器。

## S16 · Cloudflare：Workers limits

https://developers.cloudflare.com/workers/platform/limits/

请求、CPU、内存等限制；未来迁移时复核，不承诺永远免费。

## S17 · Docker：Packet filtering and firewalls

https://docs.docker.com/engine/network/packet-filtering-firewalls/

容器发布端口与 ufw/防火墙相互作用。

## S18 · Caddy：Automatic HTTPS

https://caddyserver.com/docs/automatic-https

可信证书的部署要求与 ACME 挑战；不以关闭验证代替。

## S19 · Apple：About your developer account

https://developer.apple.com/help/account/basics/about-your-developer-account/

Personal Team/真机测试签名限制；不把实验室签名当产品长期运行依赖。

## R1 · 项目当前 AGENTS.md

https://github.com/lhwen686/ContinuityBridge/blob/720e0d9112c330b6d809c14648141981797e038d/AGENTS.md

私有仓库；本次通过已连接 GitHub 读取。

## R2 · 项目 development-hosts.md

https://github.com/lhwen686/ContinuityBridge/blob/720e0d9112c330b6d809c14648141981797e038d/docs/development-hosts.md

三个主机职责、Windows-only 目标与历史环境检查范围。

## R3 · 项目 StateCoordinator.cs

https://github.com/lhwen686/ContinuityBridge/blob/720e0d9112c330b6d809c14648141981797e038d/src/ContinuityBridge.Core/StateCoordinator.cs

当前文本状态、幂等缓存和 Windows 序号语义；实施时重新读取最新版本。
