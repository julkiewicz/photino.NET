using System.Drawing;
using System.Reflection;
using Photino.NET;
using Xunit;

namespace Photino.NET.Tests;

// The hosting calls' guards, checked without a native window: a fake instance pointer that native code must never
// see (every case here throws or returns before a native call).
public class WindowLifetimeTests
{
    private static readonly FieldInfo NativeInstance =
        typeof(PhotinoWindow).GetField("_nativeInstance", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static PhotinoWindow FakeCreatedWindow()
    {
        var window = new PhotinoWindow();
        NativeInstance.SetValue(window, (IntPtr)0x1234);
        return window;
    }

    private static PopupRequestFactory Requests => new();

    [Fact]
    public void HostingCallsAfterDestructionThrowOrDoNothing()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var window = FakeCreatedWindow();

        window.OnWindowDestroyed();

        Assert.True(window.IsDestroyed);
        Assert.False(window.CompositionHostingEnabled);
        Assert.Throws<ObjectDisposedException>(() => window.AddCompositionSurface(new Rectangle(0, 0, 1, 1)));
        Assert.Throws<ObjectDisposedException>(() => window.SetCompositionSurfaceBounds(new PhotinoSurfaceBounds[1]));
        Assert.Throws<ObjectDisposedException>(() => window.SetHitRegions(new[] { new Rectangle(0, 0, 1, 1) }));
        Assert.Throws<ObjectDisposedException>(() => window.Show());
        Assert.Throws<ObjectDisposedException>(() => Requests.For(window).CreateWindow());
        window.RemoveCompositionSurface((IntPtr)0x5678);   // nothing to do: destruction closed the handles
        Requests.For(window).Refuse();                     // nothing to do: destruction refused open requests
    }

    [Fact]
    public void HostingCallsOffTheWindowThreadThrow()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var window = FakeCreatedWindow();
        var request = Requests.For(window);
        var failures = new List<Exception>();

        var thread = new Thread(() =>
        {
            failures.Add(Record.Exception(() => window.AddCompositionSurface(new Rectangle(0, 0, 1, 1))));
            failures.Add(Record.Exception(() => window.SetCompositionSurfaceBounds(new PhotinoSurfaceBounds[1])));
            failures.Add(Record.Exception(() => window.RemoveCompositionSurface((IntPtr)0x5678)));
            failures.Add(Record.Exception(() => window.SetHitRegions(new[] { new Rectangle(0, 0, 1, 1) })));
            failures.Add(Record.Exception(() => window.Show()));
            failures.Add(Record.Exception(() => _ = window.CompositionHostingEnabled));
            failures.Add(Record.Exception(() => request.CreateWindow()));
            failures.Add(Record.Exception(() => request.Refuse()));
        });
        thread.Start();
        thread.Join();

        Assert.All(failures, failure => Assert.IsType<InvalidOperationException>(failure));
    }

    [Fact]
    public void OptionsAfterCreationThrow()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var window = FakeCreatedWindow();

        Assert.Throws<InvalidOperationException>(() => window.SetCompositionHosting(true));
        Assert.Throws<InvalidOperationException>(() => window.RegisterWindowDestroyedHandler((_, _) => { }));
    }

    private sealed class PopupRequestFactory
    {
        public PhotinoPopupRequest For(PhotinoWindow opener) =>
            (PhotinoPopupRequest)typeof(PhotinoPopupRequest)
                .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)[0]
                .Invoke(new object[] { opener, 1, "about:blank", default(PhotinoNativePopupFeatures) });
    }
}
