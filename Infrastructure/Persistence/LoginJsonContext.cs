using Core.Contracts.Auth;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chat_App.Infrastructure.Persistence;

[JsonSerializable(typeof(LoginRequest))]
[JsonSerializable(typeof(LoginResult))]
[JsonSerializable(typeof(LoginCheckStatus))]
[JsonSerializable(typeof(UserStatus))]
[JsonSerializable(typeof(ErrorResult))]
[JsonSerializable(typeof(RefreshTokenRequest))]
[JsonSerializable(typeof(LogoutRequest))]
[JsonSerializable(typeof(EamilRequest))]
[JsonSerializable(typeof(RegisterRequest))]
[JsonSerializable(typeof(EmailResult))]
[JsonSerializable(typeof(RegisterResponse))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    GenerationMode = JsonSourceGenerationMode.Default,
    Converters = [typeof(JsonStringEnumConverter<LoginCheckStatus>), typeof(JsonStringEnumConverter<UserStatus>)]
)]
public partial class LoginJsonContext : JsonSerializerContext
{
}
