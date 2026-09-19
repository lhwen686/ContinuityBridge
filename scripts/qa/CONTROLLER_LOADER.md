# Mac controller 文件加载器

`load_controller.py` 使用 Python 3.9+ 标准库。`validate` 只在本机读取、解析和校验文件，构造环境映射后退出；不启动 controller、不联网、不绑定 session、不启用 QA。不得用 `controller.py Status` 代替离线校验：它会提交命令。

## 输入文件

VPS 原生 `controller.json` 必须且只能包含下列四个字符串字段，大小写敏感，拒绝重复键及未知字段：

| 字段 | 校验及用途 |
| --- | --- |
| `runId` | 非零、小写、带连字符 UUID；匹配本机预期 run；映射到 `CB_QA_RUN_ID` |
| `candidateSha` | 40 位小写十六进制；匹配本机预期候选；映射到 `CB_QA_CANDIDATE_SHA` |
| `expiresAt` | ISO8601 UTC，`Z` 或 `+00:00`，最多七位小数秒；未过期，剩余时间不超过 45 分钟；不输出为环境变量 |
| `token` | 64 位小写十六进制原始 controller token；仅映射到子进程的 `CB_QA_CONTROLLER_TOKEN` |

`controller-local.json` 是独立的本机配置，也使用严格字段集合：

| 字段 | 用途 |
| --- | --- |
| `baseUrl` | 已核验的 QA HTTPS origin，无路径（含尾斜杠）、认证信息、query 或 fragment；映射到 `CB_QA_BASE_URL` |
| `runId` | 从本批可信交付回执得到的预期 run；不能只复制未核对的收件值来证明匹配 |
| `candidateSha` | Windows 冻结并完成主机对齐的预期完整候选 SHA |
| `controllerSessionId` | Mac 为本批一次生成并保存的非零小写 UUID；映射到 `CB_QA_CONTROLLER_SESSION_ID`，同一 run 始终复用此文件和值 |

正式文件不添加 role、origin 或 session 字段。四字段结构本身不能分辨 runner/controller 身份；controller 身份来源依赖经核验的 VPS 交付路径和收据，不能把格式通过称为服务端角色认证通过。

两个文件均需属于当前用户，权限严格为 `0600`，直接父目录为当前用户所有且 `0700`。收件时另核实不存在授予其他用户权限的扩展 ACL。加载器拒绝符号链接（包括父路径）、硬链接、非普通文件、超出 16 KiB 的文件、非 UTF-8 和不合 schema 的 JSON。不执行 source/eval，不生成 token，也不复用旧 P4 解密工具。

## 离线准备

实际文件路径留在各主机本地回执，不提交仓库。用两个受限文件的实际路径替换下列非秘密占位符：

```sh
python3 scripts/qa/load_controller.py --controller-file CONTROLLER_FILE --local-config LOCAL_CONFIG validate
python3 -B -m unittest discover -s tests/qa -p 'test_*.py' -v
```

`controller.DUMMY.json` 可以包含四个字面值 `DUMMY`，预期拒绝。正向测试使用固定合成 UUID/SHA/token 和假时钟；合成值不得作为真实凭据。`.invalid` origin 可供离线校验，`run` 会拒绝它。校验成功也不证明 HTTPS 可用、VPS 租约已启用、角色正确或真实设备通过。

## 正式执行入口（本轮未执行）

仅当正式交付、同候选对齐、VPS ACTIVE、Windows 真实接管 ACTIVE 和用户测试窗口都已成立后，才调用 `run`。加载器使用自己的相邻 `controller.py`，要求当前 Git HEAD 匹配预期候选且这两个脚本没有相对 HEAD 的改动；Windows 必须先集成加载器补丁，再冻结完整候选。

```sh
python3 scripts/qa/load_controller.py --controller-file CONTROLLER_FILE --local-config LOCAL_CONFIG run Status
python3 scripts/qa/load_controller.py --controller-file CONTROLLER_FILE --local-config LOCAL_CONFIG run SetFixtureClipboard --fixture-id unicode-v1
```

动作和 fixture 仅接受现有 controller 固定清单。token 不进入 argv、父进程环境、shell 配置或日志；不把原始子进程输出/错误透传，结果只允许现有固定字段和值。错误只输出固定摘要。子进程使用隔离 Python 模式，等待时限不超过本地剩余租约或 240 秒，沿用现有 controller 的 HTTPS 校验和协议；不更改网络设置。

`expiresAt` 的本地检查依赖系统时钟，无法证明时钟准确，也无法代替 VPS 权威租约检查。离线准备不访问 iPhone；手机当前业务身份必须单独核实。
