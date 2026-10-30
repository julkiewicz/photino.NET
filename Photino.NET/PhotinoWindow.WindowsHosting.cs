using System.Drawing;
using System.Runtime.InteropServices;

namespace Photino.NET;

// The Windows-only hosting options (Photino_ctorWithOptions_win32). A window that sets none of them is created by
// Photino_ctor exactly as before. Every native call here runs on the window's thread (through Invoke).
public partial class PhotinoWindow
{
    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, SetLastError = true, CharSet = CharSet.Ansi)]
    static extern IntPtr Photino_ctorWithOptions_win32(ref PhotinoNativeParameters parameters, ref PhotinoNativeWindowsOptions options);

    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    static extern void Photino_GetCompositionHostingEnabled_win32(IntPtr instance, out byte enabled);

    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr Photino_AddCompositionSurface_win32(IntPtr instance, int x, int y, int width, int height);

    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    static extern unsafe void Photino_SetCompositionSurfaceBounds_win32(IntPtr instance, PhotinoSurfaceBounds* bounds, int count);

    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    static extern void Photino_RemoveCompositionSurface_win32(IntPtr instance, IntPtr surface);

    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    static extern unsafe void Photino_SetHitRegions_win32(IntPtr instance, int* rects, int count, byte regionsArePage);

    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    static extern void Photino_Show_win32(IntPtr instance);

    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    static extern void Photino_RefusePopupRequest_win32(IntPtr opener, int requestId);

    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    internal static extern unsafe int Photino_GetStructLayout_win32(string name, int* values, int capacity);

    private PhotinoNativeWindowsOptions _windowsOptions;
    private PhotinoHostInputHandler _hostInputHandler;
    private PhotinoPopupRequestedHandler _popupRequestedHandler;
    private PhotinoNavigationStartingHandler _navigationStartingHandler;
    private PhotinoHostDropHandler _hostDropHandler;
    // Made with Photino_ctorWithOptions_win32 (decided at creation; see HasWindowsOptions).
    private bool _hasWindowsOptions;
    // Set when the native window is destroyed: hosting calls after it throw, or do nothing where that is safe.
    private bool _destroyed;
    // The delegates native code calls: kept alive as long as this window.
    private CppHostInputDelegate _cppHostInput;
    private CppPopupRequestedDelegate _cppPopupRequested;
    private CppDestroyedDelegate _cppDestroyed;
    private CppNavigationStartingDelegate _cppNavigationStarting;
    private CppHostDropDelegate _cppHostDrop;
    // Keeps a window made with these options (and its delegates) alive until its native window is destroyed, so a
    // popup the host does not hold on to is not collected while it is open.
    private GCHandle _aliveUntilDestroyed;
    private PhotinoWindow _popupOpener;
    private readonly List<PhotinoWindow> _popups = new();
    private IReadOnlyList<PhotinoWindow> _popupsView;

    /// <summary>
    /// Raised on the window's thread when its native window was destroyed; no callback is made for the window after
    /// it. Windows only, and only for a window made with the Windows hosting options (registering a handler before
    /// creation is itself such an option).
    /// </summary>
    public event EventHandler WindowDestroyed;

    /// <summary>The window whose page opened this one, or null when this window is not a popup.</summary>
    public PhotinoWindow PopupOpener => _popupOpener;

    /// <summary>The popups this window's page opened that are still open. Read it on the window's thread only.</summary>
    public IReadOnlyList<PhotinoWindow> Popups => _popupsView ??= _popups.AsReadOnly();

    /// <summary>Whether the native window was destroyed (windows made with the Windows hosting options only).</summary>
    public bool IsDestroyed => _destroyed;

    // Options are set before creation. A window is made with Photino_ctorWithOptions_win32 when any of them differs
    // from its default (a handler counts, including a WindowDestroyed handler); such a window's own placement,
    // minimize, maximize and restore calls never activate it. Otherwise Photino_ctor makes it exactly as before.
    private bool HasWindowsOptions =>
        _windowsOptions.StartHidden != 0 || _windowsOptions.CompositionHosting != 0
        || _windowsOptions.HitRegionHoldMilliseconds != 0 || _windowsOptions.PopupRequestTimeoutMilliseconds != 0
        || _windowsOptions.BrowserAcceleratorKeysDisabled != 0
        || _hostInputHandler != null || _popupRequestedHandler != null || _navigationStartingHandler != null
        || _hostDropHandler != null
        || WindowDestroyed != null || _popupOpener != null;

    /// <summary>
    /// Windows only, before the window is created: create it without showing it. The page still loads, lays out
    /// and runs, and never takes focus; <see cref="Show"/> shows it. Any Windows hosting option set to a value other
    /// than its default makes the window's own placement, minimize, maximize and restore calls never activate it.
    /// </summary>
    public PhotinoWindow SetStartHidden(bool startHidden)
    {
        ThrowIfCreatedOrNotWindows(nameof(SetStartHidden));
        _windowsOptions.StartHidden = (byte)(startHidden ? 1 : 0);
        return this;
    }

    /// <summary>
    /// Windows only, before the window is created: host WebView2 through its composition controller, the page a
    /// transparent visual above the host's surfaces (<see cref="AddCompositionSurface"/>). Mouse input is routed by
    /// hit regions: until the page (window.photino.setHitRegions) or the host (<see cref="SetHitRegions"/>)
    /// publishes some, every input goes to the host. Falls back to the usual hosting when the runtime has no
    /// composition controller; <see cref="CompositionHostingEnabled"/> says which one is active.
    /// </summary>
    public PhotinoWindow SetCompositionHosting(bool compositionHosting)
    {
        ThrowIfCreatedOrNotWindows(nameof(SetCompositionHosting));
        _windowsOptions.CompositionHosting = (byte)(compositionHosting ? 1 : 0);
        return this;
    }

    /// <summary>
    /// Windows only, before the window is created: after the window's size or DPI changes, a press waits up to this
    /// long for the page to publish its new hit regions. 0 means the default (100 ms), a negative value turns it off.
    /// </summary>
    public PhotinoWindow SetHitRegionHoldMilliseconds(int milliseconds)
    {
        ThrowIfCreatedOrNotWindows(nameof(SetHitRegionHoldMilliseconds));
        _windowsOptions.HitRegionHoldMilliseconds = milliseconds;
        return this;
    }

    /// <summary>
    /// Windows only, before the window is created: an accepted popup request not fulfilled within this is refused.
    /// 0 means the default (5000 ms), a negative value never.
    /// </summary>
    public PhotinoWindow SetPopupRequestTimeoutMilliseconds(int milliseconds)
    {
        ThrowIfCreatedOrNotWindows(nameof(SetPopupRequestTimeoutMilliseconds));
        _windowsOptions.PopupRequestTimeoutMilliseconds = milliseconds;
        return this;
    }

    /// <summary>
    /// Windows only, before the window is created: receives the input composition hosting routes to the host. An
    /// exception it throws is logged and the input treated as not handled.
    /// </summary>
    public PhotinoWindow RegisterHostInputHandler(PhotinoHostInputHandler handler)
    {
        ThrowIfCreatedOrNotWindows(nameof(RegisterHostInputHandler));
        _hostInputHandler = handler;
        return this;
    }

    /// <summary>
    /// Windows only, before the window is created: receives the page's window.open requests. Without one,
    /// WebView2's own behavior is unchanged. An exception it throws is logged and the request refused.
    /// </summary>
    public PhotinoWindow RegisterPopupRequestedHandler(PhotinoPopupRequestedHandler handler)
    {
        ThrowIfCreatedOrNotWindows(nameof(RegisterPopupRequestedHandler));
        _popupRequestedHandler = handler;
        return this;
    }

    /// <summary>
    /// Windows only, before the window is created: asked before every navigation of the page and of its frames, the
    /// first load included; returning false cancels it. The popups the page opens take the same handler. An exception
    /// it throws is logged and the navigation cancelled.
    /// </summary>
    public PhotinoWindow RegisterNavigationStartingHandler(PhotinoNavigationStartingHandler handler)
    {
        ThrowIfCreatedOrNotWindows(nameof(RegisterNavigationStartingHandler));
        _navigationStartingHandler = handler;
        return this;
    }

    /// <summary>
    /// Windows only, before the window is created: drags over the host's area (outside the page's hit regions) in
    /// composition mode are offered to the host first. Without one, the page has drags over the whole window, as with
    /// the usual hosting (composition mode always makes the window its page's drop target). The popups the page opens
    /// take the same handler.
    /// </summary>
    public PhotinoWindow RegisterHostDropHandler(PhotinoHostDropHandler handler)
    {
        ThrowIfCreatedOrNotWindows(nameof(RegisterHostDropHandler));
        _hostDropHandler = handler;
        return this;
    }

    /// <summary>
    /// Windows only, before the window is created: whether the browser's own keys work (reload, find, print, history,
    /// zoom and the like; true by default). Turned off, the page still gets every key, and DevTools' keys follow
    /// <see cref="SetDevToolsEnabled"/>. The popups the page opens take the same setting.
    /// </summary>
    public PhotinoWindow SetBrowserAcceleratorKeysEnabled(bool enabled)
    {
        ThrowIfCreatedOrNotWindows(nameof(SetBrowserAcceleratorKeysEnabled));
        _windowsOptions.BrowserAcceleratorKeysDisabled = (byte)(enabled ? 0 : 1);
        return this;
    }

    /// <summary>
    /// Windows only: registers a handler for <see cref="WindowDestroyed"/>. Before creation it makes the window one
    /// with the Windows hosting options; after creation it is allowed only on such a window.
    /// </summary>
    public PhotinoWindow RegisterWindowDestroyedHandler(EventHandler handler)
    {
        if (!IsWindowsPlatform)
            throw new PlatformNotSupportedException($"{nameof(RegisterWindowDestroyedHandler)} is available on Windows only.");
        if (_nativeInstance != IntPtr.Zero && !_hasWindowsOptions)
            throw new InvalidOperationException("WindowDestroyed is raised only for a window made with the Windows hosting options.");
        WindowDestroyed += handler;
        return this;
    }

    // HOSTING CALLS. Windows only, after creation, on the window's thread only. There is no cross-thread path: a
    // call from another thread into a window closing on its own thread could otherwise wait for good.

    /// <summary>Whether the window is hosted through the composition controller. False before creation and after destruction.</summary>
    public bool CompositionHostingEnabled
    {
        get
        {
            if (!IsWindowsPlatform || _nativeInstance == IntPtr.Zero || _destroyed)
                return false;
            ThrowIfNotWindowThread(nameof(CompositionHostingEnabled));
            Photino_GetCompositionHostingEnabled_win32(_nativeInstance, out var enabled);
            return enabled != 0;
        }
    }

    /// <summary>
    /// Composition hosting, on the window's thread: makes a composition surface handle shown at these client-area
    /// bounds (physical pixels) under the page. The host's renderer makes its swap chain for it
    /// (IDXGIFactoryMedia.CreateSwapChainForCompositionSurfaceHandle) on its own thread. Release it with
    /// <see cref="RemoveCompositionSurface"/> once that swap chain is released. Returns zero on failure.
    /// </summary>
    public IntPtr AddCompositionSurface(Rectangle bounds)
    {
        ThrowIfNotLive(nameof(AddCompositionSurface));
        return Photino_AddCompositionSurface_win32(_nativeInstance, bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }

    /// <summary>Composition hosting, on the window's thread: moves and clips any number of surfaces in one commit.</summary>
    public unsafe void SetCompositionSurfaceBounds(ReadOnlySpan<PhotinoSurfaceBounds> bounds)
    {
        ThrowIfNotLive(nameof(SetCompositionSurfaceBounds));
        fixed (PhotinoSurfaceBounds* first = bounds)
            Photino_SetCompositionSurfaceBounds_win32(_nativeInstance, first, bounds.Length);
    }

    /// <summary>
    /// Composition hosting, on the window's thread: removes a surface and closes its handle. Call it after the swap
    /// chain made for it is released. Does nothing once the window is destroyed (destruction closed its handles).
    /// </summary>
    public void RemoveCompositionSurface(IntPtr surface)
    {
        if (_destroyed)
            return;
        ThrowIfNotLive(nameof(RemoveCompositionSurface));
        Photino_RemoveCompositionSurface_win32(_nativeInstance, surface);
    }

    /// <summary>
    /// Composition hosting, on the window's thread: sets the hit regions in client pixels, replacing any the page
    /// published. When <paramref name="regionsArePage"/> is true, input inside the regions goes to the page and
    /// outside to the host; when false, the other way round.
    /// </summary>
    public unsafe void SetHitRegions(ReadOnlySpan<Rectangle> regions, bool regionsArePage = true)
    {
        ThrowIfNotLive(nameof(SetHitRegions));
        int count = regions.Length;
        Span<int> rects = count <= 64 ? stackalloc int[count * 4] : new int[count * 4];
        for (int i = 0; i < count; ++i)
        {
            rects[i * 4] = regions[i].X;
            rects[i * 4 + 1] = regions[i].Y;
            rects[i * 4 + 2] = regions[i].Width;
            rects[i * 4 + 3] = regions[i].Height;
        }
        fixed (int* first = rects)
            Photino_SetHitRegions_win32(_nativeInstance, first, count, (byte)(regionsArePage ? 1 : 0));
    }

    /// <summary>On the window's thread: shows a window created hidden (<see cref="SetStartHidden"/>), applying any kept minimize or maximize.</summary>
    public void Show()
    {
        ThrowIfNotLive(nameof(Show));
        Photino_Show_win32(_nativeInstance);
    }

    // On the opener's thread only: a Photino window's thread is the one it is constructed on.
    internal PhotinoWindow CreatePopupWindow(PhotinoPopupRequest request)
    {
        ThrowIfNotLive(nameof(PhotinoPopupRequest.CreateWindow));
        var popup = new PhotinoWindow();
        popup._windowsOptions = _windowsOptions;
        popup._windowsOptions.StartHidden = 0;     // a pop-out is meant to be seen; the host may still set it
        popup._windowsOptions.PopupOpener = _nativeInstance;
        popup._windowsOptions.PopupRequestId = request.RequestId;
        popup._hostInputHandler = _hostInputHandler;
        popup._popupRequestedHandler = _popupRequestedHandler;
        popup._navigationStartingHandler = _navigationStartingHandler;
        popup._hostDropHandler = _hostDropHandler;
        popup._popupOpener = this;
        popup.WebMessageReceived = WebMessageReceived;
        foreach (var scheme in CustomSchemes)
            popup.CustomSchemes[scheme.Key] = scheme.Value;
        CopyBrowserSettingsTo(ref popup._startupParameters);
        // Native ignores a popup's start URL (the opener's page navigates it); it only satisfies the parameter check.
        // Never the page's URI: that is page data, and the native copy has a fixed size.
        popup._startupParameters.StartUrl = "about:blank";
        if (request.Position is Point position)
            popup.SetUseOsDefaultLocation(false).SetLocation(position);
        if (request.Size is Size size)
            popup.SetUseOsDefaultSize(false).SetSize(size.Width, size.Height);
        return popup;
    }

    // The opener's browser settings as they were set before its creation (setters used after creation change the
    // live browser only).
    private void CopyBrowserSettingsTo(ref PhotinoNativeParameters target)
    {
        target.TemporaryFilesPath = _startupParameters.TemporaryFilesPath;
        target.UserAgent = _startupParameters.UserAgent;
        target.BrowserControlInitParameters = _startupParameters.BrowserControlInitParameters;
        target.Zoom = _startupParameters.Zoom;
        target.Transparent = _startupParameters.Transparent;
        target.ContextMenuEnabled = _startupParameters.ContextMenuEnabled;
        target.DevToolsEnabled = _startupParameters.DevToolsEnabled;
        target.GrantBrowserPermissions = _startupParameters.GrantBrowserPermissions;
        target.MediaAutoplayEnabled = _startupParameters.MediaAutoplayEnabled;
        target.FileSystemAccessEnabled = _startupParameters.FileSystemAccessEnabled;
        target.WebSecurityEnabled = _startupParameters.WebSecurityEnabled;
        target.JavascriptClipboardAccessEnabled = _startupParameters.JavascriptClipboardAccessEnabled;
        target.MediaStreamEnabled = _startupParameters.MediaStreamEnabled;
        target.SmoothScrollingEnabled = _startupParameters.SmoothScrollingEnabled;
        target.IgnoreCertificateErrorsEnabled = _startupParameters.IgnoreCertificateErrorsEnabled;
        target.NotificationsEnabled = _startupParameters.NotificationsEnabled;
    }

    internal void RefusePopupRequest(int requestId)
    {
        if (_destroyed)
            return;     // destruction refused every open request
        ThrowIfNotLive(nameof(PhotinoPopupRequest.Refuse));
        Photino_RefusePopupRequest_win32(_nativeInstance, requestId);
    }

    // Called by WaitForClose on the window's thread in place of Photino_ctor.
    private IntPtr CreateNativeInstance()
    {
        _hasWindowsOptions = IsWindowsPlatform && HasWindowsOptions;
        if (!_hasWindowsOptions)
            return Photino_ctor(ref _startupParameters);

        _cppHostInput = OnHostInput;
        _cppPopupRequested = OnPopupRequested;
        _cppDestroyed = OnWindowDestroyed;
        _cppNavigationStarting = OnNavigationStarting;
        _cppHostDrop = OnHostDrop;
        _windowsOptions.HostInputHandler = _hostInputHandler == null ? IntPtr.Zero : Marshal.GetFunctionPointerForDelegate(_cppHostInput);
        _windowsOptions.PopupRequestedHandler = _popupRequestedHandler == null ? IntPtr.Zero : Marshal.GetFunctionPointerForDelegate(_cppPopupRequested);
        _windowsOptions.DestroyedHandler = Marshal.GetFunctionPointerForDelegate(_cppDestroyed);
        _windowsOptions.NavigationStartingHandler = _navigationStartingHandler == null ? IntPtr.Zero : Marshal.GetFunctionPointerForDelegate(_cppNavigationStarting);
        _windowsOptions.HostDropHandler = _hostDropHandler == null ? IntPtr.Zero : Marshal.GetFunctionPointerForDelegate(_cppHostDrop);
        _windowsOptions.Size = Marshal.SizeOf<PhotinoNativeWindowsOptions>();

        _aliveUntilDestroyed = GCHandle.Alloc(this);
        _popupOpener?._popups.Add(this);
        try
        {
            return Photino_ctorWithOptions_win32(ref _startupParameters, ref _windowsOptions);
        }
        catch
        {
            _popupOpener?._popups.Remove(this);
            if (_aliveUntilDestroyed.IsAllocated)
                _aliveUntilDestroyed.Free();
            throw;
        }
    }

    private unsafe byte OnHostInput(IntPtr input)
    {
        try
        {
            var handler = _hostInputHandler;
            return (byte)(handler != null && handler(this, in *(PhotinoHostInput*)input) ? 1 : 0);
        }
        catch (Exception ex)
        {
            Log($"host input handler threw: {ex}");
            return 0;
        }
    }

    private unsafe byte OnPopupRequested(int requestId, IntPtr uri, IntPtr features)
    {
        try
        {
            var request = new PhotinoPopupRequest(this, requestId, Marshal.PtrToStringUni(uri) ?? string.Empty,
                in *(PhotinoNativePopupFeatures*)features);
            var handler = _popupRequestedHandler;
            return (byte)(handler != null && handler(request) ? 1 : 0);
        }
        catch (Exception ex)
        {
            Log($"popup request handler threw, request refused: {ex}");
            return 0;
        }
    }

    private byte OnNavigationStarting(IntPtr uri)
    {
        try
        {
            var handler = _navigationStartingHandler;
            return (byte)(handler == null || handler(this, Marshal.PtrToStringUni(uri) ?? string.Empty) ? 1 : 0);
        }
        catch (Exception ex)
        {
            Log($"navigation starting handler threw, navigation cancelled: {ex}");
            return 0;
        }
    }

    // The native answer for an enter that passes the drag to the page (PHOTINO_HOST_DROP_TO_PAGE).
    internal const uint HostDropToPage = 0xFFFFFFFF;

    internal unsafe uint OnHostDrop(IntPtr drop)
    {
        var native = (PhotinoNativeHostDrop*)drop;
        try
        {
            var handler = _hostDropHandler;
            var answer = handler?.Invoke(this, new PhotinoHostDrop(in *native));
            if (answer == null)
                return native->Kind == (int)PhotinoHostDropKind.Enter ? HostDropToPage : 0;
            return (uint)answer.Value & native->AllowedEffects;
        }
        catch (Exception ex)
        {
            Log($"host drop handler threw, drop refused: {ex}");
            return 0;
        }
    }

    internal void OnWindowDestroyed()
    {
        _destroyed = true;
        _popupOpener?._popups.Remove(this);
        try
        {
            WindowDestroyed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log($"window destroyed handler threw: {ex}");
        }
        finally
        {
            if (_aliveUntilDestroyed.IsAllocated)
                _aliveUntilDestroyed.Free();
        }
    }

    private void ThrowIfCreatedOrNotWindows(string option)
    {
        if (!IsWindowsPlatform)
            throw new PlatformNotSupportedException($"{option} is available on Windows only.");
        if (_nativeInstance != IntPtr.Zero)
            throw new InvalidOperationException($"{option} must be called before the native window is created.");
    }

    private void ThrowIfNotLive(string operation)
    {
        if (!IsWindowsPlatform)
            throw new PlatformNotSupportedException($"{operation} is available on Windows only.");
        if (_destroyed)
            throw new ObjectDisposedException(nameof(PhotinoWindow), $"{operation}: the native window was destroyed.");
        if (_nativeInstance == IntPtr.Zero)
            throw new ApplicationException($"{operation} cannot be called until after the Photino window is initialized.");
        ThrowIfNotWindowThread(operation);
    }

    private void ThrowIfNotWindowThread(string operation)
    {
        if (Environment.CurrentManagedThreadId != _managedThreadId)
            throw new InvalidOperationException($"{operation} must be called on the window's thread.");
    }
}
