using System;
using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace Chat_App;

/// <summary>
/// 全局 DataTemplate：将 ViewModel 实例映射到对应的 View 控件。
/// ContentControl 会自动使用此 DataTemplate 渲染绑定的 ViewModel。
/// </summary>
public class ViewLocator : IDataTemplate
{
    public Control? Build(object? param)
    {
        if (param is null)
            return null;

        var name = param.GetType().FullName!?.Replace("ViewModel", "View", StringComparison.Ordinal);
        if (name is null)
            return new TextBlock { Text = "Not Found: " + param.GetType().FullName };
            
        var type = Type.GetType(name);

        if (type is not null)
        {
            return (Control)Activator.CreateInstance(type)!;
        }

        return new TextBlock { Text = $"Not Found: {name},  TYPE {type} IS NULL" };
    }

    public bool Match(object? data)
    {
        // 匹配任意 ViewModel（只要类名包含 ViewModel）
        return data?.GetType().Name.EndsWith("ViewModel", StringComparison.Ordinal) == true;
    }
}