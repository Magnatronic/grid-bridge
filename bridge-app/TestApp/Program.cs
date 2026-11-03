using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;
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

    static readonly object Gate = new();

    public static void Write(string evt, string detail = "")
    {
        string escaped = detail.Replace("\"", "\"\"").ReplaceLineEndings(" ");
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff},{evt},\"{escaped}\"\n";
        lock (Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.AppendAllText(FilePath, line);
        }
    }

    // Only text starting with this is logged, so real conversations on the device stay out of the log.
    public const string TestTextPrefix = "GBTEST";

    public static string Shown(string label, string? text) => text switch
    {
        null => $"{label}=none",
        _ when text.StartsWith(TestTextPrefix, StringComparison.Ordinal) => $"{label}={text}",
        _ => $"{label}_length={text.Length}",
    };
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

static class GridProcesses
{
    public static Process[] Find() => Process.GetProcesses()
        .Where(p => p.ProcessName.Contains("grid", StringComparison.OrdinalIgnoreCase) && p.ProcessName != "GridBridgeTest")
        .ToArray();

    // A normal app can't open an administrator's process, and Windows then also hides that process's
    // input from a normal app's keyboard hook. So "denied" warns the hook may miss Grid 3's keys.
    public static string Describe()
    {
        var parts = Find().Select(p =>
        {
            string access;
            try
            {
                _ = p.Handle;
                access = "open";
            }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException)
            {
                access = "denied";
            }
            return $"{p.ProcessName}:{access}";
        });
        return $"grid_processes=[{string.Join(";", parts)}] this_app_admin={Environment.IsPrivilegedProcess}";
    }
}

static class WritingAreaProbe
{
    // Looks through Grid 3's windows for anything whose text Windows can read, which is what watching
    // the writing area live would need. Run off the UI thread so a slow scan can't delay the hotkeys or the hook.
    public static void Run()
    {
        try
        {
            var pids = GridProcesses.Find().Select(p => p.Id).ToHashSet();
            if (pids.Count == 0)
            {
                Log.Write("probe-done", "no grid processes running");
                return;
            }

            var hasText = new OrCondition(
                new PropertyCondition(AutomationElement.IsValuePatternAvailableProperty, true),
                new PropertyCondition(AutomationElement.IsTextPatternAvailableProperty, true));
            int windows = 0, elements = 0;

            foreach (AutomationElement window in AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition))
            {
                if (!pids.Contains(window.Current.ProcessId))
                    continue;
                windows++;
                foreach (AutomationElement e in window.FindAll(TreeScope.Descendants, hasText))
                {
                    elements++;
                    var c = e.Current;
                    Log.Write("probe-element",
                        $"type={c.ControlType.ProgrammaticName} id={c.AutomationId} class={c.ClassName} " +
                        $"{Log.Shown("name", c.Name)} {ReadText(e)}");
                }
            }
            Log.Write("probe-done", $"windows={windows} text_elements={elements}");
        }
        catch (Exception e)
        {
            Log.Write("probe-failed", $"{e.GetType().Name}: {e.Message}");
        }
    }

    static string ReadText(AutomationElement e)
    {
        try
        {
            if (e.TryGetCurrentPattern(ValuePattern.Pattern, out object? value))
                return Log.Shown("value", ((ValuePattern)value).Current.Value);
            if (e.TryGetCurrentPattern(TextPattern.Pattern, out object? text))
                return Log.Shown("text", ((TextPattern)text).DocumentRange.GetText(2000));
            return "text=unreadable";
        }
        catch (Exception ex)
        {
            return $"text_read_failed={ex.GetType().Name}";
        }
    }
}

sealed class TrayApp : ApplicationContext
{
    const int TestHotkeyCount = 4;
    const int LiveHotkeyId = 4;
    const int ProbeHotkeyId = 5;
    const long ClipboardRepeatMs = 250;

    readonly NotifyIcon _icon;
    readonly ToolStripMenuItem _counts = new() { Enabled = false };
    readonly ToolStripMenuItem _liveItem = new("Live keys (Ctrl+Alt+F5)");
    readonly MessageWindow _window;
    readonly System.Windows.Forms.Timer _gridCheck = new() { Interval = 60_000 };
    readonly System.Windows.Forms.Timer _flushKeys = new() { Interval = 500 };
    readonly System.Windows.Forms.Timer _liveTimeout = new() { Interval = 10 * 60_000 };
    readonly List<string> _pendingKeys = new();
    KeyboardHook? _hook;
    int _probeRunning;
    int _hotkeys;
    int _clipboards;
    int _liveKeys;
    string? _lastClipboardText;
    long _lastClipboardAt;

    public TrayApp()
    {
        Log.Write("started", $"uptime_s={Environment.TickCount64 / 1000} {GridProcesses.Describe()}");

        _window = new MessageWindow(OnHotkey, OnClipboard);
        for (int i = 0; i < TestHotkeyCount; i++)
            RegisterHotkey(i, Native.VkF1 + i, $"Ctrl+Alt+F{i + 1}");
        RegisterHotkey(LiveHotkeyId, Native.VkF5, "Ctrl+Alt+F5 live keys");
        RegisterHotkey(ProbeHotkeyId, Native.VkF6, "Ctrl+Alt+F6 writing area check");
        bool listening = Native.AddClipboardFormatListener(_window.Handle);
        Log.Write(listening ? "clipboard-listening" : "clipboard-listen-failed");

        var startup = new ToolStripMenuItem("Start at login") { CheckOnClick = true, Checked = Startup.IsOn() };
        startup.Click += (_, _) => Startup.Set(startup.Checked);
        _liveItem.Click += (_, _) => SetLive(_hook is null);
        var menu = new ContextMenuStrip();
        menu.Items.Add(_counts);
        menu.Items.Add(_liveItem);
        menu.Items.Add("Check Grid 3 writing area (Ctrl+Alt+F6)", null, (_, _) => StartProbe());
        menu.Items.Add(startup);
        menu.Items.Add("Open log", null, (_, _) => Process.Start(new ProcessStartInfo(Log.FilePath) { UseShellExecute = true }));
        menu.Items.Add("Exit", null, (_, _) => Quit());

        _icon = new NotifyIcon { Icon = SystemIcons.Application, ContextMenuStrip = menu, Visible = true };
        UpdateCounts();

        // Grid 3 may open after this app does, so one check at launch doesn't show whether both are running.
        _gridCheck.Tick += (_, _) =>
        {
            _gridCheck.Stop();
            Log.Write("grid-check-60s", GridProcesses.Describe());
        };
        _gridCheck.Start();

        // The hook must return fast or Windows drops it, so keys are written to the log in batches.
        _flushKeys.Tick += (_, _) => FlushKeys();
        _liveTimeout.Tick += (_, _) => SetLive(false, "timeout");
    }

    void RegisterHotkey(int id, int vk, string label)
    {
        bool ok = Native.RegisterHotKey(_window.Handle, id, Native.ModControl | Native.ModAlt | Native.ModNoRepeat, vk);
        Log.Write(ok ? "hotkey-registered" : "hotkey-register-failed", label);
    }

    void OnHotkey(int id)
    {
        if (id == LiveHotkeyId)
        {
            SetLive(_hook is null);
            return;
        }
        if (id == ProbeHotkeyId)
        {
            StartProbe();
            return;
        }

        _hotkeys++;
        Log.Write("hotkey", $"Ctrl+Alt+F{id + 1} count={_hotkeys}");
        UpdateCounts();
    }

    void SetLive(bool on, string reason = "")
    {
        if (on == _hook is not null)
            return;
        if (on)
        {
            _liveKeys = 0;
            _hook = new KeyboardHook(OnKey);
            if (!_hook.Installed)
            {
                Log.Write("live-hook-failed", $"win32_error={Marshal.GetLastPInvokeError()}");
                _hook = null;
                return;
            }
            Log.Write("live-on", GridProcesses.Describe());
            _flushKeys.Start();
            _liveTimeout.Start();
        }
        else
        {
            _hook!.Dispose();
            _hook = null;
            _liveTimeout.Stop();
            _flushKeys.Stop();
            FlushKeys();
            Log.Write("live-off", $"keys={_liveKeys} {reason}".Trim());
        }
        _liveItem.Checked = on;
        UpdateCounts();
    }

    void OnKey(int vk, bool injected)
    {
        _liveKeys++;
        _pendingKeys.Add($"{(Keys)vk} injected={injected} count={_liveKeys}");
    }

    void FlushKeys()
    {
        foreach (string key in _pendingKeys)
            Log.Write("live-key", key);
        _pendingKeys.Clear();
        UpdateCounts();
    }

    void StartProbe()
    {
        if (Interlocked.Exchange(ref _probeRunning, 1) == 1)
            return;
        Log.Write("probe-start", GridProcesses.Describe());
        Task.Run(() =>
        {
            WritingAreaProbe.Run();
            Interlocked.Exchange(ref _probeRunning, 0);
        });
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
        Log.Write("clipboard", $"{Log.Shown("text", text)} count={_clipboards}");
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
        string live = _hook is null ? "" : $" · LIVE {_liveKeys}";
        string summary = $"Hotkeys {_hotkeys} · Clipboard {_clipboards}{live}";
        _counts.Text = summary;
        _icon.Text = $"Grid Bridge test: {summary}";
    }

    void Quit()
    {
        SetLive(false, "exit");
        Log.Write("exit");
        _gridCheck.Stop();
        Native.RemoveClipboardFormatListener(_window.Handle);
        for (int id = 0; id <= ProbeHotkeyId; id++)
            Native.UnregisterHotKey(_window.Handle, id);
        _icon.Visible = false;
        _window.DestroyHandle();
        ExitThread();
    }
}

sealed class KeyboardHook : IDisposable
{
    const int WhKeyboardLl = 13;
    const int WmKeyDown = 0x0100;
    const int WmSysKeyDown = 0x0104;
    const uint LlkhfInjected = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    struct KbdLlHookStruct
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, HookProc proc, IntPtr hMod, uint threadId);

    [DllImport("user32.dll")]
    static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    static extern IntPtr GetModuleHandle(string? name);

    readonly Action<int, bool> _onKey;
    readonly HookProc _proc; // Held in a field so the garbage collector can't free it while Windows still calls it.
    IntPtr _hook;

    public KeyboardHook(Action<int, bool> onKey)
    {
        _onKey = onKey;
        _proc = Callback;
        _hook = SetWindowsHookEx(WhKeyboardLl, _proc, GetModuleHandle(null), 0);
    }

    public bool Installed => _hook != IntPtr.Zero;

    IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && (wParam == WmKeyDown || wParam == WmSysKeyDown))
        {
            var data = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            _onKey((int)data.VkCode, (data.Flags & LlkhfInjected) != 0);
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
            UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
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
    public const int VkF5 = 0x74;
    public const int VkF6 = 0x75;

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
