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

剩余工作：跨设备端到端联调，以及真实设备上的采集压测/降级策略。

完成标准：录制 → 上传 → 发送 → 跨设备接收 → 播放 → 历史/同步恢复形成完整测试链路，扫描拒绝、断网、重启和过期附件均可恢复或给出明确终态。

### P1：`CALL-E2E-2` 1:1 语音通话

1. 客户端状态机覆盖 invite、ringing、accept、reject、cancel、end、timeout、reconnect 和多设备竞争；以 call id + command id 幂等处理重复与乱序信令。
2. 媒体使用 WebRTC/SRTP 与 ICE/STUN/TURN；TCP 只承载可靠信令。实现麦克风权限、音频设备切换、ICE restart、前后台恢复和弱网提示。
3. 记录建连成功率、p95 建连时间、RTT、jitter、loss、concealment、TURN relay 比例与客户端 CPU/内存；不记录 SDP、ICE 明文或音频内容。

完成标准：两端在直连、TURN、拒绝、超时、断线重连和网络切换下都得到唯一终态；关闭通话能力不影响消息与同步。

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
