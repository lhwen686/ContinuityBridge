# W1 Windows staging 凭据导入

仅 TestAgent 提供“导入 staging 配置（不开始）”及“清除导入”。没有 `--config` 参数。导入只读取用户选择的两个本地文件，不连接 QA/Relay，不创建 runner session、不访问剪贴板、不启动同步、不勾选授权。日常产品配置、凭据库及自启均不变。成功导入后用户仍须人工勾选隔离 staging 同步和测试窗口授权，点击开始才走原有在线校验与安全接管流程。

本说明定义**下一次正式批准后**的交付格式；W1 不签发租约、不查找未生成的 token。QA 凭据与 Relay 设备凭据来自 VPS，各有用途。离线检查不证明 token 有效、角色真实或设备映射正确，也无法从 64 位字符串识别其是否误用了 token hash；正式开始仍依赖 QA 身份/角色/run/session/SHA/租约验证和 Relay 业务鉴权。

## 两个文件

按顺序选择 `runner.json` 和 `windows-relay.json`。每个文件必须是 UTF-8 JSON 对象、不带 BOM，最多 8192 字节。字段大小写固定、全部必需且均为字符串；拒绝重复/未知字段、注释、嵌套对象和非字符串值。只允许本机固定磁盘绝对路径；拒绝 UNC、备用数据流及重解析点。

`runner.json` 由本候选 QA sidecar 的既有 `--provision` 写出，新增 `role`，其余字段不变；`controller.json` 格式未改。不要把 controller 文件改名当作 runner 文件。

| 字段 | 要求 |
| --- | --- |
| `role` | `runner` |
| `runId` | 本次非空 UUID，标准 D 格式 |
| `candidateSha` | 与 TestAgent 内嵌候选完全一致的 40 位小写 SHA |
| `expiresAt` | 本次 UTC ISO 8601 截止时间，剩余时间大于 0 且不超过 45 分钟 |
| `token` | 本次 runner 明文 token；64 位小写十六进制，不能填 hash |

`windows-relay.json` 由 VPS 在批准签发时，从已核验的 Windows 测试设备映射及本次 runner 元数据生成；本机不得用随机值替代真实设备凭据。仅投递给 Windows，文件 0600、目录 0700；生成和传输不能把值放到命令行或输出。

| 字段 | 要求 |
| --- | --- |
| `environment` | `staging` |
| `recipient` | `windows` |
| `runId` / `candidateSha` / `expiresAt` | 与同批 runner 文件完全对应，时间按同一 UTC 时刻比较 |
| `qaOrigin` | 已批准 QA HTTPS origin（根路径，无用户信息、查询或 fragment） |
| `relayOrigin` | 已批准 staging Relay HTTPS origin，同上 |
| `deviceToken` | 已核验的 Windows 测试设备明文 token；64 位小写十六进制，不能与 runner token 相同 |

Windows 实际接收目录由本机无秘密回执给出。文件及直接父目录的所有者必须是当前用户，允许项仅当前用户和 SYSTEM；当前用户须有读取权限。目录取消宽泛权限继承，子文件继承受限权限。工具核验实际打开文件的 ACL 后读取，不用 chmod 代替 Windows ACL，也不自动修复不合格权限。不要放在 Git、共享目录或云同步目录。

## 可见窗口行为

1. 从最终冻结并核验的包打开 TestAgent；旧安全候选包没有此导入功能，不能重新标记为新 SHA。W1 本身不交付最终冻结包。
2. 点击导入，两个文件都核验成功后才填充；秘密字段遮蔽、禁用快捷键，整组导入字段只读。不会把文件内容、路径、token/hash 或 JSON 解析错误输出到控制台/状态文字，也不通过系统剪贴板传值。
3. 导入失败清空整组配置和授权，不保留半次结果。点击“清除导入”可清空内存字段并恢复手动输入。
4. 开始前再次检查导入候选和本机有效期。正式开始后的服务器验证仍是权限依据，导入不替代在线验证。开始/结束清空秘密输入；没有保存或自动续租。
5. 导入只在未运行、未进入清理故障隔离时可用。运行期间不能切换配置，不改变现有产品互斥、剪贴板快照、排空与条件恢复策略。

内存中的托管字符串及 GUI 内部副本无法承诺物理擦除；原始读取字节在完成后清零，不写个人备份或日志。角色文件按后续明确批准的清理范围处理。

## W1 验证与阶段边界

`StagingImportTests` 使用生成的假 token、`.invalid` origin、受限临时文件及不显示的窗体。覆盖字段装载、遮蔽、授权/同步默认关闭、错角色/候选/跨批次/过期/超时长/非 UTC/非 HTTPS、未知及重复字段、过大输入、ACL 和路径拒绝、失败清空、无控制台输出。它不打开真实 TestAgent 测试窗口，不创建 FixtureDesktop，不连接外部服务，不读写真实剪贴板。

已有 TestAgent 安全回归仍覆盖先验证 QA 再备份/sentinel、排空后恢复、外部复制保护及恢复禁止上传；假数据测试不是原生接管或三端 E2E PASS。Mac 回执到达后 W2 才集成、构建并冻结最终候选；正式凭据及 W3 需要同候选各端准备结果、批准和用户测试窗口。
