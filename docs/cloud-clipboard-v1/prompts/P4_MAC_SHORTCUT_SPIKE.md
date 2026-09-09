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
