using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
namespace OllamaDesktop.App;

internal static class WindowBounds
{
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left,Top,Right,Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor,Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window,uint flags);
    [DllImport("user32.dll",CharSet=CharSet.Auto)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(nint monitor,ref MonitorInfo info);
    public static void Fit(Window window)
    {
        var handle=new WindowInteropHelper(window).Handle;if(handle==0)return;
        var info=new MonitorInfo{Size=Marshal.SizeOf<MonitorInfo>()};if(!GetMonitorInfo(MonitorFromWindow(handle,2),ref info))return;
        var dpi=VisualTreeHelper.GetDpi(window);var margin=window.WindowState==WindowState.Maximized?0:16;
        var width=Math.Max(240,(info.Work.Right-info.Work.Left)/dpi.DpiScaleX-margin);
        var height=Math.Max(200,(info.Work.Bottom-info.Work.Top)/dpi.DpiScaleY-margin);
        window.MinWidth=Math.Min(window.MinWidth,width);window.MinHeight=Math.Min(window.MinHeight,height);
        window.MaxWidth=Math.Min(window.MaxWidth,width);window.MaxHeight=Math.Min(window.MaxHeight,height);
        if(window.WindowState!=WindowState.Normal)return;
        if(double.IsFinite(window.Width))window.Width=Math.Min(window.Width,width);
        if(double.IsFinite(window.Height))window.Height=Math.Min(window.Height,height);
    }
}
