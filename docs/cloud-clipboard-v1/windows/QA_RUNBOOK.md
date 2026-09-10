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
3. 在已公告的测试窗口勾选合成 fixture 授权并点“开始测试”。这一步会先放入固定哨兵，不读取/备份旧剪贴板。请勿在测试中复制私人内容。
4. READY 只证明 runner 已绑定单会话并可领取动作。实际复制/粘贴和手机验证仍按 P6 场景执行。窗口可立即停止；关闭活动窗口先取消运行，再关闭。租约到期、未知内容、错误 SHA/角色/动作、超时会停止。
5. 结束会话保留最后合成剪贴板，不上传旧个人数据，不承诺还原私有 OLE 格式。单实例互斥阻止同一桌面启动第二个 runner。

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

fixture ID 与构造方式在 `src/ContinuityBridge.Qa.Protocol/FixtureCatalog.cs`。`sentinel-v1`、`unicode-v1`、`long-text-v1` 为固定文本；`alpha-png-v1` 为不对称透明图；`jpeg-v1` 为既有合成 JPEG；`png-20000000-v1`、`png-20000001-v1` 为真实扫描行和合法 ancillary chunk 的十进制边界 PNG；`bitmap-v1` 为相同图的白底 DIB；`file-drop-v1` 为仅本机新建的固定测试文件，产品必须忽略。

大 PNG 用固定 RFC1950/1951 stored blocks 构造，不依赖各主机 zlib 版本；不会给无效文件尾补字节。fixture 运输字节可以在本机校验；系统重编码时必须核对解码像素、宽高、alpha/方向，不能只看截图。纯手机 oracle 沿用 P4 的测试租约边界。

固定清单的长度和 SHA-256 见同目录 `fixtures.json`，随 TestAgent 包一起交付；单元测试逐项对照实际生成字节。清单中的 hash 只对应已提交合成 fixture，不是剪贴板遥测。
