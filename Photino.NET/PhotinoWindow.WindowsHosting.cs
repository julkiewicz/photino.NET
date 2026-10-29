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
    private bool _hasWindowsOptions;
    private PhotinoHostInputHandler _hostInputHandler;
    private PhotinoPopupRequestedHandler _popupRequestedHandler;
    // The delegates native code calls: kept alive as long as this window.
    private CppHostInputDelegate _cppHostInput;
    private CppPopupRequestedDelegate _cppPopupRequested;
    private CppDestroyedDelegate _cppDestroyed;
    // Keeps a window made with these options (and its delegates) alive until its native window is destroyed, so a
    // popup the host does not hold on to is not collected while it is open.
    private GCHandle _aliveUntilDestroyed;
    private PhotinoWindow _popupOpener;
    private readonly List<PhotinoWindow> _popups = new();

    /// <summary>Raised on the window's thread when its native window was destroyed (windows with Windows hosting options only).</summary>
    public event EventHandler WindowDestroyed;

    /// <summary>The window whose page opened this one, or null when this window is not a popup.</summary>
    public PhotinoWindow PopupOpener => _popupOpener;

    /// <summary>The popups this window's page opened that are still open. Read on the window's thread.</summary>
    public IReadOnlyList<PhotinoWindow> Popups => _popups;

    /// <summary>
    /// Windows only, before the window is created: create it without showing it. The page still loads, lays out
    /// and runs, and never takes focus; <see cref="Show"/> shows it. With any of the Windows hosting options set,
    /// minimize, maximize and restore never activate the window.
    /// </summary>
    public PhotinoWindow SetStartHidden(bool startHidden)
    {
        PrepareWindowsOption(nameof(SetStartHidden));
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
        PrepareWindowsOption(nameof(SetCompositionHosting));
        _windowsOptions.CompositionHosting = (byte)(compositionHosting ? 1 : 0);
        return this;
    }

    /// <summary>
    /// Windows only, before the window is created: after the window's size or DPI changes, a press waits up to this
    /// long for the page to publish its new hit regions. 0 means the default (100 ms), a negative value turns it off.
    /// </summary>
    public PhotinoWindow SetHitRegionHoldMilliseconds(int milliseconds)
    {
        PrepareWindowsOption(nameof(SetHitRegionHoldMilliseconds));
        _windowsOptions.HitRegionHoldMilliseconds = milliseconds;
        return this;
    }

    /// <summary>
    /// Windows only, before the window is created: an accepted popup request not fulfilled within this is refused.
    /// 0 means the default (5000 ms), a negative value never.
    /// </summary>
    public PhotinoWindow SetPopupRequestTimeoutMilliseconds(int milliseconds)
    {
        PrepareWindowsOption(nameof(SetPopupRequestTimeoutMilliseconds));
        _windowsOptions.PopupRequestTimeoutMilliseconds = milliseconds;
        return this;
    }

    /// <summary>Windows only, before the window is created: receives the input composition hosting routes to the host.</summary>
    public PhotinoWindow RegisterHostInputHandler(PhotinoHostInputHandler handler)
    {
        PrepareWindowsOption(nameof(RegisterHostInputHandler));
        _hostInputHandler = handler;
        return this;
    }

    /// <summary>
    /// Windows only, before the window is created: receives the page's window.open requests. Without one,
    /// WebView2's own behavior is unchanged.
    /// </summary>
    public PhotinoWindow RegisterPopupRequestedHandler(PhotinoPopupRequestedHandler handler)
    {
        PrepareWindowsOption(nameof(RegisterPopupRequestedHandler));
        _popupRequestedHandler = handler;
        return this;
    }

    /// <summary>Registers a handler for <see cref="WindowDestroyed"/>.</summary>
    public PhotinoWindow RegisterWindowDestroyedHandler(EventHandler handler)
    {
        WindowDestroyed += handler;
        return this;
    }

    /// <summary>Windows only: whether the window is hosted through the composition controller.</summary>
    public bool CompositionHostingEnabled
    {
        get
        {
            if (!IsWindowsPlatform || _nativeInstance == IntPtr.Zero)
                return false;
            byte enabled = 0;
            Invoke(() => Photino_GetCompositionHostingEnabled_win32(_nativeInstance, out enabled));
            return enabled != 0;
        }
    }

    /// <summary>
    /// Windows only, composition hosting: makes a composition surface handle shown at these client-area bounds
    /// (physical pixels) under the page. The host's renderer makes its swap chain for it
    /// (IDXGIFactoryMedia.CreateSwapChainForCompositionSurfaceHandle) on its own thread. Release it with
    /// <see cref="RemoveCompositionSurface"/> once that swap chain is released. Returns zero on failure.
    /// </summary>
    public IntPtr AddCompositionSurface(Rectangle bounds)
    {
        ThrowIfNoNativeWindow(nameof(AddCompositionSurface));
        IntPtr surface = IntPtr.Zero;
        Invoke(() => surface = Photino_AddCompositionSurface_win32(_nativeInstance, bounds.X, bounds.Y, bounds.Width, bounds.Height));
        return surface;
    }

    /// <summary>Windows only, composition hosting: moves and clips any number of surfaces in one commit.</summary>
    public unsafe void SetCompositionSurfaceBounds(ReadOnlySpan<PhotinoSurfaceBounds> bounds)
    {
        ThrowIfNoNativeWindow(nameof(SetCompositionSurfaceBounds));
        if (Environment.CurrentManagedThreadId == _managedThreadId)
        {
            fixed (PhotinoSurfaceBounds* first = bounds)
                Photino_SetCompositionSurfaceBounds_win32(_nativeInstance, first, bounds.Length);
            return;
        }
        var copy = bounds.ToArray();
        Invoke(() =>
        {
            fixed (PhotinoSurfaceBounds* first = copy)
                Photino_SetCompositionSurfaceBounds_win32(_nativeInstance, first, copy.Length);
        });
    }

    /// <summary>
    /// Windows only, composition hosting: removes a surface and closes its handle. Call it after the swap chain made
    /// for it is released. Closing the window closes the handles it made.
    /// </summary>
    public void RemoveCompositionSurface(IntPtr surface)
    {
        ThrowIfNoNativeWindow(nameof(RemoveCompositionSurface));
        Invoke(() => Photino_RemoveCompositionSurface_win32(_nativeInstance, surface));
    }

    /// <summary>
    /// Windows only, composition hosting: sets the hit regions in client pixels, replacing any the page published.
    /// When <paramref name="regionsArePage"/> is true, input inside the regions goes to the page and outside to the
    /// host; when false, the other way round.
    /// </summary>
    public unsafe void SetHitRegions(ReadOnlySpan<Rectangle> regions, bool regionsArePage = true)
    {
        ThrowIfNoNativeWindow(nameof(SetHitRegions));
        int count = regions.Length;
        var rects = new int[count * 4];
        for (int i = 0; i < count; ++i)
        {
            rects[i * 4] = regions[i].X;
            rects[i * 4 + 1] = regions[i].Y;
            rects[i * 4 + 2] = regions[i].Width;
            rects[i * 4 + 3] = regions[i].Height;
        }
        Invoke(() =>
        {
            fixed (int* first = rects)
                Photino_SetHitRegions_win32(_nativeInstance, first, count, (byte)(regionsArePage ? 1 : 0));
        });
    }

    /// <summary>Windows only: shows a window created hidden (<see cref="SetStartHidden"/>), applying any kept minimize or maximize.</summary>
    public void Show()
    {
        ThrowIfNoNativeWindow(nameof(Show));
        Invoke(() => Photino_Show_win32(_nativeInstance));
    }

    internal PhotinoWindow CreatePopupWindow(PhotinoPopupRequest request)
    {
        var popup = new PhotinoWindow();
        popup._windowsOptions = _windowsOptions;
        popup._windowsOptions.PopupOpener = _nativeInstance;
        popup._windowsOptions.PopupRequestId = request.RequestId;
        popup._hasWindowsOptions = true;
        popup._hostInputHandler = _hostInputHandler;
        popup._popupRequestedHandler = _popupRequestedHandler;
        popup._popupOpener = this;
        foreach (var scheme in CustomSchemes)
            popup.CustomSchemes[scheme.Key] = scheme.Value;
        // The opener's page navigates the popup; the URL only satisfies the start parameters' check.
        popup._startupParameters.StartUrl = string.IsNullOrEmpty(request.Uri) ? "about:blank" : request.Uri;
        if (request.Position is Point position)
            popup.SetUseOsDefaultLocation(false).SetLocation(position);
        if (request.Size is Size size)
            popup.SetUseOsDefaultSize(false).SetSize(size.Width, size.Height);
        return popup;
    }

    internal void RefusePopupRequest(int requestId)
    {
        ThrowIfNoNativeWindow(nameof(RefusePopupRequest));
        Invoke(() => Photino_RefusePopupRequest_win32(_nativeInstance, requestId));
    }

    // Called by WaitForClose on the window's thread in place of Photino_ctor.
    private IntPtr CreateNativeInstance()
    {
        if (!_hasWindowsOptions)
            return Photino_ctor(ref _startupParameters);

        _cppHostInput = OnHostInput;
        _cppPopupRequested = OnPopupRequested;
        _cppDestroyed = OnWindowDestroyed;
        _windowsOptions.HostInputHandler = _hostInputHandler == null ? IntPtr.Zero : Marshal.GetFunctionPointerForDelegate(_cppHostInput);
        _windowsOptions.PopupRequestedHandler = _popupRequestedHandler == null ? IntPtr.Zero : Marshal.GetFunctionPointerForDelegate(_cppPopupRequested);
        _windowsOptions.DestroyedHandler = Marshal.GetFunctionPointerForDelegate(_cppDestroyed);
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
            _aliveUntilDestroyed.Free();
            throw;
        }
    }

    private unsafe byte OnHostInput(IntPtr input)
    {
        var handler = _hostInputHandler;
        return (byte)(handler != null && handler(this, in *(PhotinoHostInput*)input) ? 1 : 0);
    }

    private unsafe byte OnPopupRequested(int requestId, IntPtr uri, IntPtr features)
    {
        var request = new PhotinoPopupRequest(this, requestId, Marshal.PtrToStringUni(uri) ?? string.Empty,
            in *(PhotinoNativePopupFeatures*)features);
        var handler = _popupRequestedHandler;
        return (byte)(handler != null && handler(request) ? 1 : 0);
    }

    private void OnWindowDestroyed()
    {
        _popupOpener?._popups.Remove(this);
        try
        {
            WindowDestroyed?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            if (_aliveUntilDestroyed.IsAllocated)
                _aliveUntilDestroyed.Free();
        }
    }

    private void PrepareWindowsOption(string option)
    {
        if (!IsWindowsPlatform)
            throw new PlatformNotSupportedException($"{option} is available on Windows only.");
        if (_nativeInstance != IntPtr.Zero)
            throw new InvalidOperationException($"{option} must be called before the native window is created.");
        _hasWindowsOptions = true;
    }

    private void ThrowIfNoNativeWindow(string operation)
    {
        if (!IsWindowsPlatform)
            throw new PlatformNotSupportedException($"{operation} is available on Windows only.");
        if (_nativeInstance == IntPtr.Zero)
            throw new ApplicationException($"{operation} cannot be called until after the Photino window is initialized.");
    }
}
