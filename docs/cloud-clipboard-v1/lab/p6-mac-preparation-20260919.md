# P6 Mac 冻结前准备：2026-09-19 增量回执

本次结果是部分准备完成，**Mac 尚未 PREPARED，不能据此冻结完整候选或启动正式 QA**。安全代码基线为 `4d2b94777d19a344ed477abe8f35cf6f6c5a4d7a`；本次只增加 controller 加载器、离线测试及本报告，不改产品模板或协议。

## M1 现场

| 检查项 | 结果及证据边界 |
| --- | --- |
| macOS | 27.0，build 26A428；系统偏好为简体中文 |
| Xcode | 27.0，build 27A266a；当前 developer directory 指向该 Xcode；iOS/macOS 27 SDK 已存在 |
| 真实 iPhone | CoreDevice 返回 iOS 27.0，build 24A437；wired、paired、booted、Developer Mode enabled |
| 手机语言 | 用户确认英文；本轮 UI 读取受阻，未用 Mac 中文界面代替手机类型实测 |
| 已有工具 | 配置为 Mobile MCP 1.0.3 → mobilecli 1.0.9 的既有 loopback 修补版；修补二进制与原登记值匹配；DeviceKit 源码 tag 为 0.0.26 |
| 监听边界 | 本轮观察到相关转发只监听 127.0.0.1；这不等于设备健康或 UI 可用 |
| 真机代理 | 读取前台应用超时；用户解锁后再次检查仍超时。当前只读进程查询没有找到 DeviceKit 进程；随后一次 Apple 原生 Runner 启动诊断被系统安全检查拒绝 |
| UI tree / 安全页面截图 / 安全点击 | BLOCKED，不能以设备枚举或本地模板导出代替 |
| 安全交付探针 | 复用已完成且经 VPS 确认的唯一一次 SFTP 回执；不重新连接 |

苹果的 [Xcode SDK 与系统要求](https://developer.apple.com/xcode/system-requirements) 列明 Xcode 27 支持 macOS 26.6 或更新版本并包含 iOS 27 SDK。工具链版本组合符合此表，不能据此推定旧版 DeviceKit 在当前手机上已通过。

## 签名与实际启动路径的区分

1. 当前 Xcode 管理的主 App 描述文件有效至 `2026-09-25T15:23:13Z`，UITests Target 描述文件有效至 `2026-09-25T15:23:08Z`；本机缓存解析与用户提供的 Xcode 界面一致。
2. 历史留存的已签名主 App/Runner 包内描述文件分别在 `2026-09-16T15:24:26Z`、`2026-09-16T15:28:48Z` 到期。此结论仅属于这两份旧包，不代表当前 Target 或手机安装包。
3. 既有 mobilecli 的 `StartAgent` 枚举手机已安装应用，按 Runner bundle ID 后缀查找，经 testmanagerd 启动该安装实例；不会从 Mac 当前 Xcode 的构建目录自动重新构建或重新安装。工具错误文本中的 WebDriverAgent 名称不表示已切换到独立 Appium WDA。
4. 手机可枚举到对应主 App 和 Runner，版本均为 1.0/build 1；`agent status` 也找到 Runner。bundle ID 和版本相同不足以证明安装包来源或包内描述文件内容。本轮未取得能将手机安装字节与某份 Mac 构建逐一对应的证据。

5. 对手机已安装 Runner 做一次 `devicectl device process launch` 诊断，Apple 返回 CoreDeviceError 10002、FBSOpenApplicationErrorDomain 3 / Security，原因文本涵盖 invalid code signature、inadequate entitlements 或未被用户显式信任。这确认当前安装实例被系统安全检查拒绝，但没有区分这三种原因，更没有单独证明描述文件过期。

所以当前结论是 **手机已安装 Runner 被系统安全检查拒绝，具体签名/entitlements/信任原因待区分；不能笼统判定当前签名已过期**。本轮没有重新签名、构建安装或修改签名设置。实际路径和完整元数据仅在本机受限回执中保存。

## M2 模板与安装证据

在 Mac 当前原生快捷指令库中，`CB 发送`、`CB 取回`、`CB 配置` 三个精确名称各出现一次；同时仍有多个带编号或“候选”的相关副本。没有删除、改名、运行或替换这些副本，没有打开/导出秘密配置。

本轮用原生“文件 → 导出 → 任何人”分别导出正式名称的发送和取回模板。用 Apple AEA 验签解包后，比较完整 `WFWorkflowActions` 数组：两者与仓库 P4 基线完全相等。容器字节不同，所以保留新旧文件指纹，不能只凭签名容器 hash 判断动作变化。

| Mac 当前原生导出 | 字节 | SHA-256 | 动作核对 |
| --- | ---: | --- | --- |
| 发送 | 34032 | `431db4c656ae879f52f5659ca4a6d8ab459fe784e4b5af6368fcf457948fba7f` | 218 个动作，数组与基线完全相等 |
| 取回 | 31227 | `4540cd700f79b5b8b55e317c1ada3f0b96a1d9f808d9563a6b1c7cfa07c389f5` | 197 个动作，数组与基线完全相等 |

发送仍只读取一次剪贴板，仍有 `文本`/`图像` 字符串类型条件；两份模板均引用 `CB 配置`。这些条件是英文 iPhone 的待测风险，尚无本轮真实类型探针证据，不猜测返回值，不盲目替换中文。

以上只证明 **Mac 当前库副本与模板对应**，不能证明 iPhone 安装副本、动作按钮/控制中心引用或 iCloud 同步完成。手机的存在性、重复副本、实际入口、当前导出/安装对应及文本/PNG/JPEG 类型实测仍未完成。没有覆盖手机剪贴板、导入诊断快捷指令或替换产品快捷指令。

`P4_EXISTING_DEVICE_SLOT_1` 只具备已核对的历史交付来源；当前 iPhone 配置绑定 **NOT VERIFIED**。本轮没有读取 token、输出 token/hash 或操作含秘密的配置。旧 P4 解密交付工具仍缺私钥，不能标为可复用。

## 冻结前 controller 加载器

实现和使用约束见 [加载器说明](../../../scripts/qa/CONTROLLER_LOADER.md)。严格接受 VPS 原生四字段，QA origin、预期 run/候选及固定 controller session 来自独立本机配置。`validate` 不启动 controller，不提交 Status，不绑定会话。

17 项离线测试通过。另以实际 CLI 验证：四个字面值 `DUMMY` 的结构样例被拒绝；格式有效的合成数据被接受；输出不回显合成 token。测试覆盖七位小数秒 UTC、45 分钟边界、过期/错误候选、字段/权限/链接、稳定 session、子进程环境映射及错误输出。没有安装依赖、启用正式 QA 或调用正式 controller。

## 交给 Windows/VPS 的下一步

- Windows 可先集成本提交的加载器及测试；完整候选冻结仍需后续 Mac 手机准备回执，不能将本报告当作 M1/M2 完成或模板无需修复的证明。
- Mac 继续查清实际 Runner 启动阻塞；需要构建/签名安装时先提交具体范围，不由旧包过期推定必须重装。
- 真机工具恢复后，先完成无秘密 UI 核验；手机剪贴板合成覆盖、诊断快捷指令导入及产品替换按各自具体范围取得批准。
- 手机当前业务身份单独核实；正式 QA 角色文件尚未生成/加载。`credentialDeliveryReady=false`，不签发或消耗正式测试租约。
