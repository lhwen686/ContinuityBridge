# P3 staging 完成交接

日期：2026-09-10。阶段结论：**PASS**。批准并验收的计划为`P3-STG-20260910-190c7568-R3`。本文件是脱敏交接，真实入口、主机标识、凭据、Globalping measurement ID及原始证据仅保存在VPS本机。

## 两个SHA的区别

实际构建、部署及验收的候选源码SHA始终为：

```text
190c75680206eea8ae54dcbe3fbc017587adf6e8
```

本报告所在的`feature/p3-staging-handoff`提交仅新增交接资料与模板，其提交SHA不是新的部署候选。用`git log -1 --format=%H -- docs/cloud-clipboard-v1/13_P3_STAGING_HANDOFF.md`取得报告提交SHA。接收方应读取精确提交；不得把fetch报告解释为更新VPS服务或移动候选版本。

平台：Linux amd64，Ubuntu24.04.5 / kernel6.8.0-139；实际VPS核查4vCPU/约8GiB。Docker29.8.0、Compose5.5.1、containerd2.3.5、Buildx0.37.0、宿主Caddy2.11.4。Dockerfile在SDK构建阶段restore/publish，宿主未安装.NET SDK。

运行镜像ID（本地OCI index）：`sha256:16fdd19286454b0d52bbada0f98a8da260bcdc7f0b6152781a2eff873c82628e`。本地构建，未推送镜像registry；平台manifest、image config、构建候选及实际配置指纹见[发布清单](p3/release.json)，不要把不同OCI对象的digest混称。

## 验收结果与证据来源

| 项目 | 结果 | 范围与来源 |
|---|---|---|
| Linux .NET测试 | PASS，17/17，0跳过 | R2同一候选构建；R3没有改源码，不重复全套 |
| Loopback wire | PASS，9组 | 实际Linux容器HTTP |
| VPS可信HTTPS/WSS wire | PASS，9组 | 真实API/WSS；公网health404独立验证，套件health200走loopback；头名匹配不区分大小写 |
| 权限、限额、日志与有效禁swap | PASS | 实际/proc与cgroup、日志扫描及权限核查 |
| 撤销与WSS关闭、重启、新epoch | PASS | token撤销后401/WSS1008；恢复；重启清空，旧项410/旧If-Match412 |
| 配置生效及恢复 | PASS | 临时配置与capabilities一致，恢复精确默认值 |
| R3应用回滚/恢复 | PASS | 撤下本入口/容器/自有规则/空slice，再恢复同镜像和证书 |
| 真实默认30分钟TTL | PASS | 实际1802.095秒，9个采样；读取不续期，到期空槽/旧项410/revision+1 |
| Windows外网HTTPS正反鉴权、WSS通知/内容 | PASS，用户交接 | 用户明确提供通过结论；原始Windows报告路径未提供，未声称VPS重跑或审阅其原始记录 |
| IPv4后端43117外网隔离 | PASS，用户交接 | 同上 |
| IPv6后端43117外网隔离 | PASS，timeout/filtered | Globalping独立外部节点实测，详见下文 |
| 用户入口IPv6 HTTPS/WSS | N/A | 仅当前R3：无AAAA/IPv6代理入口、Caddy仅IPv480/443；契约未要求双栈 |
| 容器公网IPv6直连 | N/A | 仅当前无公网IPv6/网关/外部IPv6路由的容器网络；不替代宿主隔离 |
| P4、P6、生产 | NOT RUN by P3 | 本任务没有执行这些阶段；其他分支的后续工作不在本报告判定范围 |

结构化结论和原始证据SHA256索引见[acceptance.json](p3/acceptance.json)。原始证据不随本提交分发；向VPS操作者按P3-E01至P3-E09索引核对。本报告保留Windows证据来源限制，不把用户交接改写成代理亲自观察。

## IPv6外网收尾

此前Windows10051表示测试端无IPv6路由，不能作为源站隔离通过。源站实际有公网IPv6与默认路由，因此后端隔离仍适用；无AAAA不代表没有源站IPv6暴露面。

2026-09-10 UTC05:44:01.931至05:45:11.669，通过Globalping官方匿名REST API，US与NL各一个独立节点执行TCP22→43117→22。每节点每轮3次，两节点均为22前3/3成功、43117零成功/3次无响应、22后3/3成功。共3项measurement、6个probe tests、18次配置的TCP探测，无付费、注册、替换节点或重复扫描。

请求明确使用TCP及端口；目标是源站IPv6字面地址，请求不传仅适用于主机名的ipVersion。后两次locations引用前一measurement ID，返回顺序和全部可见节点元数据一致、无offline；API未提供源地址、唯一节点ID或路由表，没有编造。结果判定依据结构化stats/timings及原始输出，不以HTTP202/200或finished替代TCP成功。

结合前后可达对照、同节点复用和未变化的本机隔离配置，证明的是**本次所测外部路径与时间窗口内后端不可直接连接**。超时记录为timeout/filtered，不是connection refused，不指认丢包设备，不推及所有可能路径。证据已足够支持该范围的PASS，未增加tcpdump抓包。完整ID/请求/结果保留本机，因为其公开查询结果会揭示源站地址。

## R2问题与R3修正

R2发现：在本机Docker/containerd/systemd组合下，daemon-reload能将容器子cgroup的memory.swap.max从0恢复为max，Docker配置仍显示memory==memorySwap。没有定位具体上游源码责任。R2当时撤下staging；旧TTL仅有60秒采样后取消，不能复用为PASS。

R3新增专用`cbstaging.slice`的MemorySwapMax=0，并用Compose cgroup_parent挂接。实际创建、daemon-reload、restart、force-recreate、Caddy enable及回滚恢复后，父级硬上限均0，父子swap.current采样均0。子级文件仍可能显示max，但父级有效限制持续存在；不声称历史上从未发生swap。

[共享部署模板](../../deploy/relay/staging/README.md)保留最小修正及资源/代理约束。首次回滚目标是撤下本staging；没有可回退的上一批准运行版本，绝不启动已知缺陷的R2。

## 其他主机接收与本机秘密

真实HTTPS Base URL由VPS操作者从本机P3运行记录交接，并在测试端本机配置为`CB_BASE_URL`。此共享文件不含真实域名/IP或测量查询链接。HTTP接口以该Base URL为前缀，WSS路径为`/v1/events`：

- 公网`GET /healthz`预期404；loopback健康200不能当作外网证据。
- `GET /v1/capabilities`与`GET /v1/clipboard`需要Bearer鉴权；WSS握手同样使用Authorization请求头。
- 读取及WSS连通性最少一个有效staging身份；完整双身份黑盒需要两个。当前设备token没有独立只读角色，不能将其称为只读凭据。
- token只在测试端本机安全录入；不得放进聊天、Git、便笺、截图或命令行。收到本报告不代表收到凭据。

已有`tests/relay-blackbox/run.py`与`fixtures.py`仅依赖Python标准库。原始run.py要求公网health200且Client.state对ETag头名区分大小写，不能原样当作当前Caddy入口已可通过的外网CLI；inject.py也受ETag问题影响。VPS适配器依赖本机root/路径/loopback，不是可直接在Windows/Mac运行的脚本。本提交没有修改候选测试或补造跨平台CLI。

用户已允许新建“CB 测试”便笺作为合成图片粘贴目标；这仅是后续测试准备授权，不表示便笺已创建或iPhone实际粘贴已通过。P3完成不自动执行P4或生产。
