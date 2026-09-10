# 受限 TestAgent / staging QA

产品 App 不引用 QA。`ContinuityBridge.TestAgent.exe` 是独立的可见窗口，不自动登录启动，不在产品运行时常开。只在明确的测试租约内运行。初始界面不读取剪贴板、不联网。

P6 前置：Windows、Mac 和 staging 均使用交付的完整 SHA-B；P3 已按新计划升级 Relay 并部署独立 QA sidecar；两个 HTTPS origin 和凭据均在各端本机提供。P5 本地模拟 TLS、编译和 HTTP 测试不代表这些 staging 前置已经完成。

## P3 准备

1. 在自己的干净 checkout 检出完整 SHA-B。分别用 `deploy/relay/Dockerfile` 和 `deploy/qa-staging/Dockerfile` 构建，传 `--build-arg CANDIDATE_SHA=<完整SHA>`；按 P3 核实 Linux 镜像 digest、资源与配置。生产构建上下文和镜像无 QA 源/程序集。
2. QA 单独 Compose 项目和 loopback `43118`，不改变 Relay `43117`。模板在 `deploy/qa-staging/compose.yaml.template`。只在 staging 的可信 HTTPS 入口添加 `/qa/v1/*` 代理到 QA；不要加入生产代理配置。限制请求体 4096 字节、请求超时 10 秒，禁止 access/error 日志记录头和正文。实际证书、路径、地址不在本仓库。
3. 使用已构建 sidecar 的 `--provision <本机受限目录>` 创建一次最多 45 分钟的租约。Linux 输出目录 0700，文件 0600；已存在文件不会覆盖。`lease.json` 仅含两种 token 的 hash；`runner.json`、`controller.json` 含各自短期明文 token，只在所属端安全录入。
4. 非 root sidecar 只需读取 hashed `lease.json`。按 P3 的服务 UID/组提供最小读取权限；不要把 controller/runner 明文文件挂入容器。`Qa__LeaseFile` 指向该固定文件。进程只接受与自身编译 SHA 相同的租约。换租约重建此临时 sidecar，不能后台无限续期。
5. 实际部署需新的具体计划 ID、SHA/镜像/代理/配置差异和回滚目标。P5 没有操作 VPS、域名、证书、防火墙或线上服务。停止 QA 后撤下本临时服务和 staging QA 路由，保留脱敏证据；不停止产品 Relay。

## Windows 启动

1. 当前登录桌面双击 `ContinuityBridge.TestAgent.exe`。核对窗口的完整候选 SHA。
2. 本机填写 QA HTTPS origin、runId 和 **runner token**；token 遮蔽显示、仅留内存，不截图配置。
3. **先自行保管重要剪贴板内容并确认测试窗口，手动退出日常产品 App**。TestAgent 使用产品的会话单实例互斥：产品仍开着（即使暂停）就拒绝启用；测试接管和清理期间产品也不能启动。不会代你关闭任何程序。
4. P6 双向场景勾选“启用本次隔离的 staging 同步”，本机录入 staging Relay HTTPS origin 与 Windows 测试 device token。它复用产品同步引擎，但只能传输已提交的合成 fixture，不读取/修改日常产品设置、凭据或自启项。未勾选只能运行 QA fixture 局部检查，不能满足 P6 双向前置。凭据只在本进程内存，退出后重新输入。
5. 在已公告窗口勾选授权并点“开始测试”。先通过真实 HTTPS `/qa/v1/poll`，严格核验 `service=ContinuityBridge.Qa.Sidecar`、`role=runner`、绑定 `sessionId`、runId、完整 SHA、活动状态及不超过45分钟的有效租约。缺字段、错误角色/候选、过期/不可达时不读写剪贴板；不能用本地配置自证租约。只有核验通过才准备 fixture、建立完整内存快照、在锁内核对序号并写 sentinel，随后才启动本次 staging 同步。
6. 窗口显示测试控制启用、runId 和租约截止；QA `runnerReady` 仅表示认证 session 已绑定，不证明剪贴板接管/两端验收。初始窗口、构建成功或本地修复均不是 P6-W READY。实际 staging 同 SHA、租约/配置隔离和已批准桌面测试前置都成立后，P6-W 才能报告准备结果。
7. “立即停止”、关闭、EndRun、租约到期、QA/同步网络错误、未知 fixture 和可处理异常走同一清理路径：取消并等待本次命令、准备任务及 staging 同步；仅仍是本次测试拥有的原生窗口和预期序号时恢复。任何新的外部复制（即使文字碰巧与 fixture 相同）都保留并跳过恢复；测试中不要手动复制其他内容。支持的远端 fixture 写入也由同一受保护 STA 执行。
8. 清理完成后才释放产品互斥。恢复项先发布禁止历史/云上传 DWORD 和本地 origin，再放回支持的正文；产品捕获路径在读取正文前拒绝这类项。恢复不进入 QA、同步事件或上行队列。不自动重开产品；之后由用户按原配置手动启动，产品不会上传启动前的旧剪贴板。单实例互斥继续阻止同桌面第二个 runner。

## 内存快照、竞争与恢复限制

快照是独立的有界 byte 数组，不是原生句柄、惰性 IDataObject 或文件。只接受完整格式集合中的 CF_UNICODETEXT/CF_TEXT/CF_OEMTEXT/CF_LOCALE、PNG/image/png、JFIF/image/jpeg、CF_DIB/CF_DIBV5，以及已知本条隐私/origin 元数据；CF_BITMAP 通过 Windows 转成独立 DIB（转换改变序号或无法取到时拒绝）。原有编码字节保存在本机内存；不解码私人图片、不扩展到文件传输或任意 OLE。

文本各格式上限2,000,002字节，编码图片各20,000,000字节，DIB各64,001,024字节，完整快照最多96,000,000字节和16种格式。元数据另有小限额；总预算仍适用。恢复前分配新 HGLOBAL，因此峰值还包含发布副本、fixture 目录数据及运行时开销。完成/失败后释放并清零托管备份；不承诺物理取证级擦除或控制 OS swap/dump。

枚举到文件列表、HTML/RTF、自定义/OLE、调色板等未知格式，即使同时有文本/图片，也整项拒绝并保持原剪贴板。无法读出、竞争、序号不可用/变化、资源或快照时限超出同样不覆盖。请自行保管原内容，并准备纯文本或合成图片后重试；没有“忽略其他格式直接继续”的按钮。

没有网络等待发生在 OpenClipboard 内。备份后每次写入、恢复都在锁内重新核查预期序号和已取得的所有权；检测到外部复制为粘性状态，不能因内容相同而恢复资格。真实 Windows 合成格式/延迟渲染、原生句柄错误仍需已授权的 L2 验证。

快照预算3秒，单动作90秒，QA请求10秒，租约最多45分钟；清理排空预算5秒，恢复预算2秒，线程释放另限5秒。原生 GetClipboardData 等同步调用不能由 .NET 强制中断：超时取消后跳过恢复，禁用再次启用；保持产品互斥直至原任务确实退出或进程结束。窗口保持响应并可关闭，不能把超时称为已恢复。正常退出也只是尽力恢复；强杀、断电、崩溃或不可恢复原生错误没有内存恢复保证。原生多格式发布不是系统事务，部分恢复失败明确报告，不伪造 PASS。

不修改系统全局剪贴板设置，不关闭浏览器/插件/密码管理器。恢复的 per-item 隐私标记由本产品执行；不声称能管制不遵守这些标记的第三方剪贴板管理器。P5 历史 `-DesktopFixtures` 场景没有使用这条安全恢复流程，不能拿来验证本次修复；本次默认回归只用 fake/合成数据及本机临时 HTTPS。

## Mac controller

唯一场景控制端在本机环境配置 `CB_QA_BASE_URL`、`CB_QA_RUN_ID`、`CB_QA_CANDIDATE_SHA`、`CB_QA_CONTROLLER_SESSION_ID`（本次固定 UUID）和 `CB_QA_CONTROLLER_TOKEN`。不得把 token 放命令行、Git、截图或输出。`scripts/qa/controller.py` 只用 Python 标准库，正常 HTTPS 验证，拒绝重定向。

```sh
python3 scripts/qa/controller.py Status
python3 scripts/qa/controller.py SetFixtureClipboard --fixture-id unicode-v1
python3 scripts/qa/controller.py VerifyClipboard --fixture-id unicode-v1
python3 scripts/qa/controller.py EndRun
```

只接受 `SetFixtureClipboard`、`VerifyClipboard`、`Status`、`EndRun`。输入没有可执行命令、URL、路径或正文；JSON 未知字段被拒绝。正文协议与 QA 状态隔离。结果只允许 PASS/MISMATCH/BLOCKED/STOPPED，不回传剪贴板正文、hash 或任意错误文本。

每个 run 绑定一个 controller session 和一个 runner session；最多一条未完成动作、总计最多 64 个动作，单动作服务器截止 120 秒，runner 本地操作截止 90 秒。重复 commandId 必须是同一请求，runner 不重复执行已完成动作；确认回包丢失可重试。超限后开始新租约，不能静默扩容。HTTP 状态 401/403/409/410 分别提示认证、角色、会话/候选冲突及租约到期。

当前 runner 遇到网络错误立即停止并清理，不自动重连、续租或重新接管。重新执行必须重新人工启用。QA POST 返回有 `no-store` 的真实 JSON；`service`、`role`、`sessionId` 来自服务器已认证的 mailbox 状态。此次修复后的 runner 拒绝旧 sidecar 空响应/缺身份响应，因此 P3 必须重建同 SHA 的独立 QA sidecar，不能仅替换 Windows EXE。

fixture ID 与构造方式在 `src/ContinuityBridge.Qa.Protocol/FixtureCatalog.cs`。`sentinel-v1`、`unicode-v1`、`long-text-v1` 为固定文本；`alpha-png-v1` 为不对称透明图；`jpeg-v1` 为既有合成 JPEG；`png-20000000-v1`、`png-20000001-v1` 为真实扫描行和合法 ancillary chunk 的十进制边界 PNG；`bitmap-v1` 为相同图的白底 DIB；`file-drop-v1` 为仅本机新建的固定测试文件，产品必须忽略。

大 PNG 用固定 RFC1950/1951 stored blocks 构造，不依赖各主机 zlib 版本；不会给无效文件尾补字节。fixture 运输字节可以在本机校验；系统重编码时必须核对解码像素、宽高、alpha/方向，不能只看截图。纯手机 oracle 沿用 P4 的测试租约边界。

固定清单的长度和 SHA-256 见同目录 `fixtures.json`，随 TestAgent 包一起交付；单元测试逐项对照实际生成字节。清单中的 hash 只对应已提交合成 fixture，不是剪贴板遥测。
