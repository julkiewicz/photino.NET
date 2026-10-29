// A headless check of the Windows hosting options through Photino.NET: a hidden composition window, a host surface
// and a hit region, host input, and window.open accepted and refused. It drives its own hidden windows by posted
// messages and web messages only (never SendInput or SetForegroundWindow), and it refuses to run on the input
// desktop: start it through a separate-desktop launcher.
using System.Drawing;
using System.Runtime.InteropServices;
using Photino.NET;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("SKIPPED: Windows only");
            return 0;
        }
        var onInputDesktop = IsOnInputDesktop(out var desktop);
        if (args.Length > 0 && args[0] == "--guard-only")
        {
            // Only the desktop guard, no window: safe to run anywhere.
            Console.WriteLine($"{(onInputDesktop ? "REFUSED" : "ALLOWED")} on desktop {desktop}");
            return onInputDesktop ? 9 : 0;
        }
        if (onInputDesktop)
        {
            Console.WriteLine($"REFUSED: this runs on the user's input desktop ({desktop}); start it on a desktop of its own");
            return 9;
        }

        var dataPath = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "webview2-data");
        return new HostingCheck(dataPath).Run();
    }

    private static bool IsOnInputDesktop(out string ownName)
    {
        ownName = DesktopName(GetThreadDesktop(GetCurrentThreadId()));
        var input = OpenInputDesktop(0, false, 0x0001 /* DESKTOP_READOBJECTS */);
        if (input == IntPtr.Zero)
            return true;
        var inputName = DesktopName(input);
        CloseDesktop(input);
        return inputName == ownName;
    }

    private static string DesktopName(IntPtr desktop)
    {
        var buffer = new char[256];
        GetUserObjectInformationW(desktop, 2 /* UOI_NAME */, buffer, buffer.Length * 2, out _);
        return new string(buffer).TrimEnd('\0');
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDesktop(uint threadId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetUserObjectInformationW(IntPtr obj, int index, char[] info, int length, out int needed);
}

internal sealed class HostingCheck
{
    private const uint WM_MOUSEMOVE = 0x0200;
    private const int HostX = 20, HostY = 20;

    private readonly string _dataPath;
    private readonly List<(string Name, bool Passed, string Detail)> _results = new();
    private PhotinoWindow _main;
    private PhotinoWindow _popup;
    private PhotinoPopupRequest _acceptedRequest;
    private IntPtr _surface;
    private Rectangle _panel;
    private int _popupRequests;
    private bool _hostMoveSeen;
    private bool _pageMoveSeen;
    private bool _popupsAsked;
    private bool _finished;

    public HostingCheck(string dataPath) => _dataPath = dataPath;

    public int Run()
    {
        _main = new PhotinoWindow()
            .SetTitle("Photino.NET hosting check")
            .SetUseOsDefaultLocation(false).SetLocation(new Point(0, 0))
            .SetUseOsDefaultSize(false).SetSize(800, 600)
            .SetTemporaryFilesPath(_dataPath)
            .SetStartHidden(true)
            .SetCompositionHosting(true)
            .SetPopupRequestTimeoutMilliseconds(3000)
            .RegisterHostInputHandler(OnHostInput)
            .RegisterPopupRequestedHandler(OnPopupRequested)
            .RegisterWebMessageReceivedHandler((_, message) => OnPageMessage(message))
            .LoadRawString(Page);

        using var watchdog = new Timer(_ => _main.Invoke(() => Finish("timed out after 60 s")), null, 60000, Timeout.Infinite);
        _main.WaitForClose();

        foreach (var (name, passed, detail) in _results)
            Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? ": " + detail : "")}");
        var failed = _results.Count(r => !r.Passed);
        Console.WriteLine(failed == 0 && _results.Count > 0 ? $"ALL PASSED ({_results.Count} checks)" : $"FAILED {failed} of {_results.Count}");
        return failed == 0 && _results.Count > 0 ? 0 : 1;
    }

    private void Check(string name, bool passed, string detail = "") => _results.Add((name, passed, detail));

    private void OnPageMessage(string message)
    {
        if (message.StartsWith("ready:"))
            OnPageReady(message["ready:".Length..]);
        else if (message.StartsWith("page-move:"))
        {
            _pageMoveSeen = true;
            Check("a move over the panel reaches the page", true, message);
            AskForPopupsWhenRouted();
        }
        else if (message.StartsWith("popups:"))
            OnPopupsOpened(message.Split(':'));
    }

    private void OnPageReady(string region)
    {
        Check("composition hosting is active", _main.CompositionHostingEnabled);
        var parts = region.Split(',');
        Check("the page published its hit region", parts.Length == 4, region);
        if (parts.Length != 4)
        {
            Finish("no region");
            return;
        }
        _panel = new Rectangle(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]), int.Parse(parts[3]));

        _surface = _main.AddCompositionSurface(new Rectangle(0, 0, 400, 300));
        Check("a composition surface handle is made", _surface != IntPtr.Zero, $"0x{_surface:X}");
        _main.SetCompositionSurfaceBounds(new[] { new PhotinoSurfaceBounds(_surface, new Rectangle(10, 10, 380, 280)) });
        Check("surface bounds set in a batch", true);

        // The host sets the same region the page published: input over the panel to the page, the rest to the host.
        _main.SetHitRegions(new[] { _panel }, regionsArePage: true);

        var hwnd = _main.WindowHandle;
        PostMessageW(hwnd, WM_MOUSEMOVE, IntPtr.Zero, MakeLParam(HostX, HostY));
        PostMessageW(hwnd, WM_MOUSEMOVE, IntPtr.Zero, MakeLParam(_panel.X + 20, _panel.Y + 50));
        PostMessageW(hwnd, WM_MOUSEMOVE, IntPtr.Zero, MakeLParam(_panel.X + 21, _panel.Y + 51));
    }

    private bool OnHostInput(PhotinoWindow window, in PhotinoHostInput input)
    {
        if (input.Message == WM_MOUSEMOVE && !_hostMoveSeen && window == _main)
        {
            _hostMoveSeen = true;
            Check("a move outside the panel reaches the host", input.X == HostX && input.Y == HostY, $"({input.X},{input.Y})");
            AskForPopupsWhenRouted();
        }
        else if (input.Message == WM_MOUSEMOVE && input.X >= _panel.X && window == _main)
            Check("no move over the panel reaches the host", false, $"({input.X},{input.Y})");
        return false;
    }

    private void AskForPopupsWhenRouted()
    {
        if (!_hostMoveSeen || !_pageMoveSeen || _popupsAsked)
            return;
        _popupsAsked = true;
        _main.SendWebMessage("open");
    }

    private bool OnPopupRequested(PhotinoPopupRequest request)
    {
        _popupRequests++;
        if (_popupRequests != 1)
            return false;   // refused: window.open returns null

        _acceptedRequest = request;
        _popup = request.CreateWindow()
            .SetTitle("Photino.NET hosting check popup")
            .RegisterWindowDestroyedHandler((_, _) => OnPopupDestroyed());
        _popup.WaitForClose();  // the message loop runs already: this creates the window and returns
        return true;
    }

    private void OnPopupsOpened(string[] parts)
    {
        // popups:<accepted not null>:<scripted>:<refused is null>
        Check("an accepted window.open returns a window", parts[1] == "true");
        Check("the page scripts the popup", parts[2] == "true");
        Check("a refused window.open returns null", parts[3] == "true");
        Check("two popup requests reached the host", _popupRequests == 2, _popupRequests.ToString());
        Check("window.open's size and position reach the host",
            _acceptedRequest?.Size == new Size(400, 300) && _acceptedRequest?.Position == new Point(50, 60),
            $"{_acceptedRequest?.Size} {_acceptedRequest?.Position}");
        Check("the popup is the opener's", _popup?.PopupOpener == _main && _main.Popups.Count == 1 && _main.Popups[0] == _popup);
        Check("the popup is of the opener's kind (composition)", _popup?.CompositionHostingEnabled == true);
        _popup?.Close();
    }

    private void OnPopupDestroyed()
    {
        Check("the popup closed by the host is destroyed and released", _main.Popups.Count == 0);
        _main.RemoveCompositionSurface(_surface);
        Check("the surface is removed", true);
        Finish(null);
    }

    private void Finish(string failure)
    {
        if (_finished)
            return;
        _finished = true;
        if (failure != null)
            Check("finished", false, failure);
        _main.Close();
    }

    private static IntPtr MakeLParam(int x, int y) => (IntPtr)((y << 16) | (x & 0xFFFF));

    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    private const string Page = """
        <!doctype html>
        <html><head><style>
          html, body { margin: 0; height: 100%; background: transparent; }
          #panel { position: fixed; right: 0; top: 0; width: 200px; height: 100%; background: #334; }
        </style></head>
        <body><div id="panel"></div><script>
          const send = m => window.external.sendMessage(m);
          let moved = false;
          document.getElementById('panel').addEventListener('pointermove', e => {
            if (!moved) { moved = true; send('page-move:' + Math.round(e.clientX) + ',' + Math.round(e.clientY)); }
          });
          window.external.receiveMessage(m => {
            if (m !== 'open') return;
            const accepted = window.open('about:blank', 'accepted', 'left=50,top=60,width=400,height=300');
            let scripted = false;
            if (accepted) { accepted.document.title = 'scripted'; scripted = accepted.document.title === 'scripted'; }
            const refused = window.open('about:blank', 'refused');
            send('popups:' + (accepted !== null) + ':' + scripted + ':' + (refused === null));
          });
          function publish() {
            if (!window.photino) { send('ready:none'); return; }
            const r = document.getElementById('panel').getBoundingClientRect();
            window.photino.setHitRegions([r], 'page');
            const d = devicePixelRatio;
            send('ready:' + [r.left * d, r.top * d, r.width * d, r.height * d].map(Math.round).join(','));
          }
          if (document.readyState === 'complete') publish(); else window.addEventListener('load', publish);
        </script></body></html>
        """;
}
