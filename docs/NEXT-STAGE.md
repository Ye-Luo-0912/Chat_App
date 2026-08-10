# 下一阶段与接手状态

## 职责

桌面客户端负责设备能力、UI、本地 SQLite、离线队列和重连恢复；消息、历史与同步的跨进程契约来自 Shared，服务端状态不在客户端另建权威。

## 下一步执行与交接

Client 当前不先实现关系 TCP 读取；接手点是 Shared 完成 `REL-WIRE-2` 之后的 `REL-READ-3` 消费端。

1. **等待输入。** 必须收到 Shared 不可变包/hash、list/catch-up/reset 字段与游标语义、Gateway producer fixture，以及 Realtime `REL-GATE-1`/list canary 证据；缺一项只允许做 SQLite schema 评审，不写自定义 wire DTO。
2. **本地投影。** 以账户 + list type + version 建好友/申请/黑名单投影和水位，整页事务成功后才推进；migration 前滚可重入，回滚 capability 时保留已验证数据和旧 HTTP 路径。
3. **同步接入。** 先消费 Shared 类型并通过 old/new fixture，再处理 partial/reset、unavailable、gap、版本变化、空页 HasMore、重复 cursor、断网和账户切换；mutation 继续走 Server HTTP。
4. **交付 canary。** 提供 consumer fixture、SQLite migration/rollback 说明、HTTP 权威逐项对照和短时重连报告；只给小比例新连接启用，任一漏项/重复/错序立即关闭 capability。
5. **后续顺序。** 关系读取稳定后再做附件/语音消息闭环；binary 双 codec 与媒体功能分别独立批次，不与关系 migration 同版灰度。

下一位 Agent 从 Shared 包和 Gateway fixture 开始核对语义，不从 Realtime 数据库或 Server 实体复制类型。

## 接手状态

- P0（已验证）：历史页校验/兼容推断 `ConversationId`，入站保留 `ClientMessageId`，按 `ChangedAtMs` 单调合并消息/附件/反应，并以可重入 reset 重建本地投影；消息/会话分页缺游标或续页失败均 fail-closed。同步生命周期以最新 intent 为准，Restart/Stop/切账户不会复活旧任务或并发使用会话。Release 构建 0 warning/error；Integration `185/185`、Unit `40/40`、Protocol `58/58` 通过。
- P0（契约迁移已收口）：Client 已直接消费 `ChatApp.Protocol.Tcp 0.4.1` 的历史、同步、附件与会话水位类型，本地同义 DTO 已删除；Client request/Gateway response golden 已由 Protocol tests 固定。
- P0（候选兼容已完成，feed 发布仍是 TODO）：consumer fixture `8/8` 已覆盖旧 payload 缺 `ConversationId`/`ChangedAtMs`、未知可选字段/枚举、双向 cursor 与截断输入，Shared 文档已记录哈希。发布后从 feed locked restore，并补一次 request id 错配、帧级超限和 JSON 降级短联调；回滚只切回上一不可变包，不回滚已成功的 SQLite migration。
- P0（依赖 Server/Realtime，继续走 HTTP）：Server 已生成连续版本关系 delta，并能按 owner/list 在 PostgreSQL `REPEATABLE READ` 下导出带 version/count/hash/snapshot id 的完整快照；Realtime 已在 JetStream ACK 前原子应用增量，也能以 checkpoint 幂等导入快照、推进较旧投影，并忽略被快照覆盖的迟到增量。自动扫描器已有数据库时钟租约、持久化复合 cursor、整页提交、失败续跑、两轮稳定判定和低基数指标；重复同版本快照会核对 count/hash 并修复单 stream。snapshot-gated 只读 list processor 也已实现，但两个开关都默认关闭，Shared/Gateway/Client wire 尚未接入。因此 Client 继续只把现有事件当在线失效提示，不发送 relationship watermark，也不据此推进本地关系水位。
  - 平台前置 TODO：Realtime status/streams 已能安全查看持久化 cursor、稳定轮次、租约/最后错误，以及逐 stream version、数量/hash 和本地 delta 连续性；reconcile 只比较 Server/Realtime 的 version/count/hash/checkpoint/continuity，不下载或返回好友明细。隔离环境用 secret store 注入 Ops key，运行 Realtime 的 `scripts/Invoke-RelationshipProjectionReconcile.ps1`；工具自动做两轮全量分页、状态前后校验和指纹比较，409/503、游标停滞或扫描中变化都会非零退出且报告不含 key。仍需验证多实例抢租、租约过期接管、整页中断续跑、密钥轮换、Server 429/5xx/超时和扫描期间新增较小 owner id。只有工具通过且故障恢复后再次通过，才独立打开只读 canary。
  - 客户端接入 TODO：定义好友、申请、黑名单投影与每 list 水位的 SQLite schema，再接只读 list/sync；投影必须能从确定 snapshot/version 重放，并区分 unavailable、gap、游标失效、membership/permission change 与显式 reset。TCP mutation 不恢复，仍走 Server HTTP。
  - 完成标准：整页事务落库成功后才推进水位；断网、重复、乱序、空页 HasMore、partial、reset 中断和账户切换均可重入，且本地结果与 Server HTTP 权威列表逐项一致。任一条件失败关闭 capability，不删除仍有效的 HTTP 数据或旧水位。
- P1：补齐附件生命周期、Push token、设备/安全设置和同步失败诊断。
  - 附件覆盖选择、上传、扫描中、可用、失败、过期与本地缓存回收；Push token 覆盖注册、轮换、撤销和退出登录；诊断至少能关联账户、设备、请求、同步阶段与可重试原因，且不记录令牌或消息正文。
  - 完成标准：生命周期与恢复路径有聚焦测试，离线重试幂等，数据库迁移可回滚，常用发送/同步路径无额外大对象分配。
- P1（二进制 payload 消费端，Shared 基础层已完成但默认关闭）：保留协商前 JSON bootstrap 和 JSON 回退；为全部握手后有 payload 的命令生成 codec。只有完整握手收到明确格式后才固化本连接 codec，不按 payload 猜测或中途切换；首版 Resume 连接继续使用 JSON。
  - 先对 ChatMessage、History、Sync 的真实大小分布做离线 JSON/二进制基准，再接入共享 codec；短测必须验证降级、未知字段、畸形/超限输入、断线重连和版本不匹配。客户端不拥有指针、池或全局 codec registry，buffer 生命周期仍由现有收发层管理。
- P2（媒体与传输实验）：1:1 音频用 WebRTC/ICE/TURN，TCP 只承载可靠信令；不在客户端实现一套裸 UDP 可靠协议。QUIC 仅在共享契约与二进制基线冻结后做可关闭实验，并始终保留 TCP 回退。

## 功能路线

1. 语音消息：复用附件上传/扫描/断点续传，客户端实现录音、权限、播放、缓存和 QoE；wire 只新增 duration/codec/waveform 等稳定元数据。
2. 1:1 语音通话：客户端采用 WebRTC + ICE/TURN；TCP 仅传信令和能力协商，不实现裸 UDP 媒体栈。
3. 后续再做群通话/SFU、端到端加密和跨端设备恢复；QUIC 仅在实测优于现有方案时引入。

## 验证顺序

聚焦单测/契约测试 → Release 构建 → 短时联调；阶段长测与发布 soak 只在功能冻结后执行。
