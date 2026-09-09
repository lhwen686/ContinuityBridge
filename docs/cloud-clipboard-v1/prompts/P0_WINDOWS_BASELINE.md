# P0 — Windows：建立新的实施基线与契约

你现在应在我的 **Windows 本地 ContinuityBridge 项目**中执行。先证明实际执行端是 Windows，再读 `docs/cloud-clipboard-v1/prompts/COMMON.md`、仓库 AGENTS.md、docs/development-hosts.md 以及本包 00/01/02/03/08 文档。

## 目标

完成可交接的云端剪贴板 v1 设计基线，使后续 Mac/VPS 不再依赖旧聊天推断需求。本阶段只改文档、契约/测试计划和必要的文档忽略配置，**不实现业务、不触碰当前剪贴板、不部署、不重装工具**。

## 工作

检查现有仓库root、remote可见性、branch、SHA、dirty、SDK路径、global.json和锁文件。不要强行把当前仓库变回720e0d9。运行已有只读 setup/status；读代码确认实际模型/Win32/API/App/测试完成度。

更新 AGENTS.md 和 development-hosts 中已过时的“没有初次提交/remote”描述为有日期的历史记录，写明本次新的分阶段授权；保留浏览器保护、凭据隔离、只读setup、平台门禁。不能把环境已安装或旧报告通过当真机已通过。

重点检查：全工程net10.0-windows；旧API不具备Linux部署资格；旧StateCoordinator 24h幂等缓存是否持有完整正文；Windows uint序号回绕；历史2GB文件/Tailscale路径。逐条更新迁移清单。

将用户九条需求和本包的补充默认值形成ADR，明确20MB十进制、全局单槽位、30min从提交计时、重启内存清空、过期不清设备、离线冲突处理。把02协议细化为可机器校验的OpenAPI/Schema或等价契约，规范If-Match/Idempotency-Key、etag JSON、错误响应、capabilities与未来加密边界；不需要为未实现模块创建空业务工程。

写清各阶段文件所有权、SHA交接、staging/production gate和P4先行的理由。为后续验证生成测试目录/案例清单可以，但不要添加ProjectLoads式占位测试冒充覆盖。依赖/代码改动若非本阶段必要，列入P2/P5。

可创建本机短期feature/cloud-clipboard-v1分支，显式暂存本阶段安全文档/契约，核查staged diff和秘密后commit并推送现有私有origin的同名分支。已有分支/未提交工作先保留，不强制覆盖。不合并main。

## 完成标准

事实基线、可执行契约、迁移清单、测试门禁已写入仓库并正常提交；给Mac/VPS的共享文件不存在秘密或机器私有地址；业务代码/真机/部署未执行。报告新SHA及下一步P1/P2入口。域名尚未给出不阻止完成本阶段。
