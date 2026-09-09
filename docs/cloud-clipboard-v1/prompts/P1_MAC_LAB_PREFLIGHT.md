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
