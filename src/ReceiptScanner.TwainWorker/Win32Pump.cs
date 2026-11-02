using System.Runtime.InteropServices;
using NTwain;

namespace ReceiptScanner.TwainWorker;

/// <summary>
/// A hidden window and message loop. TWAIN sources on Windows deliver their events as window
/// messages, and this worker has no UI framework to provide them.
/// </summary>
internal sealed class Win32Pump : MessageLoopHook
{
    private const string WindowClass = "ReceiptScannerTwainPump";
    private const uint RunQueuedMessage = 0x8001; // WM_APP + 1

    // Held in a field so the garbage collector can't collect the delegate while Windows uses it.
    private readonly WndProc _wndProc;
    private readonly Queue<Action> _queue = new();
    private IWinMessageFilter? _filter;

    public Win32Pump()
    {
        _wndProc = HandleMessage;

        var moduleHandle = GetModuleHandleW(null);
        var windowClass = new WNDCLASSW
        {
            lpszClassName = WindowClass,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = moduleHandle,
        };
        RegisterClassW(ref windowClass);

        var handle = CreateWindowExW(0, WindowClass, "", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, moduleHandle, IntPtr.Zero);
        if (handle == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Couldn't create the message window (error {Marshal.GetLastWin32Error()}).");
        }

        WindowHandle = handle;
        Handle = handle;
    }

    /// <summary>Handle given to TWAIN as the parent of any window the driver opens.</summary>
    public IntPtr WindowHandle { get; }

    /// <summary>All TWAIN work happens on the pump thread, so this runs inline.</summary>
    public override void Invoke(Action action) => action();

    /// <summary>
    /// Queues the action rather than running it inside the message filter. TWAIN's transfer loop
    /// runs through here, and it can block for as long as the receipt takes to feed.
    /// </summary>
    public override void BeginInvoke(Action action)
    {
        lock (_queue)
        {
            _queue.Enqueue(action);
        }

        PostMessageW(WindowHandle, RunQueuedMessage, IntPtr.Zero, IntPtr.Zero);
    }

    protected override void Start(IWinMessageFilter filter) => _filter = filter;

    protected override void Stop() => _filter = null;

    /// <summary>Runs until <see cref="Quit"/> is called on this thread.</summary>
    public void Run()
    {
        while (GetMessageW(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            if (_filter?.IsTwainMessage(message.hwnd, (int)message.message, message.wParam, message.lParam) == true)
            {
                continue;
            }

            TranslateMessage(ref message);
            DispatchMessageW(ref message);
        }
    }

    public void Quit() => PostQuitMessage(0);

    private IntPtr HandleMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg != RunQueuedMessage)
        {
            return DefWindowProcW(hwnd, msg, wParam, lParam);
        }

        while (true)
        {
            Action action;
            lock (_queue)
            {
                if (_queue.Count == 0)
                {
                    return IntPtr.Zero;
                }

                action = _queue.Dequeue();
            }

            action();
        }
    }

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSW
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassW(ref WNDCLASSW windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out MSG message, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref MSG message);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? moduleName);
}
