# P2 — Windows：实现可移植 Relay 与协议测试

在Windows开发会话执行，读COMMON、01_ARCHITECTURE、02_PROTOCOL、08_CODE_MIGRATION和P0提交的契约/ADR。目标是实现真实可运行、可测试的Relay，**不要只给计划**。当前阶段不部署VPS、不改真实剪贴板、不制作iOS原生App。

从P0已提交基线出发，保留用户变更。新增实际需要的Contracts(net10.0)与Relay(net10.0 ASP.NET Core Web)，不引用Windows-only工程。保留现有Windows SDK约束和项目布局；可修正解决方案包含关系，但不要把全部项目改目标平台。

实现capabilities、全局latest状态、text JSON/image二进制上传、鉴权正文下载、条件清空、WSS提示、最小健康检查。服务端随机设备token映射身份，配置不含真实secret。TTL/20MB/文本限制由配置和capabilities统一。内存正文、重启新epoch、原子替换、有界并发/缓冲、取消/异常清理、Cache-Control:no-store。

落实If-Match/CAS、同设备幂等键+正文指纹、丢包重试、同键异内容409、旧项不可读取410、不允许重试续期/复活。幂等缓存只留结果元数据，不得复用旧带Snapshot正文的StateCoordinator。上传未完整校验前不替换旧项；分块上传同样强制大小。新图片/文本共用一个槽位。

只实现none加密模式，保留版本化envelope升级位置并拒绝未知mode；不要提交伪E2EE。服务端图片验证受限，不用System.Drawing或无界像素解码。

编写真实HTTP/进程和有意义的状态测试：Unicode不截断、size±1边界、流式超限、畸形图、TTL fake clock、并发提交、幂等不持有旧正文、下载时替换/过期、epoch重启、token撤销、WSS重连。加不依赖.NET内部对象的黑盒契约测试，便于未来Cloudflare适配。

编写Linux镜像构建所需Dockerfile和配置模板、明确portable项目/测试入口；只在已有本地工具允许时验证，缺Docker则标Linux镜像NOT RUN，等待P3真实Linux测试。不要将Windows编译成功称为Linux运行通过。不要自动安装系统依赖。

整理fixture生成方式（非私密文本、透明图、合法20MB边界图），写清P4如何用API注入fixture。对新第三方依赖记录理由、许可证与上游来源。完成测试与修复后显式提交并push任务分支，交付SHA-A、运行命令、测试摘要、P3部署输入；不合并main。
