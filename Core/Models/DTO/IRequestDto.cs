namespace Core.Models.DTO;

/// <summary>
/// 请求 DTO 协议接口：RequestId 由发送方唯一生成，服务端响应原样回显，
/// 路由层以此关联请求与响应。禁止在调用方与发送模板中各生成一个 RequestId。
/// </summary>
public interface IRequestDto : ChatApp.Shared.Protocol.Tcp.ITcpRequest
{
}
