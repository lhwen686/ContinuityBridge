# 02 v1 HTTP 契约与状态规则

本文件是实施约束，不是已经存在的 API。P0 已提供 [OpenAPI](contracts/openapi.json)、[JSON Schema](contracts/schemas.json)、[ADR](09_ADR_V1_BASELINE.md) 和 [离线校验](contracts/README.md)；P2 才用真实服务和黑盒测试验证。下文保留设计说明；具体字段、默认值和歧义以本次契约与 ADR 的细化为准。内部类名可调整，外部行为不能在不同主机各自发明。

## 1. 通用约定

所有 `/v1/*` 接口需要 `Authorization: Bearer <device-token>`。一个 v1 部署服务一个设备组，组内只有一个当前项。公网只提供可信 HTTPS/WSS。除健康探针外不匿名暴露状态；认证先于读取大请求体。认证/日志/代理中不得输出真实凭据。

服务端产生 `serverEpoch`（实例 UUID）、`revision`（十进制字符串，避免 JS 精度问题）、`itemId`（不透明唯一 ID）、`committedAt` / `expiresAt`（UTC ISO8601），并产生可直接放入 HTTP If-Match 的完整强 ETag 字符串。不要让 iPhone 必须读取 HTTP 响应头才能得到状态：响应 JSON 同时包含 `etag`。

修改请求必须携带该次操作基于的 `If-Match` 和 UUID `Idempotency-Key`。初次先 GET 状态，拿到即使为空也存在的 ETag。服务端在提交时比较期望状态；同时到达的内容不会靠客户端时间戳排序。为了防止过期改变状态产生混乱，过期/清空也应成为可观察的状态变化，ETag 随之更新。

v1 只支持 `cryptoMode=none`。图片是原始二进制请求体，不使用 Base64 JSON，不使用任意文件名或磁盘路径。

## 2. 端点

| 方法/路径 | 输入与结果 |
|---|---|
| `GET /v1/capabilities` | 版本、种类、mime、limits、retentionSeconds、cryptoModes、通知协议、运行存储语义 |
| `GET /v1/identity` | P5 兼容新增：认证 token 推导的 `{ "deviceId": "…" }`，用于 Windows 跳过本设备旧云项；无 token 回显 |
| `GET /v1/clipboard` | 当前元数据；为空仍返回 200 JSON，`item:null` 和当前 etag |
| `POST /v1/items/text` | JSON `{ "text": "完整文本" }`；返回提交元数据 |
| `POST /v1/items/image` | `image/png` 或 `image/jpeg` 原始 File/二进制正文；返回提交元数据 |
| `GET /v1/items/{itemId}/content` | 仅当前、未过期项的完整正文，Content-Type 准确；text 为 UTF-8 |
| `GET /v1/events` | WebSocket Upgrade；认证后发送小型状态提示/心跳，不发送正文 |
| `DELETE /v1/clipboard` | 条件清空当前云项，不清除设备本地剪贴板 |
| `GET /healthz` | 最小存活结果，不泄漏设备/域名/密钥/剪贴板；是否公开由部署配置决定 |

示意 capabilities（数值是本方案默认，可配置）：

```json
{
  "protocolVersions": [1],
  "kinds": ["text", "image"],
  "imageMimeTypes": ["image/png", "image/jpeg"],
  "limits": {
    "maxTextUtf8Bytes": 1000000,
    "maxTextJsonBytes": 8000000,
    "maxImageBytes": 20000000,
    "maxDecodedPixels": 64000000
  },
  "retentionSeconds": 1800,
  "idempotency": {"retentionSeconds": 1800, "maxEntries": 512, "scope": "device", "storesBodies": false},
  "cryptoModes": ["none"],
  "notifications": ["websocket-v1", "poll"],
  "maxEventBytes": 4096,
  "storage": {"mode": "memory", "survivesRestart": false}
}
```

示意状态对象（这里的时间只作格式例子，不代表已上传内容）：

```json
{
  "protocolVersion": 1,
  "serverEpoch": "<server-generated-uuid>",
  "revision": "42",
  "etag": "\"<opaque-state-token>\"",
  "item": {
    "itemId": "<server-generated-id>",
    "kind": "image",
    "mimeType": "image/png",
    "byteLength": 123456,
    "sha256": "<transport-content-hash>",
    "sourceDeviceId": "<server-resolved-device-id>",
    "cryptoMode": "none",
    "committedAt": "2026-09-10T00:00:00Z",
    "expiresAt": "2026-09-10T00:30:00Z"
  }
}
```

capabilities 发生变化时客户端刷新缓存；不能把 20 MB/1800 秒散落硬编码在多个快捷指令分支。P4 必须验证 Shortcuts 能将返回的完整 ETag 原样作为 If-Match 请求头。

## 3. 文本/图片和错误

文本 decoded UTF-8 上限与 JSON 请求体上限区分：示例最大 1,000,000 UTF-8 字节，可允许最高 8,000,000 字节 JSON 封装，以容纳转义开销；配置要校验相互一致。拒绝非法编码、NUL 和超限，不允许使用部分正文作为成功结果。v1 不支持压缩请求 Content-Encoding，避免编码前后限制含义不同；正文路径只由受限 itemId 解析，不接受客户端提供的下载 URL 或文件系统路径。图片读取过程流式计数，不能只信 Content-Length；分块上传同样强制 20,000,000 字节上限。允许边界值，超过一个字节即失败。

图片 MIME 与签名字节匹配，尺寸和结构解析受限；不在 Relay 里无界解码大图。客户端必须在写剪贴板前完成受限解码。损坏/不支持图片返回错误或客户端明确拒绝，不能用扩展名判断成功。

错误 JSON 为固定简短结构，例如 `{"error":{"code":"payload_too_large","message":"...","requestId":"..."}}`，不回显正文/token。使用 400 格式错误，401 无效凭据，403 无权限，409 幂等键冲突，410 旧项过期/被替换，412 状态竞争，413 太大，415 类型不支持，428 缺少条件提交，429 限流。临时服务故障使用适当 5xx。用户看到可行动的错误，不能把错误 JSON/HTML 复制到剪贴板。

Shortcuts 对 HTTP 非成功响应的具体处理能力由 P4 在真机验证。必要时使用操作后元数据核验和清晰的系统错误提示；不能为了“好捕获”把所有错误改成 200 或把错误正文当内容。

## 4. 幂等与 CAS 顺序

认证 → 校验请求/受限读取 → 查同设备 Idempotency-Key → 若不是已完成请求则比较 If-Match → 原子替换并递增状态 → 发布提示。

同设备幂等键相同且请求指纹相同（方法/路径、类型、模式、原 If-Match、逻辑正文，详见 ADR），返回原提交的**元数据**，标记 `replayed:true`；绝不重复写入、续期或复活旧正文。同键不同指纹返回 409。`MutationReceipt.result` 保留原提交元数据，`state` 是响应快照时当前状态，`available:false` 明确原项已不可用；不能把原 result 伪装为当前项。所有成功写入/清空均返回 200 receipt，错误保留各自非 2xx 状态。

只缓存 requestId、请求指纹、结果元数据及时间，不缓存完整 ClipboardSnapshot、text、byte[]。幂等记录设置有界容量和 TTL（默认不长于 30 分钟），不成为历史正文。过期缓存被淘汰后的旧请求仍须通过原 If-Match，旧 ETag 或旧 epoch 保证不能悄悄覆盖新状态。

初次网络发送的丢包重试必须复用同一 Idempotency-Key、原正文和原条件。已收到 412 的新鲜当前复制可重新 GET 后最多重试一次，使用**新请求键**表示重新提交；若仍冲突则提示，不无限夺回最新槽位。断线旧候选遇到冲突不进行这种重新抢占。

## 5. 下载与写剪贴板的两段核验

GET 状态得到 itemIdA；只下载 `/items/A/content`，不在下一步下载“此刻最新的另一个 B”却继续套用 A 元数据。服务端验证 A 仍可访问；正文长度/hash 与 A 一致，否则丢弃。客户端下载后在本地提交前再次确认云状态未替换、未过期以及本地 generation 未变。失败时保留现有本地剪贴板，不能清空后再下载。

一旦下载已经结束，服务端之后过期无法取消用户获得的数据；文案不能称为“30 分钟后所有设备删除”。对正在进行的旧传输设置有界时长与取消处理，测并发替换/到期场景。

## 6. WebSocket

连接使用标准 WSS、Authorization 请求头；鉴权后的 sourceDeviceId 由服务端决定。事件例如 `{"type":"changed","serverEpoch":"...","revision":"43"}`。连接首包可提示当前状态；事件丢失、重复、乱序均通过 REST 当前状态收敛。不要依赖每个客户端永久接收历史消息。

服务器重启、新 epoch、ping/close、401 撤销、代理断开均有测试。单事件大小上限 4 KiB，连接数受限。客户端收到事件不立刻盲写；遵循本地 generation 和来源抑制规则。

## 7. 契约演进

以能力协商支持 TTL/图片大小调整；不兼容变化升级协议版本。未来 E2EE 通过版本化 opaque envelope 接口承载密文；不要简单在目前 text JSON 外塞字段就声称实现端到端安全。未来服务器只处理外层大小/路由/时间，内层敏感 metadata 由设备解密。v1 未实现加密时必须拒绝加密内容，而不是默认为明文。
