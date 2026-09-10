# P4 真 iPhone 动作探测与 staging 等待记录

> 历史动作探测快照。P3 随后完成；实际手机传输、模板和最新交付状态见
> [P4 实测报告](p4-real-iphone-results.md)。本页不再作为当前阶段状态。

2026-09-10，Mac mini USB 实验室。当时 **P4 BLOCKED：用户确认 P3 尚未执行完毕。**
本记录只交接已完成的真机 UI 探测，不放行 P5，不是产品快捷指令验收。
用户要求持续监听 P3；已建立本任务每五分钟检查一次的监听，未变化时不重复通知。

## 版本与边界

- P2/SHA-A：`190c75680206eea8ae54dcbe3fbc017587adf6e8`，来自实际远端提交与
  [P2 交接](../12_P2_RELAY_HANDOFF.md)。该提交不证明 P3 已部署。
- 本机从 clean 的 `main` / `720e0d9112c330b6d809c14648141981797e038d`
  fetch 后建立独立 `feature/p4-iphone-shortcuts`，基于上述 SHA-A。
- macOS 26.6.2（25G83）；MCP 实时报真实 iPhone18,2 / iOS 26.6.2 / online。
- 使用现有 Mobile MCP / DeviceKit；没有运行独立 Appium WDA。
  P1 记录版本为 Mobile MCP 1.0.3、mobilecli 1.0.9；本次修复运行时哈希现场核对为
  `c41c82c4bc50668727ee5fabd00505915d83971b7b68ff56be9dd35a3a6b9d72`。
  启动后 8100 实际仅监听 `127.0.0.1`。
- 未安装或升级工具，未改协议、Windows 源码、部署或网络配置。
  未打开用户照片/聊天，未调用剪贴板 get/set，未运行任何上传/下载快捷指令。
- 当前无可调用 codex-mem 搜索接口；本地历史记录只用于定位，实际连接与 UI 重新验证。

## 当前系统真实动作与参数

下表来自本次 iPhone Shortcuts 中文 UI 的搜索、动作说明和设置菜单。
“发现”仅表示 UI 存在；数据类型转换、HTTP 行为和完整性仍需运行证明。

| 动作或入口 | 实际观察 | 尚未验证 |
| --- | --- | --- |
| 获取剪贴板 | 搜索“剪贴板”可见；动作说明要求设备解锁，并把内容传给下一动作 | 真实图片类型判定、读取一次后保存 OriginalClipboard |
| 拷贝至剪贴板 | 与“获取剪贴板”同时出现在搜索结果 | 正文/图片变量绑定及目标 App 粘贴 |
| 获取URL内容 | 实际加入探测草稿，展开参数；方法菜单有 GET、POST、PUT、PATCH、DELETE | HTTPS、认证、响应类型、HTTP 错误行为 |
| POST 请求体 | 菜单实际显示 JSON、表单、文件；JSON 显示“添加新字段” | 长文本 JSON 不截断、转义与 UTF-8 完整性 |
| 文件请求体 | 选“文件”后出现“文件 → 选取变量”，不是要求打开文件选择器 | 绑定真实剪贴板图片编码数据并进行二进制上传 |
| 头部 | HTTP 动作展开后有“头部”设置入口 | Authorization、完整 etag → If-Match、Idempotency-Key 实际绑定与发送 |
| 转换图像 | 搜索可见；说明列出目标图像格式、质量、保留元数据 | 原图支持类型识别、PNG/JPEG 编码、alpha/像素保持 |
| 从输入中获取图像 | 搜索结果可见 | 受验证下载正文转换为实际图片对象 |
| 获取图像的详细信息 | 搜索结果可见 | 宽高、方向、像素上限判定 |
| 获取文件的详细信息 | 实际加入草稿；属性菜单有文件大小、扩展名、路径、创建/修改日期、名称 | 文件大小的变量类型、单位和编码字节精度 |
| UUID 搜索 | 搜索框输入 UUID，本次返回树未列出匹配动作 | 不能据此断言系统没有 UUID 能力；须继续探测正式动作/可用方式并验证生成值 |

部分动作卡片主句未出现在 accessibility tree 中，本次使用新鲜手机截图定位展开按钮，
再用实际菜单元素继续操作。没有猜测动作 ID 或生成 plist/JSON 安装文件。

## 已保存的探测草稿

手机资料库中实际保存了 **CB P4 动作探测（勿运行）**，不是“CB 发送”或“CB 取回”。
没有 URL、token 或任何秘密，也没有读取或写入剪贴板的动作；从未运行。
动作顺序和绑定如下，专用于重现设置界面，不能作为产品使用：

1. **获取URL内容**：URL 未配置；方法 POST；请求体“文件”；文件变量未选择。
2. **获取文件的详细信息**：系统自动绑定上一动作的 **URL的内容**，属性选择 **文件大小**。

这里“URL的内容 → 文件大小”只是编辑器自动连线，不能证明下载一定成为图片。
最终产品的发送源必须明确来自真实剪贴板；取回最后一步必须明确绑定完整文本或图片变量。
配置和产品完整动作链尚未制作，不能用此草稿替代交付。

## MCP 能力与秘密边界

本次实际成功调用设备枚举、前台 App、启动 Shortcuts、UI tree、截图、点击、文字输入、
返回主页。手机最后返回 Home，用户原剪贴板未经本任务读写。
等待期间通过已有 stop 脚本停止本次实验 daemon，exit 0；随后 8100 无监听。
当前 `mobile_clipboard` 工具参数仅支持文本读取/设置，没有图片二进制参数。
未测试前台限制，也未把未调用或空返回当成正确的剪贴板结论。
`mobile_open_url` 的 `shortcuts://` 路由本轮 NOT RUN；不得用 Mac 上的 `open` 或
`shortcuts run` 声称 iPhone 已运行。

未发现可直接用于本任务的 Keychain 辅助工具。后续按
[04 配置约束](../04_IPHONE_SHORTCUTS.md) 由用户本机录入单独 staging 配置，
秘密页面暂停截图及 UI tree。纯快捷指令中的 token 可被有权查看/编辑该快捷指令的人读取，
并可能随快捷指令同步或分享；不能宣称为独立 Keychain 保险库存储。
产品导出不包含本地 CB 配置、私有地址或 token。

## Fixture 与验收状态

已在 ignored artifacts 运行 SHA-A 自带 `tests/relay-blackbox/fixtures.py`，exit 0。
生成 40-byte Unicode 样本、86-byte 透明 PNG、632-byte JPEG，以及
19,999,999 / 20,000,000 / 20,000,001-byte PNG。
manifest SHA-256：`b88905a3d26cd98586f690d4a95fe7ab9a885308ff9f79ee66cdcee4eae9f113`。
大 PNG 使用原有合法块生成逻辑；本次未独立解码，未上传，也未在手机读取。
40-byte 样本不是长文本测试；接下来仍须准备完整长文本 fixture。

| 验收项 | 当前状态 |
| --- | --- |
| 真机动作搜索、HTTP POST 文件参数、文件大小属性存在 | PASS，仅 UI 探测 |
| P3 按 SHA-A 上线 / iPhone HTTPS / staging 配置 / 指定测试便笺 | BLOCKED，P3 未完成，交接与便笺待明确 |
| 完整长文本 JSON、etag 原样 If-Match、UUID 幂等重放 | NOT RUN |
| 真剪贴板图片源、编码类型/长度、File 二进制上传 | NOT RUN |
| 下载后实际图片剪贴板、透明 PNG/JPEG 目标便笺粘贴 | NOT RUN |
| 近 20MB 合法图片、超限错误、传输与像素 oracle | NOT RUN，不声称 20MB 稳定 |
| GET 后替换、到期、认证错误保留旧剪贴板 | NOT RUN |
| 真实导出及重新导入的去秘密 .shortcut | BLOCKED，尚无完成并验收的产品快捷指令；未尝试导出，不代表系统导出能力失败 |
| Windows 原生双向 E2E、QA oracle 租约、脱离 USB | NOT RUN |

原始动作树和设置截图只在 ignored `artifacts/p4-iphone-20260910/`，不进入 Git。
其中 `action-discovery.json` 包含本轮各项实际 UI 返回，
`http-file-size-draft.png` 为未配置秘密的草稿截图；fixture 清单在 `fixtures/manifest.json`。
共享交接只包含本文。没有伪造 .shortcut 文件，也没有把 API fixture 当 Windows E2E。

## 恢复 P4

监听先检查 P3 新交接及实际部署 SHA，不将远端分支变化自动等同于上线。
确认 staging 就绪后，读取本机地址/测试配置的安全交接方式，并明确测试便笺；
用户只需在本机处理秘密与必要系统权限。遵循
[04 动作逻辑](../04_IPHONE_SHORTCUTS.md) 和
[05 验收矩阵](../05_TEST_AND_AUTOMATION.md) 继续制作、运行和核验。
全部动作探测与产品实测结果应分别追加；若需协议调整先提交可复现证据，由 Windows 统一整合。
