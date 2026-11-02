using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace GridBridge.TestApp;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args is ["--startup", "on" or "off"])
        {
            Startup.Set(args[1] == "on");
            return 0;
        }

        // A login start plus a manual start would otherwise fight over the hotkeys.
        using var mutex = new Mutex(true, @"Local\GridBridgeTest", out bool isFirst);
        if (!isFirst)
        {
            Log.Write("second-instance-ignored");
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp());
        return 0;
    }
}

static class Log
{
    public static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GridBridgeTest", "log.csv");

    public static void Write(string evt, string detail = "")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string escaped = detail.Replace("\"", "\"\"").ReplaceLineEndings(" ");
        File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff},{evt},\"{escaped}\"\n");
    }
}

static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "GridBridgeTest";

    public static bool IsOn()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is not null;
    }

    public static void Set(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on)
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        Log.Write("startup-setting", on ? "on" : "off");
    }
}

sealed class TrayApp : ApplicationContext
{
    // Only text starting with this is logged, so real conversations on the device stay out of the log.
    const string TestTextPrefix = "GBTEST";
    const int HotkeyCount = 4;
    const long ClipboardRepeatMs = 250;

    readonly NotifyIcon _icon;
    readonly ToolStripMenuItem _counts = new() { Enabled = false };
    readonly MessageWindow _window;
    readonly System.Windows.Forms.Timer _gridCheck = new() { Interval = 60_000 };
    int _hotkeys;
    int _clipboards;
    string? _lastClipboardText;
    long _lastClipboardAt;

    public TrayApp()
    {
        Log.Write("started", $"uptime_s={Environment.TickCount64 / 1000} {GridProcesses()}");

        _window = new MessageWindow(OnHotkey, OnClipboard);
        for (int i = 0; i < HotkeyCount; i++)
        {
            bool ok = Native.RegisterHotKey(_window.Handle, i, Native.ModControl | Native.ModAlt | Native.ModNoRepeat, Native.VkF1 + i);
            Log.Write(ok ? "hotkey-registered" : "hotkey-register-failed", $"Ctrl+Alt+F{i + 1}");
        }
        bool listening = Native.AddClipboardFormatListener(_window.Handle);
        Log.Write(listening ? "clipboard-listening" : "clipboard-listen-failed");

        var startup = new ToolStripMenuItem("Start at login") { CheckOnClick = true, Checked = Startup.IsOn() };
        startup.Click += (_, _) => Startup.Set(startup.Checked);
        var menu = new ContextMenuStrip();
        menu.Items.Add(_counts);
        menu.Items.Add(startup);
        menu.Items.Add("Open log", null, (_, _) => Process.Start(new ProcessStartInfo(Log.FilePath) { UseShellExecute = true }));
        menu.Items.Add("Exit", null, (_, _) => Quit());

        _icon = new NotifyIcon { Icon = SystemIcons.Application, ContextMenuStrip = menu, Visible = true };
        UpdateCounts();

        // Grid 3 may open after this app does, so one check at launch doesn't show whether both are running.
        _gridCheck.Tick += (_, _) =>
        {
            _gridCheck.Stop();
            Log.Write("grid-check-60s", GridProcesses());
        };
        _gridCheck.Start();
    }

    void OnHotkey(int id)
    {
        _hotkeys++;
        Log.Write("hotkey", $"Ctrl+Alt+F{id + 1} count={_hotkeys}");
        UpdateCounts();
    }

    void OnClipboard()
    {
        string? text = ReadClipboardText(out string? error);
        if (error is not null)
        {
            Log.Write("clipboard-read-failed", error);
            return;
        }
        if (text is null)
        {
            Log.Write("clipboard-not-text");
            return;
        }

        // Windows can report one copy several times, which would break the 50 out of 50 count.
        long now = Environment.TickCount64;
        bool repeat = text == _lastClipboardText && now - _lastClipboardAt < ClipboardRepeatMs;
        _lastClipboardText = text;
        _lastClipboardAt = now;
        if (repeat)
            return;

        _clipboards++;
        string shown = text.StartsWith(TestTextPrefix, StringComparison.Ordinal) ? $"text={text}" : $"length={text.Length}";
        Log.Write("clipboard", $"{shown} count={_clipboards}");
        UpdateCounts();
    }

    static string? ReadClipboardText(out string? error)
    {
        error = null;
        // The app that just copied can still hold the clipboard open, so a first failure is normal.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return Clipboard.ContainsText() ? Clipboard.GetText() : null;
            }
            catch (ExternalException) when (attempt < 5)
            {
                Thread.Sleep(20);
            }
            catch (ExternalException e)
            {
                error = e.Message;
                return null;
            }
        }
    }

    void UpdateCounts()
    {
        string summary = $"Hotkeys {_hotkeys} · Clipboard {_clipboards}";
        _counts.Text = summary;
        _icon.Text = $"Grid Bridge test: {summary}";
    }

    static string GridProcesses()
    {
        var names = Process.GetProcesses()
            .Select(p => p.ProcessName)
            .Where(n => n.Contains("grid", StringComparison.OrdinalIgnoreCase) && n != "GridBridgeTest")
            .Distinct();
        return $"grid_processes=[{string.Join(";", names)}]";
    }

    void Quit()
    {
        Log.Write("exit");
        _gridCheck.Stop();
        Native.RemoveClipboardFormatListener(_window.Handle);
        for (int i = 0; i < HotkeyCount; i++)
            Native.UnregisterHotKey(_window.Handle, i);
        _icon.Visible = false;
        _window.DestroyHandle();
        ExitThread();
    }
}

sealed class MessageWindow : NativeWindow
{
    const int WmHotkey = 0x0312;
    const int WmClipboardUpdate = 0x031D;

    readonly Action<int> _onHotkey;
    readonly Action _onClipboard;

    public MessageWindow(Action<int> onHotkey, Action onClipboard)
    {
        _onHotkey = onHotkey;
        _onClipboard = onClipboard;
        CreateHandle(new CreateParams());
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey)
            _onHotkey((int)m.WParam);
        else if (m.Msg == WmClipboardUpdate)
            _onClipboard();
        base.WndProc(ref m);
    }
}

static partial class Native
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModNoRepeat = 0x4000;
    public const int VkF1 = 0x70;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, int vk);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(IntPtr hWnd, int id);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AddClipboardFormatListener(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RemoveClipboardFormatListener(IntPtr hWnd);
}
