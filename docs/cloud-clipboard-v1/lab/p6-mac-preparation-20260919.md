# P6 Mac 冻结前准备：2026-09-19 增量回执

本次结果是部分准备完成，**Mac 尚未 PREPARED，不能据此冻结完整候选或启动正式 QA**。安全代码基线为 `4d2b94777d19a344ed477abe8f35cf6f6c5a4d7a`；本分支包含 controller 加载器、离线测试及已批准的发送模板两处类型条件修补，不改协议。手机身份已独立核验 MATCH；产品安装一致性仍阻塞。

## M1 现场

| 检查项 | 结果及证据边界 |
| --- | --- |
| macOS | 27.0，build 26A428；系统偏好为简体中文 |
| Xcode | 27.0，build 27A266a；当前 developer directory 指向该 Xcode；iOS/macOS 27 SDK 已存在 |
| 真实 iPhone | CoreDevice 返回 iOS 27.0，build 24A437；wired、paired、booted、Developer Mode enabled |
| 手机语言 | 用户确认英文；本轮已取得英文 Shortcuts/浏览器真实 UI；已实测类型运行值 Text / Image |
| 已有工具 | 配置为 Mobile MCP 1.0.3 → mobilecli 1.0.9 的既有 loopback 修补版；修补二进制与原登记值匹配；DeviceKit 源码 tag 为 0.0.26 |
| 监听边界 | 信任完成后 `/health` 为 HTTP 200 / OK；daemon 与设备转发均只监听 127.0.0.1 |
| 真机代理 | 初始超时并被 iOS Security 拒绝；批准构建/覆盖安装后，经用户在手机完成信任，代理启动恢复 |
| UI tree / 安全页面截图 / 安全点击 | PASS：真实测试页面 UI tree、截图，空文本框聚焦与隐藏键盘；未输入或提交表单，未操作剪贴板 |
| 安全交付探针 | 复用已完成且经 VPS 确认的唯一一次 SFTP 回执；不重新连接 |

苹果的 [Xcode SDK 与系统要求](https://developer.apple.com/xcode/system-requirements) 列明 Xcode 27 支持 macOS 26.6 或更新版本并包含 iOS 27 SDK。工具链版本组合符合此表，不能据此推定旧版 DeviceKit 在当前手机上已通过。

## 签名与实际启动路径的区分

1. 当前 Xcode 管理的主 App 描述文件有效至 `2026-09-25T15:23:13Z`，UITests Target 描述文件有效至 `2026-09-25T15:23:08Z`；本机缓存解析与用户提供的 Xcode 界面一致。
2. 历史留存的已签名主 App/Runner 包内描述文件分别在 `2026-09-16T15:24:26Z`、`2026-09-16T15:28:48Z` 到期。此结论仅属于这两份旧包，不代表当前 Target 或手机安装包。
3. 既有 mobilecli 的 `StartAgent` 枚举手机已安装应用，按 Runner bundle ID 后缀查找，经 testmanagerd 启动该安装实例；不会从 Mac 当前 Xcode 的构建目录自动重新构建或重新安装。工具错误文本中的 WebDriverAgent 名称不表示已切换到独立 Appium WDA。
4. 手机可枚举到对应主 App 和 Runner，版本均为 1.0/build 1；`agent status` 也找到 Runner。bundle ID 和版本相同不足以证明安装包来源或包内描述文件内容。初始安装来源无法仅凭版本确定。随后已取得从独立新构建目录向该手机覆盖安装两份指定包的 CoreDevice 成功回执；这是本次安装过程对应证据，不声称反向提取手机字节做过比对。

5. 对手机已安装 Runner 做一次 `devicectl device process launch` 诊断，Apple 返回 CoreDeviceError 10002、FBSOpenApplicationErrorDomain 3 / Security，原因文本涵盖 invalid code signature、inadequate entitlements 或未被用户显式信任。这确认当前安装实例被系统安全检查拒绝，但没有区分这三种原因，更没有单独证明描述文件过期。

6. 用户批准 R1 构建后，首次构建失败的具体原因是缓存缺少实际 Runner `.xctrunner` 对应的描述文件，不能用有效的 UITests Target 描述文件代替。随后用户批准 R2，允许当前项目/Team/既有 bundle ID 范围内使用 `-allowProvisioningUpdates`；没有启用设备注册参数，没有修改项目文件或升级依赖。
7. R2 `build-for-testing` 成功；新主 App 包内描述文件到期于 `2026-09-25T15:23:13Z`，新 Runner 到期于 `2026-09-26T08:16:08Z`。两个包均通过代码签名验证、bundle ID/应用标识/Team 与描述文件一致性检查、当前设备授权范围检查。覆盖安装均成功，旧构建保留。
8. 新安装首次启动仍返回 Security 拒绝；用户随后在手机完成显式信任。再次启动代理成功，手机开发者页面显示 Runner 与主 App 为 Verified，健康接口及真实 UI 核验通过。这证明信任动作后控制链路恢复，不把之前含多个可能项的 Security 错误单独归因为到期。

**M1 当前工具可用性 PASS；不代表 M2 类型兼容或正式 P6 PASS。** 实际构建路径、包内元数据、安装回执、原始 UI 和日志仅在本机受限目录保存。

## M2 模板与安装证据

修补前，在 Mac 原生快捷指令库中，`CB 发送`、`CB 取回`、`CB 配置` 三个精确名称各出现一次；同时仍有多个带编号或“候选”的相关副本。没有删除、改名、运行或替换这些副本，没有打开/导出秘密配置。

修补前用原生“文件 → 导出 → 任何人”分别导出正式名称的发送和取回模板。用 Apple AEA 验签解包后，比较完整 `WFWorkflowActions` 数组：两者与仓库 P4 基线完全相等。容器字节不同，所以保留新旧文件指纹，不能只凭签名容器 hash 判断动作变化。

| 修补前 Mac 原生导出 | 字节 | SHA-256 | 动作核对 |
| --- | ---: | --- | --- |
| 发送 | 34032 | `431db4c656ae879f52f5659ca4a6d8ab459fe784e4b5af6368fcf457948fba7f` | 218 个动作，数组与基线完全相等 |
| 取回 | 31227 | `4540cd700f79b5b8b55e317c1ada3f0b96a1d9f808d9563a6b1c7cfa07c389f5` | 197 个动作，数组与基线完全相等 |

发送仍只读取一次剪贴板，仍有 `文本`/`图像` 字符串类型条件；两份模板均引用 `CB 配置`。本轮已完成英文 iPhone 的合成类型实测及原生中文条件诊断：实际返回 Text / Image，中文严格相等分支未匹配。当前发送模板的英文兼容性为 FAIL；没有运行产品发送或正式 QA。

工具恢复后，从真实英文 iPhone 的正式名称副本分别执行原生 Share → Options → File → Anyone，再传回本机。签名容器验证解包后，手机两份完整动作数组均与 Mac 当前副本及 Git 基线相等；没有用 iCloud 账号相同推断同步，也没有靠容器 hash 相等推断语义。

| 修补前 iPhone 原生导出 | 字节 | SHA-256 | 动作核对 |
| --- | ---: | --- | --- |
| 发送 | 34020 | `4a639f57814785b0262438ef82eac0db72f0fcc4ae1dcc7d9cbcc755cd963e41` | 218 个动作，与 Mac/Git 基线数组完全相等 |
| 取回 | 31232 | `9aa5799c9a3edd9e8441fcfb443e1b8f7d7c7d48bbfc5beafcce27bd0b48e22b` | 197 个动作，与 Mac/Git 基线数组完全相等 |

手机名称搜索确认三个正式名称各一份；发送另有 4 份带编号/候选的副本，取回另有 6 份。没有删除、运行或改动这些副本。动作按钮目前指向另一个既有菜单快捷指令，没有直接指向 CB 正式名称；该菜单的间接引用尚未展开验证。控制中心当前 3 页（Favorites / Now Playing / Connectivity）没有发现 CB 或 Shortcut 控件；未添加或变更控件。

**正式名称的手机模板对应证据已补齐；英文兼容缺口已证实，M2 尚未完成。** 用户批准的唯一临时探针经 Mac 原生编辑、正式导出和手机编辑器核对后，在英文真机执行。每种合成输入各一次首测，全部 Copy to Clipboard 的 Local Only 在手机确认开启；每轮只捕获一次剪贴板到 Captured，Get Type 与完整性检查/Quick Look 使用该对象。实际以同一探针的三个原生版本依次选择输入，未实现菜单选择，不把准备方案当运行证据。

| 离线用例 | 实测结果 | 证据边界 |
| --- | --- | --- |
| 42 字节合成 Unicode 文本 | Get Type = `Text`；捕获文本 SHA-256 与固定样本一致 | 含 CRLF、emoji、组合字符、制表符、首尾空格；非私人正文 |
| 86 字节 PNG | Get Type = `Image`；捕获对象原生共享回传后逐字节相等 | 2×2、含 alpha；不是正式 P6 大图/边界验收 |
| 632 字节 JPEG | Get Type = `Image`；捕获对象原生共享回传后逐字节相等 | 2×2、无 alpha；未把 Quick Look 截图当字节证据 |
| 中文图像条件 | 原生 `If Type is 图像` → `CHINESE_IMAGE_NO_MATCH` | 读取刚完成的合成 JPEG，未新增剪贴板写入；条件操作码与产品一致 |
| 中文文本条件及清理 | 原生 `If Type is 文本` → `CHINESE_TEXT_NO_MATCH` | 用获批固定结束标记 Local Only 写入，再捕获一次；不是再次覆盖原 Unicode 样本 |

三类首测共 3 次写入，清理结束标记 1 次；没有网络动作、CB 配置调用或正式 controller 调用。临时探针已从 Mac 删除，库数量 35→34；iPhone 回到库显示 34，精确名称搜索无结果，确认删除同步。保留原生导出、合成对象与本机受限截图，不将原始 UI 纳入仓库。

### 已批准的语言修补与当前安装门禁

用户已批准 `M2-LANGUAGE-REPAIR-20260919-R1`。Mac 原生编辑器完成两处修改并正式导出：

- 原零基动作索引 105：同一原类型文本输入满足 `图像` 或 `Image`。
- 原零基动作索引 146：同一原类型文本输入满足 `文本` 或 `Text`。
- 两处均使用 ANY（原生 filter prefix 0）、原比较操作码 4，保留原 GroupingIdentifier、输入引用和所有分支内容。发送仍只捕获一次剪贴板并保留原对象，取回未变。
- 完整动作数组仍为 218；原始序列化差异共 20 处，其中只有 105 / 146 为语义变化。其余 18 处是原生导出将无附件的纯字面 WFTextTokenString 包装改为相同字符串；未忽略附件、变量或其他属性差异。
- 新发送原生导出为 34469 字节，SHA-256 `b2daf2e5edd572c210797b723ea87f33c2313eac06071fd7e3218116b65d8a7e`；Apple AEA 验签与解包通过。这是供集成的模板证据，不是手机安装 PASS。

手机两次实际回读均仍等于旧基线；第二次先重新打开精确名称 `CB 发送` 的手机原生编辑器，再 Share → File → Anyone → 本机回传。回读仍为 218 动作、34020 字节、原发送指纹，105 / 146 仍是中文单条件。**当前手机安装一致性 BLOCKED；修补后的限定回归 NOT RUN。** 按获批方案“若同步不一致，停止报告，不盲目重复导入或删除”，没有进一步导入、删除或运行产品发送/取回。此前离线类型诊断的 PASS / FAIL 不充当修补后回归结果。

### 手机当前身份独立核验

手机当前身份核验：**MATCH**，核验时间 `2026-09-19T10:46:40Z`。

该结果独立于历史交付来源及语言修补，不改变整体准备门禁。旧 P4 解密交付工具仍缺私钥，不能标为可复用。

## 冻结前 controller 加载器

实现和使用约束见 [加载器说明](../../../scripts/qa/CONTROLLER_LOADER.md)。严格接受 VPS 原生四字段，QA origin、预期 run/候选及固定 controller session 来自独立本机配置。`validate` 不启动 controller，不提交 Status，不绑定会话。

17 项离线测试通过。另以实际 CLI 验证：四个字面值 `DUMMY` 的结构样例被拒绝；格式有效的合成数据被接受；输出不回显合成 token。测试覆盖七位小数秒 UTC、45 分钟边界、过期/错误候选、字段/权限/链接、稳定 session、子进程环境映射及错误输出。没有安装依赖、启用正式 QA 或调用正式 controller。

## 交给 Windows/VPS 的下一步

- Windows 可先集成本分支的加载器、离线测试和原生发送模板修补；这是 Mac 集成提交，尚无完整冻结候选 SHA。
- M1 工具可用性 PASS；既有 SSH/SFTP 无秘密探针证据复用；加载器离线验证 PASS；手机当前身份 MATCH。以上均不能替代 M2 安装与回归门禁。
- M2 剩余项：精确名称手机发送副本与新模板一致、获批限定回归，以及既有动作按钮菜单的间接引用核验。手机副本回读不一致，按方案停止自动替换，待具体替换路径批准后继续。
- 正式角色文件尚未生成/加载。`credentialDeliveryReady=false`；不运行正式 controller/Status，不签发或消耗正式测试租约，正式 QA 保持关闭。
