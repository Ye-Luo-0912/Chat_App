using Avalonia.Styling;
using Chat_App.Services;
using Chat_App.Shared.Commands;
using Chat_App.Shared.Extensions;
using Chat_App.Shared.Mvvm;
using Core.Interfaces;
using Serilog;
using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Chat_App.Presentation.ViewModels.Auth;

/// <summary>
/// 注册页面 ViewModel，分两步完成：
/// </summary>
public partial class RegisterViewModel : ViewModelBase
{

    #region 导航public RegisterViewModel()
    /// <summary>由外部注入的导航回调（传入目标 ViewModel）。</summary>
    public Action<object>? NavigateTo { get; set; }

    #endregion


    #region 步骤状态

    /// <summary>
    /// 步骤状态
    /// </summary>
    private RegisterStep _currentStep = RegisterStep.VerifyEmail;

    public RegisterStep CurrentStep
    {
        get => _currentStep;
        set
        {
            if (SetProperty(ref _currentStep, value))
            {
                OnPropertyChanged(nameof(IsStepOne));
                OnPropertyChanged(nameof(IsStepTwo));
            }
        }
    }

    public bool IsStepOne => CurrentStep == RegisterStep.VerifyEmail;
    public bool IsStepTwo => CurrentStep == RegisterStep.SetPassword;


    #endregion


    #region 属性--字段
    private readonly IAuthClientService _authClient;

    private static readonly Regex EmailRegex = EmailGeneratedRegex();

    private string _email = string.Empty;
    public string Email
    {
        get => _email;
        set
        {
            if (SetProperty(ref _email, value))
            {
                GetCodeCommand.RaiseCanExecuteChanged();
                NextStepCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private string _verificationCode = string.Empty;
    public string VerificationCode
    {
        get => _verificationCode;
        set
        {
            if (SetProperty(ref _verificationCode, value))
                NextStepCommand.RaiseCanExecuteChanged();
        }
    }

    private string _password = string.Empty;
    public string Password
    {
        get => _password;
        set
        {
            if (SetProperty(ref _password, value))
                RegisterCommand.RaiseCanExecuteChanged();
        }
    }

    private string _confirmPassword = string.Empty;
    public string ConfirmPassword
    {
        get => _confirmPassword;
        set
        {
            if (SetProperty(ref _confirmPassword, value))
                RegisterCommand.RaiseCanExecuteChanged();
        }
    }

    private string _errorMessage = string.Empty;
    public string ErrorMessage
    {
        get => _errorMessage;
        set => SetProperty(ref _errorMessage, value);
    }

    #endregion


    #region 验证码按钮

    private string _getCodeButtonText = "获取验证码";
    public string GetCodeButtonText
    {
        get => _getCodeButtonText;
        private set => SetProperty(ref _getCodeButtonText, value);
    }

    private bool _isGetCodeButtonEnabled = true;
    public bool IsGetCodeButtonEnabled
    {
        get => _isGetCodeButtonEnabled;
        private set => SetProperty(ref _isGetCodeButtonEnabled, value);
    }
    #endregion

    #region 命令
    public AsyncRelayCommand GetCodeCommand { get; }
    public AsyncRelayCommand NextStepCommand { get; }
    public AsyncRelayCommand RegisterCommand { get; }
    public RelayCommand BackToStepOneCommand { get; }
    public RelayCommand GoToLoginCommand { get; }

    #endregion

    #region 构造函数
    public RegisterViewModel(IAuthClientService authClient)
    {
        _authClient = authClient;

        GetCodeCommand = new AsyncRelayCommand(OnGetCodeAsync, () => IsGetCodeButtonEnabled && IsValidEmail(Email));
        NextStepCommand = new AsyncRelayCommand(OnNextStepAsync, CanNextStep);
        RegisterCommand = new AsyncRelayCommand(OnRegisterAsync, CanRegister);
        BackToStepOneCommand = new RelayCommand(OnBackToStepOne);
        GoToLoginCommand = new RelayCommand(OnGoToLogin);
    }

    #endregion


    #region 逻辑部分

    private bool CanNextStep() => IsValidEmail(Email) && VerificationCode.Length == 6;

    private bool CanRegister() =>
        !string.IsNullOrWhiteSpace(Password) &&
        !string.IsNullOrWhiteSpace(ConfirmPassword);

    private static bool IsValidEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email)) return false;
        return EmailRegex.IsMatch(email);
    }

    /// <summary>发送验证码，并倒计时 60 秒。</summary>
    private async Task OnGetCodeAsync(CancellationToken ct)
    {
        ErrorMessage = string.Empty;

        if (!IsValidEmail(Email))
        {
            ErrorMessage = "请输入有效的邮箱地址";
            return;
        }

        try
        {
            IsGetCodeButtonEnabled = false;
            GetCodeCommand.RaiseCanExecuteChanged();

            Log.Information("向 {Email} 发送验证码", Email);

            //发送验证码
            var isSuccess = await _authClient.SendRegisterCodeAsync(Email, ct);
            if (!isSuccess)
            {
                ErrorMessage = "验证码发送失败，请检查网络或稍后再试";
                return; // 发送失败直接 return，不进入倒计时
            }

            GetCodeButtonText = "发送成功！";
            await Task.Delay(500, ct); // 稍微停顿半秒

            // 60 秒倒计时
            for (int i = 60; i > 0; i--)
            {
                ct.ThrowIfCancellationRequested();
                GetCodeButtonText = $"{i}s 后重试";
                await Task.Delay(1000, ct);
            }
        }
        catch (OperationCanceledException) { /* 页面离开时取消，忽略 */ }
        catch (Exception ex)
        {
            ErrorMessage = $"发送失败：{ex.Message}";
            Log.Warning(ex, "发送验证码失败");
        }
        finally
        {
            GetCodeButtonText = "获取验证码";
            IsGetCodeButtonEnabled = true;
            GetCodeCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>验证验证码，通过则进入第二步。</summary>
    private async Task OnNextStepAsync(CancellationToken ct)
    {
        ErrorMessage = string.Empty;

        if (!IsValidEmail(Email))
        {
            ErrorMessage = "请输入有效的邮箱地址";
            return;
        }

        if (VerificationCode.Length != 6)
        {
            ErrorMessage = "请输入6位验证码";
            return;
        }


        // 本地格式正确直接切界面！
        CurrentStep = RegisterStep.SetPassword;
        Log.Information("邮箱 {Email} 验证码格式校验通过，进入密码设置步骤", Email);

        await Task.CompletedTask;
    }

    /// <summary>完成注册。</summary>
    private async Task OnRegisterAsync(CancellationToken ct)
    {
        ErrorMessage = string.Empty;

        if (Password.Length < 8)
        {
            ErrorMessage = "密码长度至少为 8 位";
            return;
        }

        if (Password != ConfirmPassword)
        {
            ErrorMessage = "两次输入的密码不一致";
            return;
        }

        try
        {
            // 携带 (邮箱, 验证码, 密码)
            var result = await _authClient.RegisterAsync(Email, VerificationCode, Password, ct);

            if (!result.IsSuccess)
            {
                // 扩展方法提取错误！
                ErrorMessage = result.GetDisplayError();

                // 如果后端提示验证码不对，自动退回第一步
                if (ErrorMessage.Contains("验证码"))
                {
                    CurrentStep = RegisterStep.VerifyEmail;
                }
                return;
            }

            Log.Information("用户 {Email} 注册成功", Email);

            // 注册成功 → 返回登录页，让用户登录
            OnGoToLogin();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ErrorMessage = $"注册失败：{ex.Message}";
            Log.Warning(ex, "注册失败");
        }
    }

    private void OnBackToStepOne()
    {
        CurrentStep = RegisterStep.VerifyEmail;
        ErrorMessage = string.Empty;
        Password = string.Empty;
        ConfirmPassword = string.Empty;
    }

    private void OnGoToLogin()
    {
        // 调用方通过 NavigateTo 设置 LoginViewModel 完成跳转
        NavigateTo?.Invoke("login");
    }

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.IgnoreCase | RegexOptions.Compiled, "zh-CN")]
    private static partial Regex EmailGeneratedRegex();



    #endregion
}



public enum RegisterStep
{
    VerifyEmail,
    SetPassword
}