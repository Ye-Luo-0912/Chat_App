# 下一阶段与接手状态

## 职责与优先级

桌面客户端负责设备能力、UI、本地 SQLite、离线队列和重连恢复；跨进程契约来自 Shared，服务端状态不在客户端另建权威。

当前以功能链路完整为第一优先级：先完成关系读取，再完成语音消息和 1:1 通话。二进制接入、QUIC 和进一步性能调优只作为支撑项，不阻塞功能闭环。

## 当前 P0：`REL-E2E-4` 关系读取端到端闭环

本地关系投影、watermark、list 查询路由和 sync 应用已经进入当前工作树；下一步不是重新设计，而是收口语义、回归和真实 Gateway 联调。

1. 复核 SQLite migration、投影唯一键、账户隔离和事务边界。整页或完整增量应用成功后才能推进 watermark；取消、异常和进程退出必须保留旧有效投影。
2. 用 Shared 的 list/catch-up/reset 契约接入真实 Gateway。首次读取、显式 reset、gap 和 retention exceeded 都从权威 list 重建；重复或乱序增量按资源键与版本幂等应用。
3. 覆盖空页 `HasMore`、缺失/重复 cursor、partial、分页中断、断网重连、账户切换、多设备变化和能力关闭。无法解释的状态必须 fail-closed，不得把伪空结果写成成功水位。
4. 好友、申请和黑名单 mutation 继续走 Server HTTP。TCP 读取结果最终要与同一账户的 HTTP 权威列表逐项一致；差异只修读取投影或映射，不在客户端引入第二权威。
5. 先完成聚焦 migration/事务/恢复测试，再做 5–20 分钟分页、断线和重连短测。完成后把关系入口从开发开关切换为正常功能路径。

> 状态：聚焦边界测试 14 例（`RelationshipSyncTests`）、20 分钟分页/断线/重连短测
> （`RelationshipSoakTests`）与 HTTP mutation vs TCP read 一致性用例
> （`RelationshipSoakTests.Relationship_HttpMutation_TcpRead_Converges_ItemByItem`）
> 均已完成并通过（Unit 75 / Protocol 58 / Integration 202）。短测以确定性脚本模拟 20 分钟：
> 多页分页重建、断线 fail-closed（投影/水位保持）、重连续跑、reset 全量重建、重建中断
> （projection_changed）后恢复，每轮校验投影收敛 + 水位单调不回退。一致性用例以假 HTTP 服务
> （对应 Server FriendshipController）落地发申请/接受/拉黑/删好友/解拉黑，每步 TCP 增量同步后
> 断言投影与 HTTP 权威列表逐项一致，断线轮 fail-closed、恢复后收敛（客户端不引入第二权威）。
> 关系入口已从开发开关切换为正常功能路径：客户端握手声明 `GatewayFeature.RelationshipRead`
> 能力位，`SupportsRelationshipRead` 由协商结果派生，未协商则 fail-closed。
> 剩余：真实 Server/Realtime/Gateway 栈上的跨仓联调（HTTP 权威 vs TCP 读取逐项对账）。

完成标准：好友、申请、黑名单可首屏加载、增量同步、reset 重建和离线恢复；失败不推进水位、不破坏旧投影，HTTP mutation 与 TCP read 最终一致。

## 下一阶段功能

### P1：`VOICE-MSG-2` 语音消息

1. 复用附件上传、扫描、绑定、历史和同步，补齐 codec/container、duration、sample rate、channels、size 与可选 waveform 元数据。
2. 实现录音权限、暂停/取消、上传续传、发送失败恢复、播放进度、耳机/扬声器切换、缓存回收和离线可用；未知 codec 或非法元数据明确失败。
3. 发送前确认附件为 `Available`，使用 client message id 与 attachment id 幂等合并本地消息、ACK 和服务端回声；应用重启后不得重复上传或重复发送。
4. 指标只记录低基数的录音/上传/播放结果、duration 与错误分类，不记录音频内容、对象地址、token 或联系人隐私。

**Client consumer fixture 已完成**：协议包升至 `ChatApp.Protocol.Tcp 0.5.3`（含语音元数据字段），
源生成 `ChatJsonContext` 自动获得语音字段支持；新增 `VoiceAttachmentConsumerFixtureTests` 覆盖
语音附件经 `AttachmentJson` 的序列化往返、旧客户端（无语音字段）载荷反序列化与未知字段容忍。

**录音/上传链路已完成（Client 侧）**：新增 `IVoiceRecorder` 抽象与 `WavPcmEncoder`
（确定性 RIFF/PCM WAV，跨平台字节级一致）、`VoiceRecorderService`（Start/Stop/Cancel 线程安全，
产出含正确 data 长度的 WAV 与 codec=pcm/container=wav 元数据）。`MessageViewModel` 注入
`IVoiceRecorder`，新增 Start/Send/Cancel 录音命令与实时时长显示，`PendingAttachment` 携带语音
元数据并在 `SendMessage` 映射到 wire `AttachmentRefDto` 语音字段；`SendVoiceAsync` 复用
`IAttachmentClientService.UploadAndConfirmAsync` 上传后立即发送；草稿（`DraftState`）同样持久化语音
字段。录音单测 7 项 + 链路桥接测试覆盖 WAV 结构/时长/元数据/取消/往返。

**真实麦克风采集已接入**：新增 `MicrophoneSampleSource`（NAudio/WinMM，Windows）。
NAudio 推式回调经有界队列桥接为拉式 `Read`；DI 在 Windows 用真实麦克风，其他平台回退到
`SineToneSampleSource`，保证跨平台可用。平台边界与参数校验单测 2 项。

**播放链路已完成（Client 侧）**：新增 `IAudioPlayer` 抽象（`Play/Pause/Resume/Stop`，进度/停止事件），
`Infrastructure` 实现 `PcmAudioPlayer`（NAudio WaveOutEvent/WaveFileReader，定时器上报进度，线程安全
单实例状态）。`MessageViewModel` 注入 `IAudioPlayer`，新增 `PlayVoiceCommand` 与全局播放状态
（`PlayingVoiceAttachmentId`/`IsVoicePlaying`/`VoicePlaybackProgress`/`VoicePlaybackDisplayText`），
点击语音气泡经 `IAttachmentDownloadService` 取本地缓存 WAV 后播放，再次点击暂停/恢复、点其他气泡自动切换。
UI 新增语音气泡模板（播放/暂停按钮 + 进度条 + 时长），`VoicePlaybackStateConverter`（多值转播放图标/进度）
与 `VoiceDurationConverter`（毫秒→mm:ss/H:mm:ss）。播放单测 17 项覆盖时长格式化与播放器边界。

剩余工作：真实设备上的采集压测与三条降级路径的人工验证（见下节手册）。

**降级策略已完成并自动化验证**：`VoiceRecorderService` 达最大时长自动收尾（复位录音态、产出有效 WAV、
触发 `AutoCompleted`，超时后可直接重新录音）；`MessageViewModel.PlayVoiceAsync` 下载失败/返回空时停止播放器
并给出终态提示「语音加载失败」（不进入伪播放）；`SendVoiceRecordingAsync` 上传失败/取消时以生成的
`clientAttachmentId` 调用 `AbandonAsync` 清理服务端孤儿附件并复位上传状态。自动化覆盖：
`VoiceRecorderTests` 录音超时/超时后重录 2 项 + 新增 `VoiceDegradationViewModelTests`（最小桩 + 反射驱动
私有方法，路径 2/3 共 4 项）。根目录 `verify-voice-degradation.ps1` 可一键分组运行三条路径并聚合报告
（`-SkipBuild` 复用产物），当前全部 PASSED。

完成标准：录制 → 上传 → 发送 → 跨设备接收 → 播放 → 历史/同步恢复形成完整测试链路，扫描拒绝、断网、重启和过期附件均可恢复或给出明确终态。

### P1：`CALL-E2E-2` 1:1 语音通话

1. 客户端状态机覆盖 invite、ringing、accept、reject、cancel、end、timeout、reconnect 和多设备竞争；以 call id + command id 幂等处理重复与乱序信令。
2. 媒体使用 WebRTC/SRTP 与 ICE/STUN/TURN；TCP 只承载可靠信令。实现麦克风权限、音频设备切换、ICE restart、前后台恢复和弱网提示。
3. 记录建连成功率、p95 建连时间、RTT、jitter、loss、concealment、TURN relay 比例与客户端 CPU/内存；不记录 SDP、ICE 明文或音频内容。

完成标准：两端在直连、TURN、拒绝、超时、断线重连和网络切换下都得到唯一终态；关闭通话能力不影响消息与同步。

#### 进展（客户端信令控制面 wire 层已闭环）

- `ChatSessionClient` 新增 `SendCallCommandAsync`（call id + command id 幂等、单调 revision；grant 只原样携带）与 `CallSignalReceived` S2C push 事件；`PacketCommand.CallCommandRequest/Response/CallSignal` 走真实编解码与按 RequestId 精确配对。
- 能力协商：仅当握手服务端回显 `GatewayFeature.CallSignaling` 时 `SupportsCallSignaling=true`，未协商则 fail-closed（`NotSupportedException`）；断线/Error 包按 RequestId 批量失败在途 call 命令并清空 pending。
- 集成测试：新增 `CallSignalingClientTests`（能力协商 2 + invite wire 往返 1 + push 事件 1 + 参数校验 1 + 断线 fail-closed 1）；`RequestMatrixTests` 扩展 `call` 到成功/业务拒绝/协议拒绝/超时矩阵。
- 测试：Unit 75 / Protocol 58 / Integration 213 全部通过。

下一步：客户端通话状态机（invite/ringing/accept/reject/cancel/end/timeout/reconnect 与多设备竞争）、WebRTC/SRTP 媒体面（ICE/STUN/TURN）接入，以及与真实 Gateway 的联调。

### P1：`APP-OPS-1` 客户端完整性

补齐 Push token 注册/轮换/撤销、设备与安全设置、同步失败诊断、附件缓存治理和无障碍体验。仅在关系与语音主链路不被阻塞时并行推进。

## 支撑项

- `BIN-INTEGRATION-3`：Shared 完成所需真实 schema 后，Client 只接入共享 encoder/decoder。握手保持 JSON，协商后连接级固定格式；不得在 Client 复制 schema、持有公共 pointer 实现或让 borrowed view 跨 `await`。
- QUIC：不在本阶段实现。裸 UDP 不会减少仍然保留的 TCP session 状态；语音媒体由 WebRTC 媒体面承担，控制面继续保留 TCP 回退。
- 性能：先用 profiler 或分配数据证明功能链路存在热点，再做微基准和 5–20 分钟同构短测；不为理论收益重写稳定路径。

## 跨仓衔接

Server 提供关系权威、附件安全策略和短期 call grant；Realtime 提供投影、消息/Outbox 与临时信令状态；Shared 固定外部 wire；Gateway 显式映射与路由；Client 事务应用投影并拥有设备与媒体体验。

下一位 Agent 先从当前工作树的 `SyncEngine`、`ChatSessionClient`、SQLite migration 和关系投影测试继续，不重建已存在的实现。

## 验证顺序

聚焦单测/契约测试 → Release 构建 → 5–20 分钟故障与联调短测。当前阶段到功能联调验收为止。

## 真机人工验证手册：VOICE-MSG-2 三条降级路径

> 三条路径的失败分支已由 `verify-voice-degradation.ps1` 自动化覆盖（路径 1：`VoiceRecorderTests`
> 录音超时；路径 2/3：`VoiceDegradationViewModelTests` 下载/上传失败，均 PASSED）。
> 本节人工验证聚焦自动化无法覆盖的真实链路：真实网络断连/恢复、真实麦克风时长触达、真实 UI 表现与日志核对。

环境形态：远程 relgate 主机提供 API(HTTP:8080)/Realtime(:8081/:8082)/TCP Gateway(:8888) 与
infra(postgres/garnet/nats)；本机 + 另一台真机各跑一个 Avalonia 客户端，互为好友。

### 前置条件（每台设备都满足）

1. 客户端 `appsettings.json`：
   - `AuthServer:BaseUrl` = `http://<远程IP>:8080`（远程为 HTTP 明文时；HTTPS/域名则在登录页覆盖）。
   - `Tcp:UseTls` = `false`（远程 Gateway 为明文端口；如远程走 TLS 则保持 `true`）。
   - `Voice:MaxDurationSeconds` 默认 60；路径 1 验证时临时改小（如 10）以快速观察自动收尾，验完恢复 60。
2. 远程已跑且为带语音元数据字段的版本：登录响应携带 `Server.Host/Port` 指向真机可达的 Gateway；
   Gateway 监听 `0.0.0.0:8888`（对应 `TcpGateway__ListenAddress=0.0.0.0`）。
3. A/B 两账号已互为好友；客户端日志目录 `%LOCALAPPDATA%\ChatApp\Data\logs`。

### 路径 1：录音超时自动收尾（recording timeout auto-finalize）

1. 两机 `Voice:MaxDurationSeconds` 临时设 10，重启客户端。
2. A 打开与 B 的会话，长按录音 ≥10s 不松手。
3. 预期：到时长后录音自动结束（录音按钮复位、时长停在 ~0:10、`IsRecording=false`），
   触发 `AutoCompleted` → 自动上传并发送；B 收到该语音。
4. 验收：A 无残留录音态；B 收到语音且元数据（duration≈10s、codec=pcm、container=wav）正确；
   日志无未处理异常。若上传失败则按路径 3 给出终态提示（不悬挂）。
5. 验完恢复 `MaxDurationSeconds=60` 并重启客户端。

### 路径 2：播放下载失败防护（playback download-failure terminal state）

1. 正常网络下 A 发一条语音给 B；B 收到后**不要**点播放（保证本地未缓存）。
2. B 断开网络（断 WiFi/拔网线或关闭到远程的访问）。
3. B 点该语音气泡。
4. 预期：下载失败后出现红色 toast「语音加载失败，请稍后重试。」；播放器不进播放态
   （无进度条、播放按钮不置为暂停态，即不伪播放）。
5. 验收：明确终态提示 + 播放器复位；日志记录 `语音下载失败 AttachmentId=...`。
6. 恢复网络后再次点播应能正常播放（缓存/重试恢复）。

### 路径 3：上传失败恢复（upload-failure abandon + terminal state）

1. B 打开与 A 的会话，先断开网络或使远程不可达。
2. B 按住录音几秒，松手发送。
3. 预期：上传失败 → toast「语音上传失败: <原因>」；服务端不留孤儿附件（已 `AbandonAsync`）；
   会话无悬挂「上传中」状态。
4. 验收：明确终态提示；上传进度复位；日志记录上传失败与 Abandon（`ClientAttachmentId=...`）。
5. 恢复网络后重新录音发送应成功（恢复能力验证）。

### 记录与回归

每条路径记录：设备、时间、步骤截图、实际结果 vs 预期、日志关键行。完成后在下一提交中更新本节状态。

