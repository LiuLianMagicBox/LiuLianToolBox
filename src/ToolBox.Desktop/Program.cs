using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LiuLian.ToolBox.Desktop;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var background = args.Contains("--background", StringComparer.OrdinalIgnoreCase);
        using var openWindow = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\LiuLianToolBox.OpenWindow");
        using var instance = new Mutex(true, @"Local\LiuLianToolBox.Desktop", out var first);
        if (!first)
        {
            if (!background) openWindow.Set();
            return;
        }
        var text = new Catalog(null);
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LiuLianToolBox", "settings.json");
        try
        {
            var store = new SettingsStore(path);
            using var form = new MainForm(store, store.Load(), openWindow);
            form.FormClosed += (_, _) => Application.ExitThread();
            form.Start(background);
            Application.Run();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            MessageBox.Show(text["SettingsError"], "LiuLianToolBox", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

internal static class WindowsEnvironment
{
    public static string Protect(string token) => Convert.ToBase64String(ProtectedData.Protect(
        Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser));
    public static string Unprotect(string value) => Encoding.UTF8.GetString(ProtectedData.Unprotect(
        Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser));

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInput { public uint Size, Time; }
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInput value);
    private delegate bool WindowCallback(nint handle, nint data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, nint data);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint handle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint handle, StringBuilder value, int length);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint handle, out int id);

    public static long IdleSeconds()
    {
        var input = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };
        return GetLastInputInfo(ref input) ? unchecked((uint)Environment.TickCount - input.Time) / 1000 : 0;
    }

    public static List<ShareSource> Sources(Catalog text)
    {
        var sources = Screen.AllScreens.Select(s => new ShareSource("screen:" + s.DeviceName,
            text.Format("Screen", s.DeviceName))).ToList();
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle)) return true;
            var title = new StringBuilder(256);
            if (GetWindowText(handle, title, title.Capacity) == 0) return true;
            GetWindowThreadProcessId(handle, out var pid);
            if (pid == Environment.ProcessId || pid <= 0) return true;
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(pid);
                var key = $"window:{pid}:{process.StartTime.ToUniversalTime().Ticks}:{handle}";
                sources.Add(new(key, text.Format("Window", title)));
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            return true;
        }, 0);
        return sources;
    }
}
