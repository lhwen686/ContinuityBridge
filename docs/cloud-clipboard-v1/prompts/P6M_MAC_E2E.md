# P6-M — Mac mini：自动执行跨设备真实 E2E

你运行在Mac mini项目，使用已验证Mobile MCP/WDA操作USB真iPhone。读COMMON和05测试矩阵。Windows P6-W必须READY，staging与两端均对应同一候选SHA-B；否则先核查，不把异版证据拼接。

目标是通过受限QA mailbox协调Windows真实桌面runner，自动完成两个方向的文本/图片复制粘贴。只操作合成fixture和指定测试App；禁止私人数据、任意shell远程控制或修改生产服务。一个手机同一时间只允许本会话操作，不并发跑其他UI agent。

每个场景顺序为：QA run准备fixture/状态 → 在源设备执行真实复制 → 实际产品发送/自动上传 → 实际产品取回/自动接收 → 目标原生剪贴板核验和真实粘贴 → 收集独立oracle → PASS/FAIL。不得直接调用服务器上传接口替代源端复制，又把它算E2E。

Windows→iPhone：让Windows TestAgent把指定fixture放进真实Clipboard，等待产品客户端上传；在真iPhone执行CB取回、在测试便笺粘贴，并用合成测试校验步骤核对完整文本或图像。

iPhone→Windows：在真iPhone测试页面/图片中复制fixture，执行CB发送；等待Windows产品自动接收，runner核验原生格式与粘贴。不要从Mac剪贴板或API上传替代手机源动作。

执行矩阵中的关键双向、长文本、透明图、近20MB、超限、替换、竞争、回环、重连、TTL、权限/错误场景。部分可由L0/L1证明的恶意/边界项目引用同SHA报告，但每项注明证据层。至少一次实际30分钟staging过期验证；短TTL和fake clock只能补充，不冒充真实等待。若会话窗口无法完成等待，记录NOT RUN和可执行的继续步骤，不谎称已等满。

WDA direct clipboard因前台限制失效时用已验证的产品快捷指令/受控fixture oracle，不能根据截图猜完整性。图像传输校验byte hash；必要系统重编码时解释并校验像素/尺寸/alpha。系统权限/解锁由用户完成，暂停自动化而非绕过。

发现代码bug先生成最小复现与所属模块，不在Mac改Windows-only代码并宣称通过；交回Windows修复形成新SHA，更新staging，重跑受影响矩阵。截图/UI tree留本机ignored artifacts，共享摘要脱敏。

结束时关闭QA run/租约，交付每项PASS/FAIL/BLOCKED/NOT RUN、候选SHA、测试环境与实际证据、剩余人工L3项目。只有两个方向真正成立才写双向E2E PASS；P7是否可进入依明确门禁判断。
