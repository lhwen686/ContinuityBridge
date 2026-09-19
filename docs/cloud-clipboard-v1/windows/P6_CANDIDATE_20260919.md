# P6 冻结候选集成范围（2026-09-19）

本次候选完整合并 Windows W1 与 Mac 冻结前准备历史，不改变协议，不启动正式 QA。最终候选 SHA 以 Windows 验证回执及包内 `manifest.json` 的 `sourceSha` 为准；本文所在提交及后续经验证的修复提交才可作为完整候选，不能使用任一父分支 SHA 代替。

## 完整输入

- 安全基线：`4d2b94777d19a344ed477abe8f35cf6f6c5a4d7a`。
- Windows W1：`ad031274e1fd3e42e6313789312327d86a450b7c`，包含 `91e61b4e8d8a4c534ae22bf8f9017f3afa21055f` 的受限导入及 UTC 回归修正。
- Mac：`344da7fd7c156ce344ac9489a47ebce3ae70195e`，完整包含 controller 加载器、17 项离线测试、M1/M2 回执和发送模板双语条件修补。
- 集成时补齐 TestAgent 包中的 `STAGING_IMPORT.md`，使包内使用说明的导入格式链接可用。

Mac 实测证据见 [Mac 回执](../lab/p6-mac-preparation-20260919.md)，它属于该端既有准备证据，不能宣称为 Windows 本轮重测结果。

## Windows 验证范围

在原生 Windows 独立工作树中，对干净已提交候选使用现有 SDK 和 locked restore，执行默认 `scripts/dev/test-windows-cloud.ps1`（不得传 `-DesktopFixtures`）以及离线 contracts 校验。默认回归涵盖 Core、W1 导入、TestAgent 安全控制、合成图片及本机临时 HTTPS；不会运行正式 TestAgent 或接触真实剪贴板。

`scripts/dev/package-windows-cloud.ps1` 从同一干净提交构建产品与 staging TestAgent 包。检查嵌入候选 SHA、manifest 文件哈希、ZIP 内容及产品包不包含 QA 程序集。原始 TRX、包、哈希清单和最终验证回执留在本机忽略的 `artifacts/`，具体结果随 SHA 交接。

Mac 加载器仅支持 POSIX 权限；Windows 只核验 Python 语法及 Git 文件一致性，不以 Windows 模拟权限代替 Mac 原生离线测试。

## Mac M3 接收条件

1. 在自己的 clone/worktree 中获取 Windows 回传的完整提交，检出精确 SHA，记录 HEAD 和 dirty 状态；保留既有本机设置和用户修改。
2. 重新执行 `python3 -B -m unittest discover -s tests/qa -p 'test_*.py' -v`。只使用合成文件；不要调用正式 controller 或 `Status`。
3. 核对发送模板 SHA-256 为 `b2daf2e5edd572c210797b723ea87f33c2313eac06071fd7e3218116b65d8a7e`，并核对本机当前已安装动作证据与冻结模板对应。取回模板未改，不根据 iCloud 状态或签名容器相同推定手机动作一致。
4. 按 [加载器说明](../../../scripts/qa/CONTROLLER_LOADER.md) 保持正式交付、候选及本机固定 session 的独立来源。M3 本身不签发、加载或消费正式租约。

## 冻结边界

`credentialDeliveryReady=false`；正式 QA 保持关闭，P6-W READY=false。正式凭据、VPS 同 SHA 部署/ACTIVE、Windows 真实接管及跨设备 E2E 均不由本次冻结建立。M3 及完整 P6 仍待各自主机的实际回执。不得把构建、离线测试或候选 SHA 当作完整 P6 PASS。
