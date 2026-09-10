# P4 真实 iPhone 图片快捷指令实测

2026-09-10，Mac mini USB 实验室。真实手机已完成长文本、PNG/JPEG 和
19,999,999 字节 PNG 的传输与图片粘贴；超限及竞争/认证错误保留剪贴板。
**本阶段图片路径、指定错误保留及真实导出模板的重导入真机回归 PASS。**
这不代表 Windows 原生双向 E2E、脱离 USB 或 20 MB 多轮稳定性已经通过。

## 版本、候选与边界

- staging 已部署 SHA-A：`190c75680206eea8ae54dcbe3fbc017587adf6e8`。
  P3 交接提交 `d321750d84bde7232efca8cd447f9c470a6b68ae`，计划
  `P3-STG-20260910-190c7568-R3`。P4 未重新部署或修改协议。
- 本分支：`feature/p4-iphone-shortcuts`。只交付 lab 文档、fixture 辅助工具和模板；
  不改 Windows 源码，也不把便携 Relay 测试当作 Windows 原生验收。
- 本轮开始的本机 HEAD 为 `768018e2b1f2287024e3b4662240850c335cdef6`。
  测试时工作树含本轮 P4 未提交改动；模板精确内容由安装说明中的两个 SHA-256 定位，
  不是把测试中的 dirty 工作树描述为已提交候选。最终交接以包含本报告的 Git 提交为准。
- 实测 iPhone18,2 / iOS 26.6.2（简体中文）；macOS 26.6.2（25G83）。
  Mobile MCP 1.0.3、mobilecli 1.0.9、DeviceKit 0.0.26；复用 P1 已验证的现有工具。
  没有单独运行 Appium WDA，没有重装工具。8100 监听已核对为 `127.0.0.1`。
- 修复运行时 SHA-256：`c41c82c4bc50668727ee5fabd00505915d83971b7b68ff56be9dd35a3a6b9d72`。
- 用户本机录入 **CB 配置**。录入期间停止屏幕读取，之后没有打开/导出其秘密页面。
  测试仅用 staging 和合成 fixture，目标便笺由用户明确授权为 **CB 测试**。

API 合成数据准备标记为 **API→iPhone**；手机回传标记为 **iPhone→API**。
本次不是 Windows 原生双向 E2E。没有读取私人照片/聊天。操作前用合成哨兵接管测试剪贴板。
曾发现首次人工复制后哈希/大小不符合哨兵；只读了类型/长度/哈希，没有读取正文或上传它，
随后用测试专用本机快捷指令置入确切的 33 字节哨兵，再开始传输测试。

## 真实动作名与变量类型

动作来自当前中文 Shortcuts UI 的搜索/菜单和正式导出记录，不猜 action ID。
可以在 Mac UI 制作、通过现有同步进入 iPhone；所有下面的运行结论来自真 iPhone。

| 当前系统动作 | 实际绑定/类型 |
| --- | --- |
| 获取剪贴板 | 输出可以是 **文本**、**图像**；发送只读取一次，保存为 `OriginalClipboard` |
| 获取类型 | `OriginalClipboard → OriginalType`；实测中文值为 `文本`、`图像` |
| 运行快捷指令、设定变量 | `CB 配置 → Config`；获取 `BaseURL`、`DeviceToken`，构成变量 `Auth` |
| 获取字典值 | 一级键缺失可用于空值判断；直接访问不存在的嵌套键路径会抛系统错误 |
| 文本 | URL 和 Authorization 由文字与魔法变量组合；JSON 正文不手工拼接 |
| 获取URL内容 | POST 菜单真实包含 JSON / 表单 / 文件；File 绑定图片变量，不是文件选择器 |
| 从输入中获取文本 | 取回绑定已验证的 `Downloaded`；文本比较前显式转换，避免泛型变量的比较参数丢失 |
| 从输入中获取图像 | 绑定已验证图片接口的 `Downloaded → ClipboardImage`，不是从剪贴板 URL 抓图 |
| 转换图像 | 真机验证 PNG 重编码；最终 PNG/JPEG 原始对象不强制经过此动作 |
| 获取文件的详细信息 | 属性 **文件大小** / **文件扩展名**；19,999,999-byte 图像显示 `20 MB`，获取数字后为精确 `19999999` |
| 从输入中获取数字 | 用于大小比较，避免对带单位的显示字符串进行数值猜测 |
| 生成哈希 | 算法 **SHA256**；分别核对下载对象、上传对象、原剪贴板 fixture |
| 拆分文本、从列表中获取项目 | 按新行拆十六进制字符，选择 **随机项目**，构成一次请求的 UUID |
| 如果、停止执行此快捷指令 | 不支持/错误/空项/超限/状态变化都在拷贝之前停止 |
| 拷贝至剪贴板 | 取回只绑定 `ClipboardText` 或 `ClipboardImage`；发送没有此动作 |
| 显示内容 | 只显示固定提示或类型/长度/哈希/回执，不把预览作为传输正文 |
| 打开URL | 在 iPhone 的测试入口实际打开 `shortcuts://run-shortcut?name=CB%20%E5%8F%96%E5%9B%9E` |

UUID 搜索未找到可直接采用的独立动作，因此使用已正式导出的原生列表随机动作：
32 个十六进制位置，固定版本位 `4`，variant 从 `8/9/a/b` 选择，按 `8-4-4-4-12`
分组。真机生成的值被服务端接受，回执 `requestId` 匹配；这不是对随机源密码学强度的证明。

## CB 发送：真实顺序和绑定

最终原生导出 218 个动作。重复的设定变量/比较/停止块计入该数量。

1. **获取剪贴板**一次 → `OriginalClipboard`；**获取类型** → `OriginalType`。
   空内容停止；后续所有正文都来自这一份变量。
2. 运行 **CB 配置** → `Config`；取 `BaseURL`、`DeviceToken`；
   **文本** `Bearer [DeviceToken]` → `Auth`。字典键严格区分大小写及尾随空格。
3. GET `[BaseURL]/v1/capabilities` → `Caps`。先取一级 `error`，存在时才取 `code` 并停止。
   检查 `protocolVersions` 包含当前基线的唯一版本 `1`、图片限额为 `20000000`。
   当前模板固定该候选，限额变化时停止并要求更新；每次运行重新读取 capabilities。
4. GET `/v1/clipboard` → `State`，检查错误，保存完整 `etag → BeforeETag`。
   保留双引号与全值，不拆分、不去引号。
5. 用原生文本/拆分/随机项目生成一次 `RequestId`。本次请求使用同一个变量。
6. `OriginalType` 为图像时，读取 `OriginalExtension`：`png` 保留原对象并设
   `PayloadMime=image/png`；`jpg`/`jpeg` 保留原对象并设 `image/jpeg`。
   其他真实图片对象才尝试 **转换图像→PNG**。`Payload` 的编码文件大小不得大于 20,000,000。
   不 resize、不降低 JPEG 质量。仅对已实测 PNG/JPEG 作支持承诺。
7. 文本分支要求 `OriginalType=文本`，`Payload=OriginalClipboard`。
   POST `/v1/items/text` 的请求体选择 **JSON**，字段 `text` 直接绑定 `Payload`。
8. 图像分支 POST `/v1/items/image`，请求体选择 **文件**，文件变量绑定 `Payload`，
   `Content-Type` 绑定 `PayloadMime`。两分支头部都绑定 `Auth`、`BeforeETag`、`RequestId`。
9. 校验回执错误、`requestId`、`result.item.sha256` 与 `PayloadHash`；保存结果 etag/长度。
   再 GET 当前状态，确认仍是该回执 etag，显示发送结果。
   产品模板没有 QA 重复 POST，也没有写剪贴板动作。

文本上限仍由服务器执行；本次上传回执/状态核验不能替代 Windows 端接收后的粘贴验证。

## CB 取回：真实顺序和绑定

最终原生导出 197 个动作。

1. 读取本机配置，认证 GET capabilities，按与发送相同的顺序检查错误和基线。
2. GET `/v1/clipboard → State`；检查错误；保存 `BeforeETag`。
   `item → Item` 为空时提示并停止，完全不接触剪贴板写入。
3. 保存 `ItemId`、`Kind`、`Mime`（键是 `mimeType`）、`ByteLength`、`ExpectedSHA256`、
   `ExpiresAt`；必要字段缺失停止。文本限定 1,000,000 字节及标准文本 MIME；
   图片限定 20,000,000 字节及 PNG/JPEG。
4. GET `/v1/items/[ItemId]/content → Downloaded`，生成 `DownloadedSHA256`。
   与元数据哈希不一致就停止；HTTP 错误正文不得进入图片/文本拷贝动作。
5. 再 GET `/v1/clipboard → AfterState`，检查错误，要求完整 `AfterETag=BeforeETag`。
   一级读取 `AfterItem`，非空后才取 `AfterItemId`，要求仍是原 `ItemId`。
   服务端自然到期或替换引起状态变化时停止。
6. `Kind=image`：**从输入中获取图像**，输入 `Downloaded`，输出 `ClipboardImage`；
   非空后 **拷贝至剪贴板**，输入明确为 `ClipboardImage`。
7. `Kind=text`：**从输入中获取文本**，输入 `Downloaded`，输出 `ClipboardText`；
   **拷贝至剪贴板**，输入明确为 `ClipboardText`。最后显示固定完成提示。

## 真机结果矩阵

| 用例 | 实际结果 |
| --- | --- |
| 手机可信 HTTPS、认证 capabilities/state GET | PASS，实际 JSON 内容正确；头/body 完整 etag 在 API 侧核对一致 |
| 504,028-byte 长文本 API→iPhone | PASS，真实文本剪贴板 SHA-256 `030ef73b…cc1b92` 与 fixture 一致 |
| 同一长文本 iPhone→API JSON | PASS，回执 504028，完整 API 读回逐字节一致；含 8000 行中文、emoji、组合字符、CRLF、引号、反斜线、空白 |
| 完整 etag→If-Match、UUID、同键重放 | PASS，真机 QA 版本重复相同正文/UUID/etag，`replayed=true`、结果 etag 不变；正式版移除重复 POST |
| 86-byte 透明 PNG 取回 | PASS，真实图像剪贴板，原始文件长度/哈希一致 |
| 480×320 可视透明 PNG | PASS，1880 字节，手机图像哈希一致；在 CB 测试实际粘贴并渲染彩色形状/透明区域 |
| 480×320 JPEG 取回及粘贴 | PASS，18413 字节，真实图像剪贴板哈希一致；CB 测试实际出现第二张图片 |
| 正式版 PNG 原始对象 File 上传 | PASS，86 字节，API 逐字节读回与原始 PNG 一致 |
| 正式版 JPEG 原始对象 File 上传 | PASS，18413 字节，API 逐字节读回与原始 JPEG 一致 |
| 19,999,999-byte 合法 PNG API→iPhone | PASS，图像对象、精确字节数、全文件哈希一致，CB 测试实际渲染 2500×1999 彩色条纹图 |
| 同一近20MB PNG iPhone→API | PASS，保留原 PNG 的 QA 版本 File 上传及重放，回执和完整 API 读回逐字节一致 |
| 20,000,001-byte 手机超限 | PASS，固定合成图片已放入真实剪贴板并校验；正常发送在 POST 前拒绝，操作后同大小/同哈希 |
| API 超限 | PASS，实际读到 HTTP 413 / `payload_too_large`，云状态不变；首次客户端 BrokenPipe 不算 PASS，修正读取早到响应后再通过 |
| 云端空项 | PASS，取回前后哨兵均 33 字节、同 SHA-256 |
| GET 后新项替换 | PASS，旧下载结果未通过完整性检查；哨兵保持 |
| 正文已下载后新项替换 | PASS，复制前重查发现 etag 改变，明确提示替换/过期；哨兵保持 |
| 认证错误 | PASS，合成无效 token 用例输出 `unauthorized`，之后哨兵仍为 33 字节、同哈希；最终错误检查结构的负例复测同样通过 |
| GET 后自然到期 | PASS，补测保留真实 1800 秒 TTL：20:37 上传 PNG，21:04 手机 GET 后暂停，21:08 API 确认空项后继续同一次执行，完整性检查拒绝下载结果；21:09 哨兵仍为 33 字节且同哈希 |
| iPhone 本机 shortcuts URL | PASS，测试入口的原生打开URL动作实际启动 CB 取回；完成后固定提示输出权限单独确认 |
| 原生导出/重导入 | PASS；交付路径中的真实 AEA1 文件经系统重导入并同步 iPhone；1880-byte PNG 取回、真实粘贴、从剪贴板 File 上传及 API 完整读回逐字节一致 |
| 正好 20,000,000 字节、重复多轮/网络切换、脱离 USB | NOT RUN；不声称 20 MB 稳定可用 |
| Windows 原生双向 E2E、HEIC/GIF/多图片/动画、64MP 边界 | NOT RUN |

类型判定依赖本机实际输出的中文类型名；其他系统语言和其他 iOS 版本没有验证。
自然到期首轮为 INCONCLUSIVE：19:51 写入、19:53 真机 GET 后暂停、20:22 API 已空；
解锁后等待已退出，剪贴板不再匹配哨兵，未读取或上传正文。该轮不算保留 PASS。
重新置标记后的正式版到期空项取回已验证保留；随后上表第二轮才补齐 GET 跨到期证据。
第二轮只在临近到期时暂停，并以普通界面触摸保持测试提示，没有修改自动锁定设置。

辅助工具六项离线单元检查通过：早到 413、无响应 BrokenPipe 不能通过、401 不输出正文、
未知 metadata 不下载、文本 MIME/完整 etag，以及生成器编码差异不覆盖已有 fixture。
这些是 lab 工具检查，不算真实手机或 Relay/Windows 测试。

原七份 fixture 的实测生成环境为 Python 3.9.6；可视图/像素检查使用已安装的
Python 3.12.14 / Pillow 12.3.0。两个环境均报告 zlib 1.2.12，但大 PNG 的生成字节曾不同；
没有把具体原因推断为服务端变化。准备工具现在先在临时目录校验固定哈希，
不匹配则停止并保留原文件。没有更换协议 fixture 或安装依赖。

## HTTP 与图片转换的真实限制

Shortcuts 的 HTTP 401 会作为错误 JSON 继续流入后续动作，不能依赖系统自动停止。
最终版本先读取一级 `error`，只有存在才访问 `code`。早期直接读 `error.code`
导致正常响应弹“无法评估键路径”，已在真机发现并修正；没有把错误正文拷贝到剪贴板。
下载遇到旧项失效时，哈希检查会拒绝错误正文；若系统网络/类型转换动作自身抛错，
可能只出现系统提示。测试必须随后再读取已知 fixture 的大小/哈希，不能仅根据提示判定保留。

强制转换 PNG 的早期版本把 86-byte 图重编码为 169 字节：alpha 相同，但完全透明
像素的隐藏 RGB 归零，半透明像素出现约 1 个色阶变化。严格 RGBA oracle 的结果为 FAIL，
未称为无损。最终 PNG/JPEG 优先原样上传，已经通过原始文件字节一致验证。
从已认证下载取得的真实图片对象本身可保持原始 PNG/JPEG 哈希。

Mobile MCP 剪贴板接口只有文本能力，本次没有用 WDA/DeviceKit 空 clipboard 返回作结论。
校验通过手机原生 **获取剪贴板→类型/文件大小/获取数字/SHA256→显示内容** 完成。
工具 AX 经常缺失系统权限/显示内容弹层，因此必须辅以新鲜真机截图。

## 验证命令与交接

本机执行的 lab 检查（退出码 0）：

```sh
python3 -m unittest discover -s docs/cloud-clipboard-v1/lab -p test_p4_fixture_api.py -v
python3 docs/cloud-clipboard-v1/lab/p4_fixture_api.py prepare
git diff --check
```

`prepare-visual` 使用已经存在的 Python 3.12.14 / Pillow 12.3.0 环境执行，退出码 0。
API 的 `inject` / `verify` 由本机临时加密交接包装器提供环境变量；包装器、密钥及端点
不属于交付。其他主机只在自己的安全环境中配置 `CB_BASE_URL` / `CB_TOKEN_A`。
`verify-iphone --fixture-id transparent` 的严格 RGBA 检查曾返回非零，
对应前文真实转换失真，不能把这条失败记入通过数。

Windows 整合时 fetch 本短期分支的最终提交，保留 SHA-A 的协议/部署候选映射，
采用本目录真实模板和安装说明；P5/P6 仍需独立完成 Windows 原生剪贴板证据。
本次没有采集足够多轮的端到端延迟样本，p50/p95 未测，不提供性能 SLA。

## 导出、证据与测试租约

模板见 [安装说明及文件哈希](../shortcuts/README.md)。在真实 UI 创建/探测动作后，
从原生导出记录取得实际动作结构，使用系统 `shortcuts sign` 和系统导入进行候选构建。
最终再用 **文件→导出→任何人** 得到 AEA1 模板。Apple `aea` 校验签名、`aa` 解包用于审计；
导出动作与实测动作一致，取回只增加了系统编辑器 UUID。没有用 Mac `shortcuts run` 证明手机行为。

已在解密后的导出结构中对实际 staging token/私有 host 作精确匹配检查，结果均不存在；
也没有设备 UDID、证书 email 或内嵌 CB 配置。秘密只在本机配置/临时加密交接中。
未发现独立 Keychain 辅助工具；纯快捷指令 token 的可查看、同步和分享边界见安装说明。

原始证据只保留于 ignored `artifacts/p4-iphone-20260910/`，不进 Git。关键证据文件：

- `iphone-long-retrieve-oracle.png`、`iphone-long-send-uuid-replay.png`。
- `iphone-visual-transparent-oracle.png`、`iphone-visual-transparent-pasted.png`。
- `iphone-visual-jpeg-oracle.png`、`iphone-visual-jpeg-pasted.png`。
- `iphone-final-transparent-send.png`、`iphone-final-jpeg-send.png`。
- `iphone-near20mb-retrieve-oracle.png`、`iphone-near20mb-pasted.png`、`iphone-near20mb-file-send.png`。
- `iphone-over20mb-before.png`、`iphone-over20mb-rejected.png`、`iphone-over20mb-preserved.png`。
- `iphone-auth-error-code-final.png`、`iphone-auth-error-final-preserved.png`。
- `iphone-race-preserved.png`、`iphone-after-download-race-rejected.png`、`iphone-after-download-race-preserved.png`。
- `iphone-final-url-retrieve-success.png`、`iphone-expiry-paused-after-get.png`。
- `iphone-export-auth-error.png`、`iphone-export-auth-preserved.png`。
- `iphone-export-expired-empty.png`、`iphone-export-expired-empty-preserved.png`。
- `iphone-export-retrieve-oracle.png`、`iphone-export-png-pasted.png`、`iphone-export-send-receipt.png`。
- `api-before-natural-expiry.json`、`api-natural-expiry-status.json`：自然到期前后只读 API 检查。
- `api-before-expiry-repeat.json`、`api-expiry-repeat-status.json`：补测到期前后的 API 检查。
- `iphone-expiry-repeat-baseline.png`、`iphone-expiry-repeat-paused.png`、
  `iphone-expiry-repeat-rejected.png`、`iphone-expiry-repeat-preserved.png`。

**CB 测试校验**只有本地读取、类型/长度/hash 和显示，没有 HTTP 或写剪贴板动作。
置标记/超限置图只写固定合成 fixture。暂停与无效凭据版本只用于本次 QA 租约，
不进入生产模板、不建立自动化触发器、不成为自动外传剪贴板入口。
今后运行 oracle 必须重新建立 QA fixture 租约，先置入明确合成内容。
