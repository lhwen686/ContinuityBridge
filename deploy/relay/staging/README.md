# P3 staging 模板（R3）

这些文件是已验收R3配置的共享模板，不是当前VPS配置的完整副本，也不是新一次部署授权。真实域名、源站地址、注册表目录、凭据及证书只在目标主机配置。模板中的保留示例域名/IP不能用于上线。

- `compose.yaml.template`：运行既有、已核验的本地不可变镜像。`RELAY_IMAGE_ID`来自发布清单；`STAGING_DEVICE_DIRECTORY`仅指向目标主机的服务端哈希注册表目录。该目录挂载到`/run/secrets`，不能改成客户端明文token目录。
- `relay.env`：在本机部署目录从上一级已有的`relay.env.template`准备，核对默认TTL1800、文本1,000,000和图片20,000,000字节；不要把本机完整env提交Git。
- `Caddyfile.template`：保留示例IPv4/域名，宿主Caddy管理可信证书，TCP80重定向、TCP443只代理`/v1/*`，公网`/healthz`为404；HTTP/1.1与HTTP/2，无管理接口或HTTP/3监听。
- `caddy-staging.conf.template`：与R3匹配的systemd限制；必须先确认服务/路径/用户存在，离线校验后按获批计划安装。
- `cbstaging.slice`：本staging Relay专用父级，MemorySwapMax=0。必须先建立并验证父级，再启动引用它的容器；不要把其他业务放入此slice。

Compose保留R3的loopback发布、非root UID1654、只读rootfs、丢弃capabilities、no-new-privileges、临时tmpfs、768MiB/1CPU/128pids与禁dump设置。宿主registry目录需保证UID/GID1654可遍历/读取；实际R3采用目录0750、文件0640，客户端凭据在另一root私有目录。不要仅凭Compose配置值推断实际swap隔离。

模板与实际配置的差异只有：注释、Caddy域名/IPv4改为保留示例值、服务端目录改为必填本机变量。模板不是新测过的部署；R3测试针对发布清单记录的实际配置指纹。解析/静态检查不等于运行验收。

首次发布回滚目标是撤下本staging：停Caddy入口、down本Compose、仅移除本次拥有的80/443规则，确认slice空后移除本slice并重载systemd。R2配置只保留审计，不启动其已知swap隔离缺陷版本。镜像、受限凭据/证书与历史证据保留；不卸载共享依赖、不改SSH/DNS、不清空全局规则。精确操作命令及所有权记录由VPS本机P3运行手册提供，不在其他主机照搬执行。

共享验收入口：[P3交接报告](../../../docs/cloud-clipboard-v1/13_P3_STAGING_HANDOFF.md)。本提交不重新部署当前服务。
