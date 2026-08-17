// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FluentFlyout.Classes.Utils;

/// <summary>
/// A borderless window that is invisible on screen but shows a dedicated button on the
/// Windows taskbar. Clicking the taskbar button runs the configured action, then the
/// window returns to its minimized (off-screen) state.
/// Each button gets its own AppUserModelID so taskbar buttons of the same process
/// are not merged into one stack in Windows 10/11.
/// </summary>
internal sealed class TaskbarActionButton : Window
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private readonly Action _action;

    // the initial Show() raises Activated once; skip that one so it doesn't trigger the action
    private bool _skipNextActivated = true;

    internal TaskbarActionButton(string title, Action action, string appUserModelId, ImageSource icon)
    {
        _action = action;

        Title = title;
        Icon = icon;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = true;
        ShowActivated = false; // don't steal focus while starting up
        Width = 1;
        Height = 1;
        Left = -32000; // off-screen; only the taskbar button is visible
        Top = -32000;

        Activated += OnActivated;
        SourceInitialized += (_, _) => TaskbarHelpers.SetAppUserModelID(this, appUserModelId);
        Loaded += (_, _) => WindowState = WindowState.Minimized;
    }

    /// <summary>
    /// Updates the taskbar button label (and hover tooltip).
    /// </summary>
    internal void SetTitle(string title) => Title = title;

    private void OnActivated(object? sender, EventArgs e)
    {
        if (_skipNextActivated)
        {
            _skipNextActivated = false;
            return;
        }

        // user clicked the taskbar button -> run the action, then go back to minimized
        Dispatcher.InvokeAsync(() =>
        {
            try
            {
                _action();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Taskbar action button failed");
            }
            finally
            {
                WindowState = WindowState.Minimized;
            }
        });
    }
}

/// <summary>
/// Sets a per-window AppUserModelID (System.AppUserModel.ID) so that multiple windows of the
/// same process appear as separate, non-merged buttons on the taskbar.
/// </summary>
internal static class TaskbarHelpers
{
    private static readonly Guid IID_IPropertyStore = new("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99");
    private static readonly Guid PKEY_AppUserModelID_FmtId = new("9f4c2855-9f79-4b39-a8d0-e1d42de1d5f3");
    private const uint PKEY_AppUserModelID_Pid = 5;

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint cProps);
        [PreserveSig] int GetAt(uint iProp, out PROPERTYKEY pkey);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
        [PreserveSig] int Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public IntPtr union1;
        public IntPtr union2;
    }

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid riid, out IPropertyStore ppv);

    [DllImport("propsys.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int InitPropVariantFromString(string psz, out PROPVARIANT ppropvar);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT pvar);

    /// <summary>
    /// Assigns a unique AppUserModelID to the given window so its taskbar button is separate
    /// from other windows of the same process. Fails silently (logging) on any error.
    /// </summary>
    internal static void SetAppUserModelID(Window window, string appUserModelId)
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
                return;

            if (SHGetPropertyStoreForWindow(hwnd, ref IID_IPropertyStore, out IPropertyStore store) != 0 || store == null)
                return;

            try
            {
                if (InitPropVariantFromString(appUserModelId, out PROPVARIANT pv) != 0)
                    return;

                try
                {
                    PROPERTYKEY key = new()
                    {
                        fmtid = PKEY_AppUserModelID_FmtId,
                        pid = PKEY_AppUserModelID_Pid
                    };
                    store.SetValue(ref key, ref pv);
                    store.Commit();
                }
                finally
                {
                    PropVariantClear(ref pv);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(store);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to set AppUserModelID on taskbar button window");
        }
    }
}

/// <summary>
/// Programmatically drawn icons for the taskbar action buttons (no extra dependencies).
/// </summary>
internal static class TaskbarIcons
{
    /// <summary>
    /// 64x64 icon: a circle split into a light half and a dark half (theme toggle).
    /// </summary>
    internal static BitmapSource CreateThemeIcon()
    {
        var group = new DrawingGroup();

        // dark right half: full circle in dark, then overlay the light left half
        group.Children.Add(new GeometryDrawing(
            new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B)),
            null,
            new EllipseGeometry(new Rect(4, 4, 56, 56))));

        var lightHalf = new PathGeometry();
        var figure = new PathFigure
        {
            StartPoint = new Point(32, 4),
            IsClosed = true,
            IsFilled = true
        };
        figure.Segments.Add(new ArcSegment(
            new Point(32, 60), new Size(28, 28), 0, false, SweepDirection.Clockwise, true));
        lightHalf.Figures.Add(figure);
        group.Children.Add(new GeometryDrawing(
            new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3)),
            null,
            lightHalf));

        return Render(group);
    }

    /// <summary>
    /// 64x64 icon: a crescent moon (sleep).
    /// </summary>
    internal static BitmapSource CreateSleepIcon()
    {
        var moon = Geometry.Combine(
            new EllipseGeometry(new Point(42, 31), 17, 17),
            new EllipseGeometry(new Point(30, 25), 13, 13),
            GeometryCombineMode.Exclude,
            null);

        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(
            new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3)),
            new Pen(new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B)), 2),
            moon));

        return Render(group);
    }

    private static BitmapSource Render(DrawingGroup drawing)
    {
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawDrawing(drawing);
        }

        var bitmap = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}