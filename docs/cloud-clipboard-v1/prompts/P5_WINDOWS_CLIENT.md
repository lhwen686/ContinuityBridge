# P5 — Windows：完成自动同步客户端与受限测试 runner

在Windows当前用户会话的项目中执行。读COMMON、01/02/05/08和P4结果。先整合已审核的Mac快捷指令/契约变更到集成分支，使未来所有主机使用同一候选SHA。P4手机图片关键能力未通过时，不通过扩大到原生iOS App来绕过范围，应先解决实际阻塞。

## 目标

完成真正可用的Windows托盘客户端：本地文本/图片复制自动上传，远端更新自动写入真实Windows剪贴板；无需本机入站API或Tailscale。允许在预先说明的测试窗口使用合成fixture覆盖剪贴板；不能触碰私人数据/关闭浏览器，不能把后台SSH会话当交互桌面。

复用STA消息窗口、OpenClipboard重试和隐私格式规则。图片读取支持当前实际格式，输出注册PNG及目标App所需DIBV5/DIB；bitmap按规定编码，透明度/方向正确，不能只写路径/URL/Base64。CF_HDROP普通文件不发送。编码/网络脱离STA，限制解码像素和内存。

实现文本/图片类型模型、localGeneration、原子本地应用与operation标记、自写不回传、有限去重、serverEpoch/revision、CAS、断线重连与HTTP降级。下载期间本地新复制要使旧下载失效。网络重试保留同一请求键，不把旧离线候选无条件覆盖新云内容；不上传启动前剪贴板，不因云端TTL去清空本地。覆盖uint序号回绕和相同内容过期后重复制。

实现可用托盘：配置baseURL和安全凭据、连接/错误状态、暂停/恢复、清空云端、退出、单实例、可选登录自启。第一次启用自动上传需清晰告知；不修改全局Windows云剪贴板、代理/防火墙。运行在用户会话，不安装交互式Windows Service。凭据进入OS保护存储，生产日志无正文/hash/token。

实现最小测试设施：一个用户显式启动、可见、可停止的Windows TestAgent + 独立staging QA sidecar协议。只接受已提交fixtureId对应的固定动作，不接受任意命令/路径/URL/正文；分controller/runner短期令牌、run租约、candidateSHA、单会话、超时。各端主动HTTPS连接，不依赖LAN。不在产品运行时常开，也不将QA控制器打进production部署。

运行有意义的单元、HTTP、Windows真实剪贴板/目标测试窗口检查；产生可部署候选SHA-B和Windows构建包（若当前阶段可完成）。打包前确认无QA凭据/源秘密/个人数据。无代码签名证书时如实说明未签名，不关闭系统安全防护。

显式提交/push任务分支，交付P3更新staging所需SHA-B、测试agent启动方式、fixture清单、已执行与NOT RUN矩阵。不要声称已经完成尚待P6的iPhone↔Windows E2E。
