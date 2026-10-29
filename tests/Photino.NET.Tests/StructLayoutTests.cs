using System.Runtime.InteropServices;
using Photino.NET;
using Xunit;

namespace Photino.NET.Tests;

// The P/Invoke structs must match Photino.Native's layout exactly: each native struct's size and field offsets, as
// the native library reports them (Photino_GetStructLayout_win32), against the managed struct's.
public class StructLayoutTests
{
    [Theory]
    [InlineData("PhotinoWindowsOptions", typeof(PhotinoNativeWindowsOptions), new[] { "StartHidden", "CompositionHosting", "HitRegionHoldMilliseconds", "HostInputHandler", "PopupRequestedHandler", "PopupRequestTimeoutMilliseconds", "PopupOpener", "PopupRequestId", "DestroyedHandler", "Size" })]
    [InlineData("PhotinoHostInput", typeof(PhotinoHostInput), new[] { "Message", "X", "Y", "WParam", "LParam" })]
    [InlineData("PhotinoSurfaceBounds", typeof(PhotinoSurfaceBounds), new[] { "Surface", "X", "Y", "Width", "Height" })]
    [InlineData("PhotinoPopupFeatures", typeof(PhotinoNativePopupFeatures), new[] { "HasPosition", "HasSize", "Left", "Top", "Width", "Height" })]
    [InlineData("PhotinoInitParams", typeof(PhotinoNativeParameters), new[] { "Size" })]
    public void ManagedLayoutMatchesNative(string nativeName, Type managed, string[] fields)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var expected = new List<int> { Marshal.SizeOf(managed) };
        expected.AddRange(fields.Select(field => (int)Marshal.OffsetOf(managed, field)));

        var native = NativeLayout(nativeName);

        Assert.Equal(native, expected);
    }

    [Fact]
    public void EveryManagedFieldIsChecked()
    {
        // A field added on the managed side only would otherwise go unchecked (PhotinoInitParams is checked by size).
        Assert.Equal(10, typeof(PhotinoNativeWindowsOptions).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Length);
        Assert.Equal(5, typeof(PhotinoHostInput).GetFields().Length);
        Assert.Equal(5, typeof(PhotinoSurfaceBounds).GetFields().Length);
        Assert.Equal(6, typeof(PhotinoNativePopupFeatures).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Length);
    }

    [Fact]
    public void UnknownStructIsRefused()
    {
        if (!OperatingSystem.IsWindows())
            return;
        Assert.Throws<InvalidOperationException>(() => NativeLayout("NoSuchStruct"));
    }

    private static unsafe List<int> NativeLayout(string name)
    {
        var values = new int[32];
        int count;
        fixed (int* first = values)
            count = PhotinoWindow.Photino_GetStructLayout_win32(name, first, values.Length);
        if (count < 0)
            throw new InvalidOperationException($"Photino.Native does not know {name}.");
        return values.Take(count).ToList();
    }
}
