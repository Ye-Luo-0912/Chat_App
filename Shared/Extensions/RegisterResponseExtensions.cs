using ChatApp.Contracts.Http.Auth;

namespace Chat_App.Shared.Extensions;

public static class RegisterResponseExtensions
{
    public static string GetDisplayError(this RegisterResponse? response) => response switch
    {
        null => "网络错误或响应解析失败",
        { IsSuccess: true } => string.Empty,
        { Errors: [{ Description: { Length: > 0 } desc }, ..] } => desc,
        { Message: { Length: > 0 } msg } => msg,
        _ => "注册失败，请检查输入"
    };
}
