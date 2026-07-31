using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Infrastructure.Networking;

public static class RequestOptionKeys
{
    public static readonly HttpRequestOptionsKey<bool> SkipAuthInterceptor = new("SkipAuthInterceptor");

    /// <summary>
    /// 不可重放请求（流式上传等）的请求体重建工厂（九5）。
    /// 当 401 重试且原始内容无法缓冲重放时，拦截器调用此工厂重新创建 HttpContent，
    /// 而非发送空 body。未提供工厂则抛出 ReplayNotNotSupportedException 由上层处理。
    /// </summary>
    public static readonly HttpRequestOptionsKey<Func<CancellationToken, Task<HttpContent?>>> ReplayFactory = new("ReplayFactory");
}

/// <summary>
/// 不可重放请求遇到 401 且未提供请求体重建工厂时抛出（九5）。
/// 上层应捕获此异常，重新创建流与请求后重试，而非依赖拦截器发送空 body。
/// </summary>
public sealed class ReplayNotSupportedException : Exception
{
    public ReplayNotSupportedException(string message) : base(message) { }
}
