# P4：接收 P3 与准备真机测试

> 本文保留收到 P3 时的历史检查。后续真实手机结果、完整动作和交付状态以
> [P4 实测报告](p4-real-iphone-results.md) 为准；下文“待录入/NOT RUN”是当时快照。

2026-09-10，收到 P3 时：**P3 交接及 API 认证已通过；P4 手机产品验收尚未通过。**
用户已确认允许新建“CB 测试”，并完成 staging 凭据的加密交接；手机配置待本机录入。
本文是进度记录，不取代最终动作链、图片粘贴及错误矩阵。

## 精确交接

- P3 报告提交：`d321750d84bde7232efca8cd447f9c470a6b68ae`，分支
  `feature/p3-staging-handoff`。本机 fetch 后实际读取报告、release 与 acceptance JSON。
- 已部署源码：`190c75680206eea8ae54dcbe3fbc017587adf6e8`，未改变候选或重新部署。
- P3 计划：`P3-STG-20260910-190c7568-R3`，报告状态 PASS。
  原始部署证据仅在 VPS，P4 没有重新执行 VPS/Windows 验收。
- Git 交接不含真实入口与凭据。后续本机加密交接已成功：仅读取已部署发布记录与一个
  staging token，未改变服务、凭据或 sudo 配置。解密值仅用于本机进程，不进入报告。
- Mac 实际可信 HTTPS 认证及 capabilities 查询 PASS：协议 1，图片上限 20,000,000 字节，
  文本 UTF-8 上限 1,000,000 字节，JSON 上限 8,000,000 字节，保留 1,800 秒。
- 真 iPhone Chrome 访问同一 HTTPS `/v1/capabilities`，地址显示安全，正文返回预期的
  未认证错误。这里只证明手机可信 HTTPS 可达；手机携带 token 的请求尚未验证。
- P3 报告说明公网 `/healthz` 预期 404，不能把它判成产品接口失败。
  原 `run.py`/`inject.py` 对响应 ETag 头名大小写的处理不适用于当前入口；本次不修改它们。

## 真 iPhone 已完成的准备

设备枚举与真实 UI 均重新验证：iPhone18,2，iOS 26.6.2。
现有 Mobile MCP/DeviceKit 可用，修复 runtime 哈希与先前记录一致；8100 仅 loopback。
没有重装工具、读取私人照片/聊天或主动读取/写入剪贴板。

1. 在真实 Shortcuts UI 创建 **CB 测试便笺初始化**。
2. 添加实际动作 **创建备忘录**，选择现有 iCloud“备忘录”文件夹，正文固定为：
   第一行 `CB 测试`，第二行 `仅用于 ContinuityBridge P4 合成 fixture 粘贴验收。`。
3. 在 iPhone 点“播放”，系统切换到新便笺。实际 Notes TextView 与完整两行内容一致。
   该便笺已创建，不需要再次运行初始化以免重复创建。
4. 创建 **CB 配置**，实际动作 **字典**。条目为文本 `BaseURL`、文本 `DeviceToken`、
   数字 `ProtocolVersion`。前两项保持空；在 iPhone 运行空配置骨架，结果为字典，
   版本为数值 `1`（数字编辑器曾显示 `01`，运行结果确认为数值 `1`）。
   没有网络、剪贴板动作或实际凭据。

秘密录入后不能再次采集 CB 配置的截图、UI tree 或运行结果预览。
该配置不进入产品模板导出；纯快捷指令存储不等于独立 Keychain 保险库。
手机已返回 Shortcuts 资料库，等待用户完成配置交接。

创建便笺只证明目标准备完成，空配置运行只证明字典类型；二者均不证明图片可粘贴。
真实证据保存在 ignored `artifacts/p4-iphone-20260910/cb-test-note-created.png`。
原始内容/截图/界面树不进 Git。

## P4 合成 fixture 辅助工具

本阶段新增 [p4_fixture_api.py](p4_fixture_api.py)，核心网络/原有 fixture 使用标准库，位于 P4 lab 范围。
后续补充的 `prepare-visual` / `verify-iphone` 使用已有 Pillow，不自动安装依赖。
它不运行既有全套黑盒、不访问系统剪贴板，不是生产功能或 QA mailbox。
只接收固定合成 fixture ID，不接受任意文件路径、URL 或上传正文；本地文件须匹配固定长度/hash。
后续新增 `visual-transparent` 和 `visual-jpeg`；`prepare` 生成原七份，`prepare-visual` 生成两份可视图。

从 checkout 根目录运行：

```sh
python3 docs/cloud-clipboard-v1/lab/p4_fixture_api.py prepare
python3 docs/cloud-clipboard-v1/lab/p4_fixture_api.py status
python3 docs/cloud-clipboard-v1/lab/p4_fixture_api.py inject --fixture-id transparent
python3 docs/cloud-clipboard-v1/lab/p4_fixture_api.py verify --fixture-id transparent
```

`prepare` 无需凭据，只准备合成素材。它复用 SHA-A 图像生成器，并生成 504,028-byte 长文本，
包含 8,000 行编号、中文、emoji、组合字符、CRLF、tab、引号、反斜线及首尾空白。
七个固定 ID 为 `unicode`、`long-unicode`、`transparent`、`jpeg`、`near-limit`、
`at-limit`、`over-limit`；20MB 默认值来自原候选，状态查询仍回报实际 capabilities。

联网模式须由用户在本机安全提供进程环境 `CB_BASE_URL` 与 `CB_TOKEN_A`；
仅接受可信 HTTPS origin，不跳过证书检查，不自动跟随重定向，不打印地址/token/云正文。
`inject` 会替换测试组的最新项，仅在已交接的 staging 合成测试组运行。
先 GET 完整 etag，再带原样 If-Match 与新 UUID 提交；读取 receipt 后重查状态。
只有云 metadata 匹配固定 fixture，才允许下载正文做字节核验，最后再查状态。
未知当前项不会下载。`over-limit` 注入预期 HTTP 413、固定错误码和云状态不变；
仍须另测 iPhone 自身的超限动作与原剪贴板保留，不能用该 API 负例代替。

HTTP 头名在 P4 工具中按不区分大小写比较，完整带引号 ETag 值保持不变。
报告仅输出 fixture 类型、长度、hash 与核验状态，不输出 itemId、设备信息或服务器正文。
图片经手机重编码后 hash 变化会使此工具拒绝下载；像素比较须在明确 fixture/QA 租约上下文
另行验证，不放宽未知正文下载限制。后续 `verify-iphone` 只允许实测报告中观察到的固定重编码
回执哈希/长度，下载前仍检查 metadata；RGBA 不一致返回 FAIL。

## 当前验证及剩余工作

| 检查 | 结果与边界 |
| --- | --- |
| `prepare` | PASS，exit 0，七份文件与固定长度/SHA-256 一致 |
| 离线混合大小写 `EtAg`/`cAcHe-CoNtRoL` 响应 | PASS，完整带引号 etag 保持；不是公网测试 |
| 未识别 fixture metadata | PASS，内容下载调用未发生 |
| 离线 HTTP 401 | PASS，停止于 HTTP 状态，不返回正文作为内容 |
| staging API 认证 / 完整 ETag / UUID 提交 | PASS，真实状态头与 JSON etag 一致；原样 If-Match 和 Python uuid4 提交成功；不是 iPhone 动作证据 |
| API 长文本上传及回读 | PASS，504,028 字节与固定 SHA-256 完全一致；手机长文本 NOT RUN |
| `sips` 格式/尺寸/alpha 元信息 | PASS，PNG/JPEG 与大 PNG 元信息可读；不算像素一致或 iPhone 解码 |
| 手机“CB 测试”创建 / 空 CB 配置字典执行 | PASS，仅准备步骤 |
| 手机长文本上传、UUID、etag 实际发送、PNG/JPEG/近20MB图片传输粘贴 | NOT RUN |
| GET 后替换、到期、认证错误保留原剪贴板；手机超限错误 | NOT RUN |
| 去秘密产品 .shortcut 导出/重导入 | BLOCKED，产品动作链尚未完成，无可交付模板 |

“CB 发送”“CB 取回”完整动作链仍待制作，不以初始化/配置快捷指令冒充产品。
收到安全交接后继续同一 P4 分支；先真机 HTTPS，再按
[动作与顺序](../04_IPHONE_SHORTCUTS.md) 和 [完整矩阵](../05_TEST_AND_AUTOMATION.md)
执行。API→iPhone 与 Windows 原生 E2E 必须分开报告。

## API 首轮核验修正

首次长文本上传已返回 200，但 P4 辅助脚本把状态 `mimeType` 与裸 `text/plain` 比较，
因而拒绝继续下载。SHA-A 契约规定元数据应为 `text/plain; charset=utf-8`；
本次只修正 P4 辅助脚本的期待值，未改变协议或 Relay。随后对同一 fixture 重新读取
状态、下载并比较完整字节，通过长度和 SHA-256 核验。不能把首轮 oracle 失败记成服务端截断。
