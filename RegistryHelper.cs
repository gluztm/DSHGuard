using System;
using Microsoft.Win32;

namespace DSHGuard;

public static class RegistryHelper
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "DSHGuard";

    public static void SetAutoStart(bool enabled, string exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            if (key == null) return;

            if (enabled && !string.IsNullOrEmpty(exePath))
            {
                key.SetValue(AppName, $"\"{exePath}\"");
            }
            else
            {
                key.DeleteValue(AppName, false);
            }
        }
        catch (Exception ex)
        {
            // 开关失败原来是 Debug.WriteLine（Release 下写入不生效），排查时看不到任何线索；
            // 按"只记异常"的日志策略落到异常日志里，出问题时有据可查。
            Logger.LogError("RegistryHelper.SetAutoStart", ex);
        }
    }

    public static bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            if (key == null) return false;
            var val = key.GetValue(AppName);
            return val != null;
        }
        catch
        {
            return false;
        }
    }
}
