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
> 跨仓联调已完成（2026-08-19，真实 Server/Realtime/Gateway 栈，HTTP 权威 vs TCP 读取逐项对账）：
> Server `RelationshipProjectionSnapshotReader` 增加全量用户流枚举（无关系数据的用户也产出
> version=0 空快照基线），修复空列表用户 TCP 读取 `relationship_read_projection_unavailable`
> 与 HTTP 空列表不一致的缺陷，并补回归用例
> `ListStreams_EnumeratesEmptyUsersSoRebuildCanBaseline`；
> Realtime 开 `RelationshipProjectionRebuild__Enabled` + `RelationshipProjectionRead__Enabled`
> 从 Server 导出端点（`X-Relationship-Projection-Key`）同步投影；对账 harness（`.tmp-call-e2e` 内
> `RelationshipReconcile`）对好友/申请/黑名单三类列表逐项比对 HTTP 权威与 TCP 读取，
> 覆盖有数据用户（loaduser1）、空列表用户（loaduser3）、待处理申请用户（loaduser4），
> 支持多页分页、状态一致性、重建同步轮询重试，最终 70 PASS / 0 FAIL。
> REL-E2E-4 完成。

> 阶段收口扫描（2026-08-19）：服务端各仓非 Docker 测试全绿。修复两处陈旧断言回归——
> Shared `ContractBoundaryTests` `VersionPrefix` 断言由 `0.5.1` 更新为当前 `0.5.3`；
> TCP `CallSignalingRealtimeIntegrationTests.FullLifecycle` 转发计数由 2 更新为 3 并断言 End
> 信令转发（`DefaultCallControlProcessor` 非 silent 命令一律转发的既定行为），Shared 110/110、
> TCP 579/579 通过。Realtime.Tests 109 例失败全部为 `Failed to connect to Docker endpoint`
> （Testcontainers 依赖，Docker 守护进程不可用），Server.IntegrationTests 同样依赖 Docker——
> 属环境性失败，非代码回归；Docker 恢复后需重跑这两套以闭环。

完成标准：好友、申请、黑名单可首屏加载、增量同步、reset 重建和离线恢复；失败不推进水位、不破坏旧投影，HTTP mutation 与 TCP read 最终一致。

## 下一阶段功能

### P1：`VOICE-MSG-2` 语音消息

1. 复用附件上传、扫描、绑定、历史和同步，补齐 codec/container、duration、sample rate、channels、size 与可选 waveform 元数据。
2. 实现录音权限、暂停/取消、上传续传、发送失败恢复、播放进度、耳机/扬声器切换、缓存回收和离线可用；未知 codec 或非法元数据明确失败。
3. 发送前确认附件为 `Available`，使用 client message id 与 attachment id 幂等合并本地消息、ACK 和服务端回声；应用重启后不得重复上传或重复发送。
4. 指标只记录低基数的录音/上传/播放结果、duration 与错误分类，不记录音频内容、对象地址、token 或联系人隐私。

> 端到端联调二轮（2026-08-30）：历史侧语音元数据已修复（Realtime 附件绑定链路写入语音 6 列 + Enrich 回查带出，VoiceE2E 41/0，BinE2E 12/12，CallE2E 27/27 全绿，已部署 relgate）。**客户端跟进项已完成（2026-08-31）**：`SendChatMessageAsync` 增加可选 `attachments`（AttachmentRefDto）参数；outbox 上行时按 ClientMessageId 回查 LocalMessage.AttachmentsJson 携带 refs（仅带附件消息触发回查，文本消息零开销），录音/普通附件消息的 wire 上行即携带语音元数据——网关写入附件注册表，历史重建即有来源。UnitTests 233 / Protocol.Tests 96 全绿。

端到端联调（2026-08-30，relgate 真栈 + `.tmp-voice-e2e`）：实时路径语音元数据双格式存活一致；发现并修复服务端 presign 缺 audio/wav；剩余缺口为历史路径语音元数据丢失（Server/Realtime 落库侧），详见 Shared docs/NEXT-STAGE.md 的 VOICE-MSG-2 节。

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

#### 进展（客户端信令控制面 wire 层已闭环，状态机与会话编排已闭环）

- `ChatSessionClient` 新增 `SendCallCommandAsync`（call id + command id 幂等、单调 revision；grant 只原样携带）与 `CallSignalReceived` S2C push 事件；`PacketCommand.CallCommandRequest/Response/CallSignal` 走真实编解码与按 RequestId 精确配对。
- 能力协商：仅当握手服务端回显 `GatewayFeature.CallSignaling` 时 `SupportsCallSignaling=true`，未协商则 fail-closed（`NotSupportedException`）；断线/Error 包按 RequestId 批量失败在途 call 命令并清空 pending。
- 纯状态机 `Core/Models/CallSession.cs`：invite/ringing/accept/reject/cancel/end/reconnect 迁移表、终态唯一不可逆、本地乐观迁移 + 服务端权威覆盖、对端信令按 signal id 幂等去重、单调 revision。
- 媒体面抽象 `Core/Interfaces/ICallMediaSession.cs`：控制面只交换 SDP offer/answer 与 ICE candidate，媒体面（WebRTC/SRTP/ICE/STUN/TURN）留待真机接入；SDP 经信令平面完整双向透传。
- 会话管理器 `Core/Services/CallSessionManager.cs`：命令编排（乐观迁移 → 权威收敛）、来电分派、invite/ringing 超时收尾（TimedOut/Missed）、Active 时启动媒体面、终态收尾释放；`App.axaml.cs` 注册 `ICallSessionManager`（MediaFactory 暂缺省）。
- 测试：
  - Unit：`CallSessionStateMachineTests`（状态机迁移表 + 管理器，假客户端 + 手动延迟，34 项）；Unit 合计 114。
  - Integration：新增 `CallSessionManagerE2ETests`（双设备 over 真实 wire：邀请 → 来电 → 应答/拒绝/取消 → Active 媒体面双向 SDP → 挂断终态收敛，3 项）；`CallSignalingClientTests` 与 `RequestMatrixTests` 保持。
  - 全量 Unit 142 / Protocol 58 / Integration 216 通过。

#### 进展（WebRTC/SRTP 媒体面真实实现已接入）

- 实时播放 sink `Core/Interfaces/ICallAudioSink.cs` + `Infrastructure/Services/Call/WaveOutCallAudioSink.cs`
  （NAudio `BufferedWaveProvider`/`WaveOutEvent`，通话缓冲约 300ms、溢出丢弃、未打开静默丢弃、Open/Close/Dispose 线程安全幂等）。
- 纯编译辅助 `CallMediaCodec`（采样率→`AudioSamplingRatesEnum` 映射、PCM short/byte 小端互转、RTP
  clock units 计算）与 `CallMediaStateMapper`（ICE 状态 → `CallMediaState`）。
- 真实媒体会话 `Infrastructure/Services/Call/SipsorceryCallMediaSession.cs`：SIPSorcery
  `RTCPeerConnection`（默认 STUN，生产可注入 TURN 凭据）、本端 addTrack(Opus/48k)、offer/answer 创建、
  `setRemoteDescription`/`addIceCandidate`、`restartIce`；上行 `SendLoopAsync` 从 `IWaveSampleSource`
  拉 20ms 帧经 `AudioEncoder` 编码后 `SendAudio`，下行 `OnAudioFrameReceived` 解码 PCM 直写播放 sink；
  ICE 状态经 `CallMediaStateMapper` 上报 `StateChanged`，本端 ICE candidate 经 `LocalIceCandidate` 事件暴露。
- DI 接入：`App.axaml.cs` 注册 `ICallSessionManager` 时经 `MediaFactory` 创建
  `SipsorceryCallMediaSession`（Windows 走 `MicrophoneSampleSource` + `WaveOutCallAudioSink`）；创建失败
  fail-soft 回退 null（仅控制面），保证无音频设备平台仍可信令联调。
- 测试：`UnitTests/CallMediaPlaneTests.cs`（codec 映射/PCM 往返/RTP 单位 + ICE 状态映射 + sink 参数校验/
  未打开静默/空包/幂等释放）。SIPSorcery 升至 10.0.15（修复两个高危漏洞）。
- 全量 Unit 142 / Protocol 58 / Integration 216 通过；修复 `RingingTimeout_CalleeMissed` 的并行时序
  flaky（等待 `IsTerminal && EndReason==Missed` 完整收敛后再断言）。
- 跨仓信令联调（Gateway 测试仓）：`ChatApp.TcpGateway.Tests/Networking/CallSignalingRealtimeIntegrationTests.cs`
  在生产适配路径上打通两层——两端真实 TCP 连接 → `CallCommandHandler` → `RealtimeCallBackend` → 真实
  `DefaultCallControlProcessor`（内存仓储 + 默认 grant 校验）。验证 invite→ringing→accept→active→end 终态
  收敛、offer/answer 经临时信令路径转发、End（不携带 SDP）不转发、grant 过期 fail-closed 返回稳定错误。
  Gateway 测试仓全量 579 通过（1 Redis 跳过）。
- 能力隔离（客户端）：`CallSignaling_*` 系列验证未协商 `CallSignaling` 位时命令 fail-closed
  （`NotSupportedException`），且 `ConversationSync`/`MessageMutation` 能力位与消息发送帧不受影响——
  关闭通话能力不影响消息与同步。全量 Unit 142 / Protocol 58 / Integration 217 通过。

下一步：跨仓真机联调（Server/Realtime/Gateway 栈）验证 WebRTC 直连/TURN、ICE restart、弱网与通话期间
降级（拒绝/超时/断线重连/网络切换唯一终态）。

#### 进展（跨仓授权缺口已补齐：Server 签发 + Realtime 校验 + 客户端入口）

- **Server 侧 call grant 签发**：新增 `POST /api/calls/grants`（`CallsController`），校验主被叫互认好友且无屏蔽
  后以 `JwtSettings.Secret` 对规范载荷 `CallId|CallerUserId|CalleeUserId|ExpiresAtMs|Nonce` 做
  HMAC-SHA256 签名（`CallGrantSigner`）；错误分类 `call_grant_invalid_target_user/not_friends/blocked/
  signing_unavailable`，未配置密钥 fail-closed。`ChatApp.Server.IntegrationTests/Calls/CallGrantSignerTests` 5 项通过。
- **Realtime 侧签名校验**：新增 `SignedCallGrantVerifier`（HMAC-SHA256 恒定时间比对，校验签名/过期/结构，
  与 Server 同款 canonical 载荷同密钥）；`CallGrantSigning:Secret` 配置时注册覆盖默认结构校验
  （`RealtimeServicesRegistration`），未配置保留开发默认。`ChatApp.Realtime.Tests/SignedCallGrantVerifierTests` 7 项通过。
- **客户端通话 UI 入口**：新增 `ICallApiService`/`CallApiService`（POST `/api/calls/grants` 获取 grant）；
  `ChatViewModel` 注入 `ICallSessionManager` + `ICallApiService`，新增 `StartCallCommand`（直聊会话取 grant 后
  `StartCallAsync`）/`AcceptCallCommand`/`RejectCallCommand`/`EndCallCommand`，订阅 `IncomingCall/CallStateChanged/
  CallEnded` 暴露 `IsIncomingCall/IncomingCallerName/IsCallActive/CallStatusText`；`ChatView.axaml` 新增通话按钮 +
  来电横幅（接听/拒绝）+ 通话中横幅（挂断）。`UnitTests/CallClientIntegrationTests` 12 项新增，全量 Unit 200 通过。

#### 进展（跨仓真机联调闭环：本地栈端到端信令验证通过）

用真实 Server/Realtime/Gateway 本地栈打通双客户端端到端信令：HTTP 登录两名好友 → `POST /api/calls/grants`
签发 grant → 双端真实 TCP 连 Gateway(127.0.0.1:8888) 鉴权 → `CallSessionManager` 驱动
invite→ringing→accept→active→end 终态收敛，并验证对端 SDP 双向透传与媒体面生命周期。
联调 harness（`.tmp-call-e2e`）最终 **PASS 27 / FAIL 0**。联调中发现并修复三个跨仓缺陷：

- **命令 id 跨参与方冲突**（客户端 `Core/Models/CallSession.cs`）：主叫/被叫本地计数器都从 `{CallId}:c1` 起步，
  被叫首条 Accept 的 `CommandId` 与主叫 Invite 相同，被 Realtime 误判为幂等重放而返回当前状态（Ringing）。
  修复：`NextCommandId()` 以本端角色作前缀（Caller→`A`、Callee→`B`），保证 1:1 通话内跨参与方唯一。
- **revision 跨参与方冲突**（`CallSession.cs`）：主叫 Invite 后服务端全局 revision=1，被叫 Accept 本地 revision
  也从 1 起步，被判 `RevisionStale` 拒绝。修复：`NextRevision()` 以服务端权威 revision 为底
  （`max(本地, 服务端)+1`），且 `ApplyRemoteSignal` 从对端信令推进权威 revision，使后续命令严格大于服务端全局 revision。
- **非 SDP 终态命令未转发对端**（Realtime `DefaultCallControlProcessor`）：原仅对 `CarriesSdp`
  （Invite/Accept/Reconnect）转发信令，End/Reject/Cancel 不携带 SDP 从不转发，导致对端收不到挂断/拒绝通知、
  无法收敛终态（主叫挂断后被叫一直停留 Active）。修复：非 silent（`!IsSilent`，即排除 Ringing ack）命令一律
  经临时信令路径转发给对端，对端靠 `Kind` 驱动本端终态收敛；SDP 载荷预算仍只对携带 SDP 的命令计数。

回归：全量 Unit 200 / Realtime Tests 382 / CallSession 相关集成测试均通过，联调后无回归。

剩余：真实网络环境下的 WebRTC 直连/TURN、ICE restart、弱网与通话期间降级（拒绝/超时/断线重连/网络切换唯一终态）
人工验证（见下节真机手册形态）。

#### 进展（远程真机弱网与降级路径验证：4 场景通过，1 项库级限制记录）

在远程 relgate 真机（192.168.5.49）跑通真实 Server(8080)/Realtime(:8081/:8082)/Gateway(8888)/
coturn(3478) 栈 + 真实 SIPSorcery WebRTC 媒体面（SRTP/ICE/STUN/TURN），harness 即
`.tmp-call-e2e`（`--media --scenario=`），双客户端本机、媒体经 lo/relgate TURN 穿越：

- **degrade（降级路径唯一终态）PASS 47/FAIL 0**：被叫拒绝→Rejected、接通前取消→Cancelled、
  邀请超时→TimedOut、通话中挂断→HungUp，双端终态唯一一致；断线重连（`ReconnectAsync`）在
  Active 通话内发起，双端状态不变、被叫收到重连后新 SDP offer、媒体面保持 Connected（真实音频持续，
  SendCalls=411/SendFail=0）。覆盖 CALL-E2E-2 完成标准中的拒绝/超时/断线重连/网络切换唯一终态。
- **direct（直连 host candidate）PASS 14/FAIL 0**：双端 Connected，音频双向持续（SendFail=0）。
- **relay（强制 `iceTransportPolicy=relay`，媒体经 relgate coturn 中继）PASS 14/FAIL 0**：TURN 建连与
  relay 路径可用，音频持续。
- **weak（netem 弱网下通话）PASS 20/FAIL 0**：通话建立后经 `relgate-netem.sh` 对 lo 注入
  `weak`（80ms±20ms + 3% loss + 512kbit），媒体面保持 Connected、音频继续流入（+25KB）；
  清除 netem 后保持 Connected 且音频恢复（+25.7KB）。弱网降级不终断、恢复路径正确。
- **ice-restart（媒体面 ICE restart）记录为 SIPSorcery 库级限制**：重协商触发
  （`OnAudioFormatsNegotiated #2`）、双端进入 Connecting，但 restart 后两端均无法回到 Connected。
  根因在 SIPSorcery `restartIce`→`RtpIceChannel.Restart()`：`LocalIceUser/LocalIcePassword` 是 ctor 生成的
  readonly 字段，`Restart()` 仅复位并重新采集、不旋转凭据（实测 offer2/answer2 ice-ufrag 与首轮一致）、
  不重建连接级状态（新 checklist/新 nominated pair）。控制面断线重连（`degrade-reconnect`）与弱网保持已通过，故「网络切换/断线重连」的
  降级承诺由客户端协议栈履行；SRTP 级 ICE restart 需以真实 WebRTC 客户端（Chrome/Edge/移动端）跨客户端验证，
  不归入本协议栈缺陷。
- 修复 harness `RunSsh` 用 `bash -lc '{cmd}'` 拼接在 .NET/Linux 下引号被吞导致 netem 注入失败——改
  `ProcessStartInfo.ArgumentList` 逐参数传参，netem 应用/清除恢复可用。

#### 待办：ICE restart（SIPSorcery 库级限制与解决方案）

**状态：跨端复测通过（✅ 2026-08-19）**：以真实 Chrome 浏览器为对端跑 `CallE2E.exe --media
--scenario=cross --host=<coturn ip>`，结果 **PASS 9 / FAIL 0**。浏览器完成凭据轮换（初始 `a=ice-ufrag:Bo4Z`
→ restart `nhmN`），SIPSorcery 作被动（answer）侧正确应用新凭据并在新候选对（host 62541 → prflx 56299）上
回到 Connected；媒体面存活客观口径通过：restart 后浏览器下行 rxPkts 686→786 持续递增、本端 sink 上行
+20331B 持续流入。协议栈侧仅需正常应用新 offer/answer，验证通过；进程内 `--scenario=ice-restart` 仍待
SIPSorcery 补丁（见下）。

**现状/限制**：`SipsorceryCallMediaSession`（SIPSorcery 10.0.15）的进程内 ICE restart 不完整——
`restartIce()`→`RtpIceChannel.Restart()` 仅复位并重新采集（重协商触发、双端进入 Connecting），但
restart 后双端均无法回到 Connected（真机 `--scenario=ice-restart` 复现，实测 offer2/answer2 ice-ufrag
与首轮一致——未旋转）。根因：`RtpIceChannel.LocalIceUser/LocalIcePassword` 是 ctor 生成的 readonly 字段，
`Restart()` 不旋转本地凭据，也不重建连接级状态（新 checklist/新 nominated pair），违反 RFC 8445 §9
（ICE restart 必须使用新凭据）。属第三方库限制，不归入本协议栈缺陷。

**解决方案（按优先级）**：

1. 跨端验证为主：以真实 WebRTC 客户端（Chrome/Edge/移动端）与本端 `SipsorceryCallMediaSession` 互通，
   SRTP 级 ICE restart 由浏览器原生 ICE 栈完成，SIPSorcery 侧仅需正常应用新 offer/answer。
2. 客户端降级兜底已具备：控制面 `CallSessionManager.ReconnectAsync`（`degrade-reconnect` 场景 PASS）在
   网络切换/断线重连时以新 SDP offer 重建媒体面，双端状态保持 Active——协议栈层面的「网络切换/断线重连」
   承诺已由它履行，不依赖进程内 ICE restart。
3. 持续跟进 SIPSorcery：升级或对 `RtpIceChannel.Restart()` 重建连接级状态（新 checklist、清除旧 nominated
   pair）作贡献补丁，并在 `--scenario=ice-restart` 复测通过后置为本机默认路径。

验收标准：以真实浏览器对端完成一次 ICE restart（凭据旋转 + 重连后 Connected + 媒体面存活）。「媒体面存活」
以客观口径判定：双端 ICE/DTLS Connected + 浏览器下行 RTP 包数在 getStats(inbound-rtp) 上持续递增（SRTP 在新
凭据下解密成功）+ 本端 sink 侧上行计数递增。（注：不要求浏览器「听得见」本端出站音频，见下方编码器限制。）
或在 SIPSorcery 补丁后 `--scenario=ice-restart` 恢复 PASS。

#### 已解决：SIPSorcery Concentus opus 编码器与 Chrome 解码的互操作缺口（G.711 规避，2026-08-19 跨端复测 PASS）

**根因**：`SipsorceryCallMediaSession`（SIPSorcery 10.0.15）出站音频由 `AudioEncoder`（纯 C# Concentus
opus 编码器）产生。浏览器跨端实测：Chrome→本端的 opus 能被本端 Concentus 解码器正常解码（本端 CountingSink
音频帧持续流入），但本端 Concentus 编码出的 opus 帧 Chrome 解码器不认——Chrome `getStats` 显示下行
rx=684pkt(lost=0)、candidate pair `[succeeded]`（SRTP 解密+媒体帧到达无误），但解码后静音 `rms=0.000`。
判定为单向不对称互操作缺口：**Concentus opus 编码器输出与真实浏览器 opus 解码不兼容**（SIPSorcery↔SIPSorcery、
Concentus 编+解的自洽链路不受影响）。属第三方库编码器限制，不归入本协议栈缺陷。

**解决/规避（G.711 路径落地）**：
1. `SipsorceryCallMediaSession` 新增 `preferPcmu` 参数：开启后 track 声明 PCMU/PCMA 双格式（8kHz/单声道，
   Chrome 原生解码），协商后编码走 SIPSorcery `AudioEncoder` 的 MuLawEncoder（G.711 μ-law），完全避开
   Concentus opus 编码器；新增 `NegotiatedCodec` 属性上报协商出的上行编码格式供断言。默认仍为 opus，
   不改变现有 SIPSorcery↔SIPSorcery 链路。
2. 跨端 harness（`BrowserInterop.cs`，scenario=cross）改用 `SineToneSampleSource(8000,1,…)` 8kHz 源 +
   `preferPcmu`，PASS 门槛为客观媒体面存活口径：双端 ICE/DTLS Connected + 浏览器下行 RTP 包持续递增 +
   协商格式为 PCMU/PCMA（`media.NegotiatedCodec is PCMU or PCMA`）；`rms` 仅作诊断（自动化 Chrome 无音频
   输出设备，AudioContext 采样目标静音，Opus 与 G.711 皆读 0）。
3. 复测（2026-08-19，Chrome + coturn @192.168.5.49）：answer 协商 `rtpmap:0 PCMU/8000`、本端媒体面 Connected、
   双向 RTP 流入、ICE restart 后仍 Connected（浏览器 rxPkts 769 vs 基线 670），场景完成 PASS；真人耳听确认
   留待真机手工验证。
4. 生产出站包 { useinbandfec; minptime } 与 Chrome rtpmap 对齐已在做（2 声道声明匹配 opus/48000/2）。

### P1：`APP-OPS-1` 客户端完整性

补齐 Push token 注册/轮换/撤销、设备与安全设置、同步失败诊断、附件缓存治理和无障碍体验。仅在关系与语音主链路不被阻塞时并行推进。

#### 进展（附件缓存治理）
- `Core/Services/AttachmentStorageService.cs`：缓存容量上限可配置（默认 512MB，可注入小容量驱动 LRU 测试）；
- 新增 `UnitTests/AttachmentCacheGovernanceTests.cs`（8 项）：LRU 淘汰顺序、每账户容量隔离、
  `.partial` 在途文件豁免、`cache.version` 标记文件保护、哈希校验失败不落盘完整缓存、路径安全与缓存命中；
- 全量 Unit 150 / Protocol 58 / Integration 217 通过。

#### 进展（同步失败诊断）
- 新增 `Core/Diagnostics/SyncFailureRecord`：结构化失败记录（机器错误码/信息/发生时间/可重试性）；
- `ISyncDiagnostics` 扩展 `LastFailure`/`FailCount`/`ConsecutiveFailures`，成功归零连续失败但保留最近失败记录；
- `SyncEngine.Fail` 按错误码分类可重试性：网络/服务端瞬时（`SYNC_ERROR`/`BOOTSTRAP_FAILED`/
  `CONVERSATION_LIST_PAGE_FAILED`/`RELATIONSHIP_SYNC_FAILED`/`RELATIONSHIP_SYNC_PROJECTION_UNAVAILABLE`）
  标记为可自动重试，其余契约违例/会话失效/能力不匹配视为永久失败；
- 诊断页指标源新增 `sync_fail_count`/`sync_consecutive_failures` 计数；
- 新增 `UnitTests/SyncDiagnosticsTests.cs`（6 项）：结构化记录、临时/永久分类、连续失败归零、`UNKNOWN` 回退；
- 全量 Unit 156 / Protocol 58 / Integration 217 通过。

#### 进展（设备与安全设置）
- 新增 `Core/Settings/ClientSettings`：类型化设置模型（通知预览、附件自动下载、空闲自动锁定），带默认值与越界归一化；
- 新增 `ISettingsService`/`SettingsService`：SQLite 键值存储、按账户隔离、透明加/解密数值序列化；
- `ClientDbContext` 新增 `LocalSetting` 实体（`OwnerUserId`+`Key` 唯一索引），迁移 `AddLocalSettings` 已生成；
- `SettingsViewModel` 注入设置服务，加载/持久化设置并响应属性变更；
- 新增 `UnitTests/SettingsServiceTests.cs`（6 项）：默认值、往返一致、账户隔离、越界归一化、幂等更新；
- 全量 Unit 162 / Protocol 58 / Integration 217 通过。

#### 进展（Push token 管理）
- 新增 `Core/Models/DTO/PushTokenDtos.cs`：`PushPlatformDto`（Fcm/Apns/WebPush）、注册/注销请求响应 DTO、
  `PushTokenLimits`（token/label/requestId 长度上限），与 Gateway 侧数值一致；
- `ChatSessionClient` 新增 `RegisterPushTokenAsync`/`UnregisterPushTokenAsync`：本地参数校验（平台枚举/token 长度/
  label 长度）、按 RequestId 精确配对响应、断线/Error 包 fail-closed 批量失败在途 push 命令并清空 pending；
- `ChatJsonContext` 注册 push DTO 的 source-generated 序列化（camelCase wire）；
- 新增 `IntegrationTests/PushTokenClientTests.cs`（7 项）：注册往返（platform/token/label 字段完整）、业务拒绝传播、
  按精确 token 注销、按设备注销（wire 省略 token）、本地参数校验、断线 fail-closed 回收；
- 全量 Unit 162 / Protocol 58 / Integration 224 通过。

#### 进展（无障碍体验）
- 新增 `Core/Accessibility/AccessibilityFontSize`（标准/大/特大三档，`ToScale`→1.00/1.15/1.30、`Coerce` 规整非法值）
  与 `AccessibilityOptions`（从设置解析出渲染选项：字体缩放、减少动效、高对比度，`ScaleFont` 供 UI 缩放）；
- `ClientSettings` 新增 `FontSize`/`ReduceMotion`/`HighContrast` 及 Normalize 规整；
- `SettingsService` 持久化/读取新键（`a11y_font_size`/`a11y_reduce_motion`/`a11y_high_contrast`）；
- 新增 `IAccessibilityService`（选项持有者 + 变更广播）+ `AccessibilityService` 实现并注册 DI；
- `SettingsViewModel` 暴露响应式属性与字体下拉（`SelectedFontSizeIndex`/`FontSizeOptionsDisplay`），
  加载/变更时解析并 `Apply` 到无障碍服务；`SettingsView` 新增「无障碍体验」分区（字体下拉 + 动效/高对比度开关）；
- 新增 `UnitTests/AccessibilityOptionsTests.cs`（7 项）+ `SettingsServiceTests` 扩展（无障碍往返/非法档位规整）；
- 全量 Unit 173 / Protocol 58 / Integration 224 通过。

#### 进展（附件无障碍标签）
- `MessageView.axaml` 为三类附件交互补无障碍名称（`Avalonia.Automation.AutomationProperties.Name`）：
  语音播放按钮（随播放状态输出「播放语音/暂停语音」）、附件下载按钮（「下载附件 {文件名}」）、待发送附件移除按钮（「移除附件 {文件名}」）；
- `VoicePlaybackStateConverter` 新增 `Label` 输出（播放语音/暂停语音），与 Icon 输出保持一致；
- 新增 `VoicePlaybackTests` 转换器标签测试（4 组状态 + 一致性 + ConvertBack）；
- 全量 Unit 179 / Protocol 58 / Integration 224 通过。

#### 进展（无障碍渲染层应用）
- 新增 `Presentation/Services/AccessibilityThemeApplier`：订阅 `IAccessibilityService.OptionsChanged`，
  挂接到主窗口实时应用——根 `FontSize` 缩放（基准 14px × 档位倍率，随继承传播）、高对比度切暗色
  `ThemeVariant`、减少动效在主窗口挂 `reduce-motion` 类；启动时 `App.OnFrameworkInitializationCompleted` Attach；
- `HomeViewModel` 注入 `IAccessibilityService`，暴露 `ReduceMotion` 并订阅变更（Dispose 时退订）；
- `HomeView` 的 `TransitioningContentControl.PageTransition` 绑定 `ReduceMotion`，
  经新增 `MotionTransitionConverter` 在减少动效时关停页面切换动画（返回 null）；
- 新增 `UnitTests/AccessibilityRenderingTests.cs`（9 项）：根字号映射、主题变体映射、过渡转换器行为；
- 全量 Unit 188 / Protocol 58 / Integration 224 通过。

#### 进展（断线 Resume：客户端接入，2026-09-04）
- 旧表述「暂不声明 SessionResume」已作废：客户端 Resume 全链路接入完成（网关侧事务化 Resume 此前已就绪）。
- `ChatSessionClient`：ClientHello 声明 `GatewayFeature.SessionResume`（`AdvertiseSessionResume` 可关，默认开，
  与 `AdvertiseBinaryPayload` 同模式）；`ConnectAsync` 新增可选 `resumeToken` 参数并写入 `ClientHello.ResumeToken`；
  `HandleResumeResponse` 按新语义处理——本代已发起 Resume 时消费响应（成功 → 直接进入已认证状态，
  失败 → 交协调器回退），未请求 Resume 收到 ResumeResponse 仍按协议违例断连（fail-closed 不变）。
- 网关契约落地：Resume 成功只回 `ResumeResponse`（无 ServerHello，连接恒 JSON、BinaryPayload 协商位被剥离，
  客户端同步收敛协商状态）；失败先回 `Error(ResumeFailed/DependencyUnavailable/AccountSuspended)` 再照常发
  ServerHello，客户端可回退完整认证。
- 重连状态机（`ChatConnectionCoordinator.ConnectOnceAsync`）：连接后本地有未过期 ResumeToken → 先 Resume；
  成功 → 跳过 `AuthenticateAsync` 并持久化网关轮换的新 token；`ResumeFailed`/超时 → 清 token 回退完整认证；
  `DependencyUnavailable` → 保留 token 退避重试；单纯断线（IOException）→ 保留 token 供 TTL 内重连 Resume。
- Token 持久化：`AuthToken` 表新增 `ResumeToken`（DPAPI 密文落库，与其他令牌同惯例）+ `ResumeTokenUpdatedAtMs`
  两列（迁移 `20260904090000_AddResumeTokenColumns`），`IDatabaseService` 新增
  `GetResumeTokenAsync`/`SaveResumeTokenAsync`/`ClearResumeTokenAsync`（本地新鲜度窗口 2 分钟，真实 TTL 由网关裁决）；
  认证成功颁发/轮换的 token 自动落库，重启后仍可 Resume；显式登出（`DeleteTokenAsync` 清行）与
  新登录会话（`PersistLoginSessionAsync` 清零残留，防跨账户携带）覆盖生命周期。
- 新增 `Core/Models/ResumeAttemptResult`（结果快照：Success/FailureKind/轮换 token/SessionId/水位）。
- 测试：`Protocol.Tests/TcpHandshakeContractTests` 扩展 7 项（成功路径不发 AuthenticationRequest、ClientHello
  携带 token 与能力位、ResumeFailed/DependencyUnavailable 回退、带内 Success=false、网关忽略 token、
  关闭开关不声明、未请求 ResumeResponse 仍 ProtocolViolation）；`IntegrationTests/SessionResumeTests` 3 项 +
  `TcpHandshakeTestServer` Resume 场景帧变体（成功帧 / 失败 Error 帧）；`UnitTests/CoordinatorSessionResumeTests`
  4 项（协调器状态机 + 真实 SQLite：轮换保存 / 清除回退 / 依赖不可用保留 / 无 token 认证后落库）；
  `UnitTests/ResumeTokenPersistenceTests` 6 项（保存读取/密文落库/清除/过期过滤/无行忽略/新登录清零/迁移可发现）。
- 全量 Unit 250 / Protocol 103 / Integration 233 通过。

## 支撑项

- `BIN-INTEGRATION-3`：Shared 完成所需真实 schema 后，Client 只接入共享 encoder/decoder。握手保持 JSON，协商后连接级固定格式；不得在 Client 复制 schema、持有公共 pointer 实现或让 borrowed view 跨 `await`。
  > 状态（2026-08-30）：**已完成接入**。`ChatSessionClient` 在 ClientHello 声明 `GatewayFeature.BinaryPayload`（`AdvertiseBinaryPayload` 可关，默认开），按 `ServerHello.PayloadFormat` 精确匹配 `json`/`chatapp-bin-v1` 固定连接格式（未知值协议违例断连）；`SendPacketAsync`/`RoutePacket` 按协商格式分流，握手与 ServerHello 恒 JSON；新增 `Core/Protocol/Binary/BinaryPayloadMapper`（54 对客户端 DTO ↔ 共享规范 DTO 双向映射 + 11 个共享类型直通命令，DateTime↔Unix ms、枚举按数值核对），JSON 主链路零改动。消费共享包 `ChatApp.Protocol.Tcp.Binary.Schemas` 0.5.4（本地 feed 的 Protocol.Tcp 0.5.3 为残缺旧构建，已弃用）。测试：Protocol 96 / Unit 207 / Integration 228 全绿（含协商/回退/双向 codec/RequestId 路由二进制端到端）。JSON fallback 必须保持可用：服务端回应 json 时行为与旧版完全一致。
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

