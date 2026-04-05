using System.Runtime.InteropServices;

namespace JointVentures.Launcher;

/// <summary>
/// Minimal Win32 window with a read-only log area. No close button.
/// Posts WM_APP+1 to append log lines from any thread.
/// </summary>
internal sealed class NativeWindow : IDisposable
{
    // ── Win32 constants ──
    private const int WS_OVERLAPPED = 0x00000000;
    private const int WS_CAPTION = 0x00C00000;
    private const int WS_SYSMENU = 0x00080000;
    private const int WS_MINIMIZEBOX = 0x00020000;
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_CHILD = 0x40000000;
    private const int WS_VSCROLL = 0x00200000;
    private const int WS_BORDER = 0x00800000;

    private const int WS_EX_CLIENTEDGE = 0x00000200;

    private const int ES_MULTILINE = 0x0004;
    private const int ES_READONLY = 0x0800;
    private const int ES_AUTOVSCROLL = 0x0040;

    private const int WM_DESTROY = 0x0002;
    private const int WM_SETFONT = 0x0030;
    private const int WM_SETTEXT = 0x000C;
    private const int WM_CLOSE = 0x0010;
    private const int EM_SETSEL = 0x00B1;
    private const int EM_REPLACESEL = 0x00C2;
    private const int EM_SCROLLCARET = 0x00B7;

    private const int WM_APP_LOG = 0x0401; // WM_APP + 1

    private const int CW_USEDEFAULT = unchecked((int)0x80000000);

    private const int SC_CLOSE = 0xF060;
    private const int MF_BYCOMMAND = 0x0000;

    // ── P/Invoke ──

    private delegate nint WndProcDelegate(nint hWnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public int cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public nint lpszMenuName;
        public nint lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(
        int dwExStyle, nint lpClassName, string lpWindowName, int dwStyle,
        int x, int y, int nWidth, int nHeight,
        nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool UpdateWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProcW(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint SendMessageW(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint GetSystemMenu(nint hWnd, bool bRevert);

    [DllImport("user32.dll")]
    private static extern bool DeleteMenu(nint hMenu, int nPosition, int nFlags);

    [DllImport("user32.dll")]
    private static extern nint LoadCursorW(nint hInstance, int lpCursorName);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateFontW(
        int cHeight, int cWidth, int cEscapement, int cOrientation, int cWeight,
        uint bItalic, uint bUnderline, uint bStrikeOut, uint iCharSet,
        uint iOutPrecision, uint iClipPrecision, uint iQuality, uint iPitchAndFamily,
        string pszFaceName);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint ho);

    [DllImport("kernel32.dll")]
    private static extern nint GetModuleHandleW(nint lpModuleName);

    // ── State ──

    private nint _hWnd;
    private nint _hEdit;
    private nint _hFont;
    private readonly WndProcDelegate _wndProc; // prevent GC
    private readonly List<string> _pendingLines = [];
    private readonly object _pendingLock = new();

    public nint Handle => _hWnd;

    public NativeWindow()
    {
        _wndProc = WndProc;
    }

    public void Create(string title, int width = 1000, int height = 300)
    {
        var hInstance = GetModuleHandleW(0);
        var className = Marshal.StringToHGlobalUni("JVLauncher\0");

        var wc = new WNDCLASSEXW
        {
            cbSize = Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInstance,
            hCursor = LoadCursorW(0, 32512), // IDC_ARROW
            hbrBackground = 6, // COLOR_WINDOW + 1 (nint 6 = stock brush)
            lpszClassName = className
        };

        RegisterClassExW(ref wc);

        _hWnd = CreateWindowExW(
            0, className,
            title,
            WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX | WS_VISIBLE,
            CW_USEDEFAULT, CW_USEDEFAULT, width, height,
            0, 0, hInstance, 0);

        // Remove close button from system menu
        var hMenu = GetSystemMenu(_hWnd, false);
        if (hMenu != 0)
            DeleteMenu(hMenu, SC_CLOSE, MF_BYCOMMAND);

        // Create edit control (log area)
        var editClass = Marshal.StringToHGlobalUni("EDIT\0");
        _hEdit = CreateWindowExW(
            WS_EX_CLIENTEDGE, editClass, "",
            WS_CHILD | WS_VISIBLE | WS_VSCROLL | ES_MULTILINE | ES_READONLY | ES_AUTOVSCROLL,
            8, 8, width - 32, height - 55,
            _hWnd, 0, hInstance, 0);

        // Monospace font
        _hFont = CreateFontW(
            -14, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 0, 49, // FIXED_PITCH | FF_MODERN
            "Consolas");
        SendMessageW(_hEdit, WM_SETFONT, _hFont, 1);

        Marshal.FreeHGlobal(className);
        Marshal.FreeHGlobal(editClass);
    }

    public void RunMessageLoop()
    {
        while (GetMessageW(out var msg, 0, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
    }

    /// <summary>
    /// Thread-safe: queues a log line and posts a message to the window thread.
    /// </summary>
    public void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}\r\n";
        lock (_pendingLock)
        {
            _pendingLines.Add(line);
        }

        if (_hWnd != 0)
            PostMessageW(_hWnd, WM_APP_LOG, 0, 0);
    }

    /// <summary>
    /// Thread-safe: posts WM_CLOSE to shut down the window from any thread.
    /// </summary>
    public void Close()
    {
        if (_hWnd != 0)
            PostMessageW(_hWnd, WM_CLOSE, 0, 0);
    }

    public void Dispose()
    {
        if (_hFont != 0)
        {
            DeleteObject(_hFont);
            _hFont = 0;
        }
    }

    private nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_APP_LOG:
                FlushPendingLines();
                return 0;

            case WM_CLOSE:
                // Allow close — called by our code when cleanup is done
                PostQuitMessage(0);
                return 0;

            case WM_DESTROY:
                PostQuitMessage(0);
                return 0;

            default:
                return DefWindowProcW(hWnd, msg, wParam, lParam);
        }
    }

    private void FlushPendingLines()
    {
        List<string> lines;
        lock (_pendingLock)
        {
            if (_pendingLines.Count == 0) return;
            lines = [.. _pendingLines];
            _pendingLines.Clear();
        }

        foreach (var line in lines)
        {
            // Move caret to end, then replace selection (appends)
            SendMessageW(_hEdit, EM_SETSEL, -1, -1);
            var ptr = Marshal.StringToHGlobalUni(line);
            SendMessageW(_hEdit, EM_REPLACESEL, 0, ptr);
            Marshal.FreeHGlobal(ptr);
        }

        SendMessageW(_hEdit, EM_SCROLLCARET, 0, 0);
    }
}
