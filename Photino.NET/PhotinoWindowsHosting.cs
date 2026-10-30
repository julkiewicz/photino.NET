using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: InternalsVisibleTo("Photino.NET.Tests")]

namespace Photino.NET;

// Types for the Windows-only hosting options of Photino.Native (Photino_ctorWithOptions_win32): composition hosting,
// host surfaces, hit regions, host input and popup hosting. Every struct here matches its native counterpart in
// Photino.h field for field; Photino.NET.Tests checks the sizes and offsets against the native library.

/// <summary>
/// One mouse or key message that composition hosting routes to the host instead of the page (Windows only).
/// </summary>
/// <remarks>
/// X and Y are in client pixels (also for the wheel, whose LParam is in screen pixels). WParam and LParam are the
/// window message's own.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly struct PhotinoHostInput
{
    /// <summary>The window message (WM_MOUSEMOVE, WM_LBUTTONDOWN, WM_MOUSEWHEEL, WM_KEYDOWN, WM_SETCURSOR, ...).</summary>
    public readonly uint Message;
    /// <summary>The pointer's x in client pixels.</summary>
    public readonly int X;
    /// <summary>The pointer's y in client pixels.</summary>
    public readonly int Y;
    /// <summary>The message's WPARAM.</summary>
    public readonly ulong WParam;
    /// <summary>The message's LPARAM.</summary>
    public readonly long LParam;

    public PhotinoHostInput(uint message, int x, int y, ulong wParam, long lParam)
    {
        Message = message;
        X = x;
        Y = y;
        WParam = wParam;
        LParam = lParam;
    }
}

/// <summary>
/// Where a composition surface sits in the window's client area, in physical pixels (Windows only).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct PhotinoSurfaceBounds
{
    /// <summary>The surface handle from <see cref="PhotinoWindow.AddCompositionSurface"/>.</summary>
    public IntPtr Surface;
    public int X;
    public int Y;
    public int Width;
    public int Height;

    public PhotinoSurfaceBounds(IntPtr surface, Rectangle bounds)
    {
        Surface = surface;
        X = bounds.X;
        Y = bounds.Y;
        Width = bounds.Width;
        Height = bounds.Height;
    }
}

/// <summary>
/// Called with mouse input outside the page's hit regions and key input while the window (not the page) has focus.
/// Return true when the host handled it: Photino then skips its default handling (a key that would open the window
/// menu, or WM_SETCURSOR when the host set its own cursor). Called on the window's thread.
/// </summary>
public delegate bool PhotinoHostInputHandler(PhotinoWindow window, in PhotinoHostInput input);

/// <summary>
/// Called on the opener's thread when its page calls window.open. Return true to host it, then fulfil it (now or
/// later) with a window from <see cref="PhotinoPopupRequest.CreateWindow"/>, or refuse it later with
/// <see cref="PhotinoPopupRequest.Refuse"/>. Return false to refuse it now (window.open returns null). The page's
/// script waits until the request is fulfilled or refused, so fulfil it promptly; a request still open after the
/// opener's popup request timeout is refused.
/// </summary>
public delegate bool PhotinoPopupRequestedHandler(PhotinoPopupRequest request);

/// <summary>
/// Called on the window's thread before its page, or a frame in it, navigates to <paramref name="uri"/>: the first
/// load, a link, a script setting location, a reload, a dropped file or link. Return false to cancel the navigation;
/// the page stays as it is.
/// </summary>
public delegate bool PhotinoNavigationStartingHandler(PhotinoWindow window, string uri);

/// <summary>The drag and drop effects (OLE's DROPEFFECT flags).</summary>
[Flags]
public enum PhotinoDropEffects : uint
{
    None = 0,
    Copy = 1,
    Move = 2,
    Link = 4,
}

/// <summary>What happens to a drag over the host's area.</summary>
public enum PhotinoHostDropKind
{
    /// <summary>The drag came into the host's area.</summary>
    Enter = 0,
    /// <summary>The drag moved within the host's area.</summary>
    Over = 1,
    /// <summary>The drag left the host's area or the window, or was cancelled.</summary>
    Leave = 2,
    /// <summary>The data was dropped on the host's area.</summary>
    Drop = 3,
}

/// <summary>
/// One drag and drop event over the host's area (outside the page's hit regions) in composition mode (Windows only).
/// </summary>
public sealed class PhotinoHostDrop
{
    internal PhotinoHostDrop(in PhotinoNativeHostDrop native)
    {
        Kind = (PhotinoHostDropKind)native.Kind;
        X = native.X;
        Y = native.Y;
        KeyState = native.KeyState;
        AllowedEffects = (PhotinoDropEffects)native.AllowedEffects;
        Formats = Lines(native.Formats);
        Files = Lines(native.Files);
        Text = native.Text == IntPtr.Zero ? null : Marshal.PtrToStringUni(native.Text);
    }

    private static string[] Lines(IntPtr text) =>
        text == IntPtr.Zero ? Array.Empty<string>() : (Marshal.PtrToStringUni(text) ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>What happened to the drag.</summary>
    public PhotinoHostDropKind Kind { get; }
    /// <summary>The pointer's x in client pixels.</summary>
    public int X { get; }
    /// <summary>The pointer's y in client pixels.</summary>
    public int Y { get; }
    /// <summary>The keys and buttons held (the MK_* flags).</summary>
    public uint KeyState { get; }
    /// <summary>The effects the drag's source allows.</summary>
    public PhotinoDropEffects AllowedEffects { get; }
    /// <summary>The data's clipboard formats (standard ones as CF_*, registered ones by name); empty on a leave.</summary>
    public IReadOnlyList<string> Formats { get; }
    /// <summary>On a drop, the dropped files' paths (CF_HDROP); otherwise empty.</summary>
    public IReadOnlyList<string> Files { get; }
    /// <summary>On a drop, the data's text (CF_UNICODETEXT), or null when it has none; otherwise null.</summary>
    public string Text { get; }
}

/// <summary>
/// Called on the window's thread for a drag over the host's area in composition mode. Return the effect the host
/// takes (<see cref="PhotinoDropEffects.None"/> refuses it there; masked by the allowed effects). For an enter, null
/// passes the drag to the page instead: the page has it over the host's area until it leaves that area, and the host
/// gets no over, leave or drop for it meanwhile (null for any other kind refuses). A leave's answer is ignored. An
/// exception it throws is logged and taken as a refusal. It runs inside OLE's call to the window: to close the window
/// from it, post the close (closing it in the call ends the drag, which the handler then hears as a leave).
/// </summary>
public delegate PhotinoDropEffects? PhotinoHostDropHandler(PhotinoWindow window, PhotinoHostDrop drop);

/// <summary>
/// A page's window.open, reported to the host (Windows only).
/// </summary>
public sealed class PhotinoPopupRequest
{
    internal PhotinoPopupRequest(PhotinoWindow opener, int requestId, string uri, in PhotinoNativePopupFeatures features)
    {
        Opener = opener;
        RequestId = requestId;
        Uri = uri;
        if (features.HasPosition != 0)
            Position = new Point(features.Left, features.Top);
        if (features.HasSize != 0)
            Size = new Size(features.Width, features.Height);
    }

    /// <summary>The window whose page called window.open.</summary>
    public PhotinoWindow Opener { get; }

    /// <summary>The request's id, unique within its opener.</summary>
    public int RequestId { get; }

    /// <summary>The URI the page opened (often about:blank).</summary>
    public string Uri { get; }

    /// <summary>The position window.open asked for, in pixels, or null when the page gave none.</summary>
    public Point? Position { get; }

    /// <summary>The size window.open asked for, in pixels, or null when the page gave none.</summary>
    public Size? Size { get; }

    /// <summary>
    /// A new window of the opener's kind, set to fulfil this request. Call it, and then
    /// <see cref="PhotinoWindow.WaitForClose"/> on the new window, on the opener's thread (it throws on any other):
    /// the window joins the opener's WebView2 environment and becomes the page's new window.
    /// </summary>
    /// <remarks>
    /// Copied from the opener: its Windows hosting options except StartHidden (a pop-out starts visible; set it again
    /// to keep it hidden), its host input and popup request handlers, its web message handlers, its custom schemes,
    /// and its browser settings as set before its creation (context menu, dev tools, zoom, user agent, permissions,
    /// transparency, temporary files path and the other start parameters of that kind). Taken from the request:
    /// window.open's position and size, as the page gave them. Not copied: the title, icon, size limits, the other
    /// window events (closing, resize, focus, destroyed) and anything changed on the opener after its creation.
    /// </remarks>
    public PhotinoWindow CreateWindow() => Opener.CreatePopupWindow(this);

    /// <summary>Refuses the request (window.open returns null). Does nothing once it was fulfilled or refused.</summary>
    public void Refuse() => Opener.RefusePopupRequest(RequestId);
}

[StructLayout(LayoutKind.Sequential)]
internal struct PhotinoNativeWindowsOptions
{
    internal byte StartHidden;          // C++ bool: one byte
    internal byte CompositionHosting;
    internal int HitRegionHoldMilliseconds;
    internal IntPtr HostInputHandler;
    internal IntPtr PopupRequestedHandler;
    internal int PopupRequestTimeoutMilliseconds;
    internal IntPtr PopupOpener;
    internal int PopupRequestId;
    internal IntPtr DestroyedHandler;
    internal IntPtr NavigationStartingHandler;
    internal byte BrowserAcceleratorKeysDisabled;   // C++ bool: one byte
    internal IntPtr HostDropHandler;
    internal int Size;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PhotinoNativeHostDrop
{
    internal int Kind;
    internal int X;
    internal int Y;
    internal uint KeyState;
    internal uint AllowedEffects;
    internal IntPtr Formats;            // UTF-16, one per line, or null
    internal IntPtr Files;
    internal IntPtr Text;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PhotinoNativePopupFeatures
{
    internal byte HasPosition;          // C++ bool: one byte
    internal byte HasSize;
    internal int Left;
    internal int Top;
    internal int Width;
    internal int Height;
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate byte CppHostInputDelegate(IntPtr input);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate byte CppPopupRequestedDelegate(int requestId, IntPtr uri, IntPtr features);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void CppDestroyedDelegate();
[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate byte CppNavigationStartingDelegate(IntPtr uri);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate uint CppHostDropDelegate(IntPtr drop);
