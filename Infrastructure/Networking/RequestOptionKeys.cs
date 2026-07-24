using System.Net.Http;

namespace Infrastructure.Networking;

public static class RequestOptionKeys
{
    public static readonly HttpRequestOptionsKey<bool> SkipAuthInterceptor = new("SkipAuthInterceptor");
}
