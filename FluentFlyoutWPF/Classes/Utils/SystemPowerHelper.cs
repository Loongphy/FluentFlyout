// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace FluentFlyout.Classes.Utils;

/// <summary>
/// System-level power &amp; theme actions: toggle Windows light/dark theme and put the machine to sleep.
/// <para>
/// Implementation modeled after PowerToys:
/// - theme toggling mirrors the LightSwitch module (registry keys under
///   HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize plus a WM_SETTINGCHANGE /
///   WM_THEMECHANGED broadcast so the shell &amp; apps refresh instantly);
/// - sleep uses SetSuspendState (powrprof.dll), the same API behind rundll32 powrprof.dll,SetSuspendState.
/// </para>
/// </summary>
internal static class SystemPowerHelper
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    #region Constants

    private const string PersonalizationRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsThemeValueName = "AppsUseLightTheme";
    private const string SystemThemeValueName = "SystemUsesLightTheme";
    private const string ColorPrevalenceValueName = "ColorPrevalence";

    // Messages broadcast after a theme registry change so the shell, Explorer and apps pick it up instantly
    private const uint WM_SETTINGCHANGE = 0x001A;
    private const uint WM_THEMECHANGED = 0x031A;
    private const uint WM_DWMCOLORIZATIONCOLORCHANGED = 0x0320;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    private static readonly IntPtr HWND_BROADCAST = new(0xFFFF);

    #endregion

    #region Native methods

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);

    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    #endregion

    #region Theme toggling (mirrors PowerToys LightSwitch)

    /// <summary>
    /// Toggles the Windows system and app themes between light and dark (each follows
    /// its own current state, matching PowerToys LightSwitch behavior).
    /// </summary>
    internal static void ToggleSystemTheme()
    {
        bool systemLight = GetCurrentSystemTheme();
        bool appsLight = GetCurrentAppsTheme();

        Logger.Info($"Toggling Windows theme (system light = {systemLight}, apps light = {appsLight})");

        SetSystemTheme(!systemLight);
        SetAppsTheme(!appsLight);
    }

    /// <summary>
    /// True when the Windows "system" theme (taskbar, start menu) is light.
    /// </summary>
    internal static bool GetCurrentSystemTheme() => ReadThemeValue(SystemThemeValueName);

    /// <summary>
    /// True when the Windows "apps" theme is light.
    /// </summary>
    internal static bool GetCurrentAppsTheme() => ReadThemeValue(AppsThemeValueName);

    /// <summary>
    /// Sets the Windows system theme (taskbar, start menu, ...). When switching to light,
    /// also resets ColorPrevalence so the accent-colored taskbar/titlebar set by dark mode does not stick.
    /// </summary>
    internal static void SetSystemTheme(bool light)
    {
        WriteThemeValue(SystemThemeValueName, light);

        if (light)
        {
            ResetColorPrevalence();
        }

        BroadcastThemeChanged();
    }

    /// <summary>
    /// Sets the Windows apps theme (apps follow light/dark).
    /// </summary>
    internal static void SetAppsTheme(bool light)
    {
        WriteThemeValue(AppsThemeValueName, light);
        BroadcastThemeChanged();
    }

    private static bool ReadThemeValue(string valueName)
    {
        try
        {
            // 1 = Light, 0 = Dark. On any failure default to light, never throw.
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(PersonalizationRegistryPath);
            return key?.GetValue(valueName) is int value ? value > 0 : true;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, $"Failed to read Windows theme registry value '{valueName}'");
            return true;
        }
    }

    private static void WriteThemeValue(string valueName, bool light)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(PersonalizationRegistryPath);
            key.SetValue(valueName, light ? 1 : 0, RegistryValueKind.DWord);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to write Windows theme registry value '{valueName}'");
        }
    }

    /// <summary>
    /// Restores ColorPrevalence to its default (0) and broadcasts the DWM color change.
    /// </summary>
    private static void ResetColorPrevalence()
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(PersonalizationRegistryPath);
            key.SetValue(ColorPrevalenceValueName, 0, RegistryValueKind.DWord);

            SendMessageTimeout(HWND_BROADCAST, WM_DWMCOLORIZATIONCOLORCHANGED, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 5000, out _);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to reset ColorPrevalence");
        }
    }

    /// <summary>
    /// Lets the shell and running apps know the theme changed. The same "ImmersiveColorSet" hint
    /// is also picked up by FluentFlyout's own WndProc (see WM_SETTINGCHANGE handling).
    /// </summary>
    private static void BroadcastThemeChanged()
    {
        IntPtr immersiveColorSet = Marshal.StringToHGlobalUni("ImmersiveColorSet");
        try
        {
            SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, IntPtr.Zero, immersiveColorSet, SMTO_ABORTIFHUNG, 5000, out _);
            SendMessageTimeout(HWND_BROADCAST, WM_THEMECHANGED, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 5000, out _);
        }
        finally
        {
            Marshal.FreeHGlobal(immersiveColorSet);
        }
    }

    #endregion

    #region Sleep

    /// <summary>
    /// Puts the machine to sleep immediately. Equivalent to
    /// "rundll32.exe powrprof.dll,SetSuspendState 0,1,0" (hibernate = false).
    /// </summary>
    internal static void SleepNow()
    {
        Logger.Info("Putting the system to sleep");
        try
        {
            if (!SetSuspendState(false, false, false))
            {
                Logger.Warn($"SetSuspendState failed. Win32 error: {Marshal.GetLastWin32Error()}");
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to put the system to sleep");
        }
    }

    #endregion
}