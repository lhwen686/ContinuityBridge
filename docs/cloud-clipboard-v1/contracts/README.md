# v1 可执行契约

P0 只建立规范，不实现服务。HTTP 入口见 [openapi.json](openapi.json)，JSON/头部/WSS 值见 [schemas.json](schemas.json)。[默认 capabilities](default-capabilities.json) 是配置样例，不是当前运行服务返回值。

从仓库根目录，使用已有 PowerShell 执行：

```powershell
pwsh -NoProfile -File docs/cloud-clipboard-v1/contracts/validate.ps1
```

脚本不安装依赖、不联网、不启动服务、不访问剪贴板。它用已有 `Test-Json` 执行 [正反例](schema-cases.json)，检查引用、写接口条件头和端点清单。兼容 PowerShell 不存在时报告 BLOCKED，不能假装执行。它不是完整 OpenAPI 认证器，也不是 Relay 实现测试。

JSON Schema 验证结构、枚举、必需字段和词法限制；严格 UTF-8、JSON wire/decoded 字节数、图片解码、TTL 与时间先后、ETag/epoch/revision 对应关系、available 与当前项对应关系、撤销、幂等原子性仍需 [行为验收](../11_ACCEPTANCE_AND_HANDOFF.md)。结构正例中的 fixture metadata 只验证结构，不能用它证明实际图片合法或字节 hash 相符。

只有 GET `/v1/clipboard` 的 HTTP ETag 与它的 JSON `etag` 相同。MutationReceipt 的 `result.etag` 是原操作结果，`state.etag` 是响应时当前状态；不把整份 receipt 的 HTTP ETag 冒充云状态。iPhone 始终可从 JSON 得到条件，不依赖读取响应头。收到重放结果后先重新 GET，不能把旧 result 当作当前项。

请求格式严格：只接受单个强 If-Match；重复的条件/幂等/模式头、非空 DELETE 正文、重复 JSON 属性或未知字段为 400。缺 If-Match 为 428，缺/非法 Idempotency-Key 为 400；同设备同键不同指纹为 409；新请求条件过期为 412；超限为 413；不支持 MIME/Content-Encoding/cryptoMode 为 415。错误均为 Error JSON，代理生成的非 JSON 错误也必须作为失败保留原剪贴板。

错误优先级：先凭据（401/403），再头部/类型（400/415/428）、受限正文（400/408/413/415），再原子幂等/CAS（409/412）。限流可在认证后任何昂贵操作前返回 429。请求 fingerprint 见 ADR；并发行为不能由 Schema 推导。无论成功还是错误，REST 返回 `Cache-Control: no-store`；401 返回 `WWW-Authenticate: Bearer`，429/503 返回整数秒 `Retry-After`。错误 requestId 为服务端生成的脱敏关联 UUID，不回显 token/正文/生产 hash。

图片 MIME 需与实际编码相符；损坏/非法编码为 400，动画/非 PNG/JPEG 为 415，编码字节/解码像素超限为 413。传输中断不能提交；服务端超时在还能响应时返回 408。GET 内容永不返回成功错误 JSON、重定向或部分内容；未知/旧/过期 itemId 为 410。支持空文本但不支持零字节图片。

ETag 的强比较参考 [RFC 9110](https://www.rfc-editor.org/rfc/rfc9110.html#name-if-match)；本项目额外限制为单个服务器状态 token。JSON Schema 使用 draft 2020-12，HTTP 文档固定 [OpenAPI 3.1.1](https://spec.openapis.org/oas/v3.1.1.html)，不追逐最新版。图片请求的媒体类型无 JSON schema，表示原始二进制，不是 Base64 字符串。
