using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("This prototype currently supports only macOS and Windows for global volume key capture.");
    return;
}

var config = HookConfig.FromArgs(args);
using var oscClient = new OscClient(config.OscHost, config.OscPort);
using var hook = GlobalVolumeHookFactory.Create();

hook.VolumeKeyPressed += (_, key) =>
{
    var (address, value) = key switch
    {
        VolumeKey.VolumeUp => (config.VolumeUpAddress, config.StepAmount),
        VolumeKey.VolumeDown => (config.VolumeDownAddress, -config.StepAmount),
        VolumeKey.MuteToggle => (config.MuteAddress, 1f),
        _ => ("/volume/unknown", 0f)
    };

    oscClient.SendFloat(address, value);
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {key} => {address} {value}");
};

Console.WriteLine("Global hook active. Press Ctrl+C to exit.");
if (OperatingSystem.IsMacOS())
{
    Console.WriteLine("macOS requires Accessibility permission for key event taps (System Settings > Privacy & Security > Accessibility).");
}
Console.WriteLine($"Sending OSC to {config.OscHost}:{config.OscPort}");
Console.WriteLine($"Mappings: UP={config.VolumeUpAddress} DOWN={config.VolumeDownAddress} MUTE={config.MuteAddress}");

hook.Start();

internal static class GlobalVolumeHookFactory
{
    public static IGlobalVolumeHook Create()
    {
        if (OperatingSystem.IsMacOS())
        {
            return new MacGlobalKeyHook();
        }

        if (OperatingSystem.IsWindows())
        {
            return new WindowsGlobalKeyHook();
        }

        throw new PlatformNotSupportedException("Only macOS and Windows are supported.");
    }
}

internal interface IGlobalVolumeHook : IDisposable
{
    event EventHandler<VolumeKey>? VolumeKeyPressed;
    void Start();
}

internal sealed record HookConfig(
    string OscHost,
    int OscPort,
    string VolumeUpAddress,
    string VolumeDownAddress,
    string MuteAddress,
    float StepAmount)
{
    public static HookConfig FromArgs(string[] args)
    {
        var options = args
            .Select(arg => arg.Split('=', count: 2, StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0].TrimStart('-'), parts => parts[1], StringComparer.OrdinalIgnoreCase);

        return new HookConfig(
            OscHost: Get(options, "host", "127.0.0.1"),
            OscPort: ParseInt(Get(options, "port", "9000"), 9000),
            VolumeUpAddress: Get(options, "up", "/volume/up"),
            VolumeDownAddress: Get(options, "down", "/volume/down"),
            MuteAddress: Get(options, "mute", "/volume/mute"),
            StepAmount: ParseFloat(Get(options, "step", "0.05"), 0.05f));
    }

    private static string Get(IReadOnlyDictionary<string, string> map, string key, string fallback) =>
        map.TryGetValue(key, out var value) ? value : fallback;

    private static int ParseInt(string value, int fallback) => int.TryParse(value, out var result) ? result : fallback;

    private static float ParseFloat(string value, float fallback) => float.TryParse(value, out var result) ? result : fallback;
}

internal enum VolumeKey
{
    VolumeUp,
    VolumeDown,
    MuteToggle
}

internal sealed class MacGlobalKeyHook : IGlobalVolumeHook
{
    private const int CgSessionEventTap = 1;
    private const int CgHeadInsertEventTap = 0;
    private const int CgEventTapOptionDefault = 0;
    private const int CgEventKeyDown = 10;
    private const int CgKeyboardEventKeycode = 9;

    private const long KvkVolumeUp = 72;
    private const long KvkVolumeDown = 73;
    private const long KvkMute = 74;

    private readonly EventTapCallback _callback;
    private IntPtr _eventTap = IntPtr.Zero;
    private IntPtr _runLoopSource = IntPtr.Zero;

    public event EventHandler<VolumeKey>? VolumeKeyPressed;

    public MacGlobalKeyHook() => _callback = OnEvent;

    public void Start()
    {
        var mask = 1UL << CgEventKeyDown;
        _eventTap = CGEventTapCreate(CgSessionEventTap, CgHeadInsertEventTap, CgEventTapOptionDefault, mask, _callback, IntPtr.Zero);
        if (_eventTap == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to create CGEvent tap. Ensure Accessibility permissions are granted.");
        }

        _runLoopSource = CFMachPortCreateRunLoopSource(IntPtr.Zero, _eventTap, IntPtr.Zero);
        if (_runLoopSource == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to create run-loop source for event tap.");
        }

        var runLoop = CFRunLoopGetCurrent();
        CFRunLoopAddSource(runLoop, _runLoopSource, kCFRunLoopCommonModes);
        CGEventTapEnable(_eventTap, true);

        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            CFRunLoopStop(runLoop);
        };

        CFRunLoopRun();
    }

    private IntPtr OnEvent(IntPtr proxy, int type, IntPtr cgEvent, IntPtr userInfo)
    {
        if (type == CgEventKeyDown)
        {
            var keyCode = CGEventGetIntegerValueField(cgEvent, CgKeyboardEventKeycode);
            switch (keyCode)
            {
                case KvkVolumeUp:
                    VolumeKeyPressed?.Invoke(this, VolumeKey.VolumeUp);
                    break;
                case KvkVolumeDown:
                    VolumeKeyPressed?.Invoke(this, VolumeKey.VolumeDown);
                    break;
                case KvkMute:
                    VolumeKeyPressed?.Invoke(this, VolumeKey.MuteToggle);
                    break;
            }
        }

        return cgEvent;
    }

    public void Dispose()
    {
        if (_eventTap != IntPtr.Zero)
        {
            CFRelease(_eventTap);
            _eventTap = IntPtr.Zero;
        }

        if (_runLoopSource != IntPtr.Zero)
        {
            CFRelease(_runLoopSource);
            _runLoopSource = IntPtr.Zero;
        }

        GC.SuppressFinalize(this);
    }

    private delegate IntPtr EventTapCallback(IntPtr proxy, int type, IntPtr cgEvent, IntPtr userInfo);

    [DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
    private static extern IntPtr CGEventTapCreate(int tap, int place, int options, ulong eventsOfInterest, EventTapCallback callback, IntPtr userInfo);

    [DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
    private static extern void CGEventTapEnable(IntPtr tap, bool enable);

    [DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
    private static extern long CGEventGetIntegerValueField(IntPtr cgEvent, int field);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFMachPortCreateRunLoopSource(IntPtr allocator, IntPtr port, IntPtr order);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFRunLoopGetCurrent();

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRunLoopAddSource(IntPtr runLoop, IntPtr source, IntPtr mode);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRunLoopRun();

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRunLoopStop(IntPtr runLoop);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRelease(IntPtr cfTypeRef);

    private static readonly IntPtr kCFRunLoopCommonModes = GetCommonModes();

    private static IntPtr GetCommonModes()
    {
        var handle = NativeLibrary.Load("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation");
        return NativeLibrary.GetExport(handle, "kCFRunLoopCommonModes");
    }
}

internal sealed class WindowsGlobalKeyHook : IGlobalVolumeHook
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int VkVolumeMute = 0xAD;
    private const int VkVolumeDown = 0xAE;
    private const int VkVolumeUp = 0xAF;

    private readonly HookProc _hookProc;
    private nint _hookHandle;

    public event EventHandler<VolumeKey>? VolumeKeyPressed;

    public WindowsGlobalKeyHook() => _hookProc = HookCallback;

    public void Start()
    {
        _hookHandle = SetWindowsHookEx(WhKeyboardLl, _hookProc, GetModuleHandle(null), 0);
        if (_hookHandle == 0)
        {
            throw new InvalidOperationException($"Unable to register keyboard hook. Win32 error: {Marshal.GetLastWin32Error()}");
        }

        var shutdown = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Set();
        };

        shutdown.Wait();
    }

    private nint HookCallback(int code, nuint wParam, nint lParam)
    {
        if (code >= 0 && wParam == WmKeyDown)
        {
            var data = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            switch ((int)data.vkCode)
            {
                case VkVolumeUp:
                    VolumeKeyPressed?.Invoke(this, VolumeKey.VolumeUp);
                    break;
                case VkVolumeDown:
                    VolumeKeyPressed?.Invoke(this, VolumeKey.VolumeDown);
                    break;
                case VkVolumeMute:
                    VolumeKeyPressed?.Invoke(this, VolumeKey.MuteToggle);
                    break;
            }
        }

        return CallNextHookEx(_hookHandle, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hookHandle != 0)
        {
            _ = UnhookWindowsHookEx(_hookHandle);
            _hookHandle = 0;
        }

        GC.SuppressFinalize(this);
    }

    private delegate nint HookProc(int code, nuint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, HookProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nuint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern nint GetModuleHandle(string? lpModuleName);
}

internal sealed class OscClient : IDisposable
{
    private readonly UdpClient _udpClient;
    private readonly IPEndPoint _endpoint;

    public OscClient(string host, int port)
    {
        _udpClient = new UdpClient();
        _endpoint = new IPEndPoint(IPAddress.Parse(host), port);
    }

    public void SendFloat(string address, float value)
    {
        var packet = OscPacket.EncodeFloat(address, value);
        _udpClient.Send(packet, packet.Length, _endpoint);
    }

    public void Dispose()
    {
        _udpClient.Dispose();
        GC.SuppressFinalize(this);
    }
}

internal static class OscPacket
{
    public static byte[] EncodeFloat(string address, float value)
    {
        using var stream = new MemoryStream();
        WritePaddedString(stream, address);
        WritePaddedString(stream, ",f");

        var raw = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(raw);
        }

        stream.Write(raw);
        return stream.ToArray();
    }

    private static void WritePaddedString(Stream stream, string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        stream.Write(bytes);
        stream.WriteByte(0);

        while (stream.Length % 4 != 0)
        {
            stream.WriteByte(0);
        }
    }
}
