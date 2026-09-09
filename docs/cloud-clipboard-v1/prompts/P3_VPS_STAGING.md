# P3 — VPS：按指定 SHA 部署或升级 staging

你现在应通过SSH运行在我的VPS上。先确认Linux/用户/工作目录/实际仓库，不在Windows本地误跑生产命令。读COMMON、06_DEPLOYMENT、02_PROTOCOL与本次交接的精确candidate SHA。现有VPS为用户自用4核8GB，实际资源、OS和服务必须检查。

## 本次默认授权

先只读检查并准备可审核的staging部署方案；不创建公开服务、不改防火墙/SSH/代理/DNS、不重启已有服务。用户明确批准具体planId后，才执行该计划。已有明确授权覆盖本次更新时，引用其范围；不能把“请设计”当远端上线授权。

## 工作

核查现有Docker/Compose、可信HTTPS入口、域名、端口占用、sudo可用性、现有业务、部署路径、磁盘/内存/时钟。需要sudo而未授权就说明未验证部分，不要求把sudo密码贴到聊天、不设置NOPASSWD:ALL。

只部署P2/P5交付的干净、明确SHA。未存在clone时确认已有GitHub认证/安全的只读获取方式，不能从别台复制私人认证目录。生产工作树不承载临时代码修复；修复走任务分支重新交接。

完成计划：镜像构建输入、不可变版本、配置/secret路径、loopback应用端口、反向代理路由/TLS、内存限额、restart策略、预期新增监听、对现有服务影响、健康检查、回滚。已有反向代理优先复用，不能抢80/443。Docker映射loopback，不能仅以ufw状态判断无暴露。禁止关闭TLS验证。

批准后执行：构建/部署指定Relay Linux版本 → 鉴权/无鉴权/size/TTL/小fixture smoke → 验证证书/外部访问与loopback限制 → 检查脱敏日志 → 输出真实版本与回滚命令。记录Linux实际运行测试，不用Windows的结果代替。

这是staging。首次SHA-A不用QA控制器；P5后升级SHA-B时，若已实现独立QA sidecar且本次计划批准，可只在测试入口启用带租约/角色令牌的受限fixture mailbox；不能放入production或公开任意命令接口。

域名、端口批准、精确SHA等无法从现有环境确定时集中问最少问题，同时完成其余只读准备。原始域名/IP/token只存本机.local/或安全配置，不进共享repo。交付staging可用性、SHA/digest、secret已安全配置的状态、P4/P6入口与PASS/FAIL/BLOCKED；不要输出真实密钥。
