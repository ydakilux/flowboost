using Microsoft.Win32;
using System.Security;
using System.IO;

namespace flowboost.Services;

public sealed class AutostartService
{
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "flowboost";
    private const string LegacyValueName = "MXKeypad";
    public void RemoveLegacyValue()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunPath, writable: true);
            key?.DeleteValue(LegacyValueName, false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        { AppLog.Write("Could not remove legacy startup entry", ex); }
    }
    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunPath, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunPath)
            ?? throw new UnauthorizedAccessException("Unable to open the Windows startup registry key.");
        if (enabled) key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue(ValueName, false);
    }
}
