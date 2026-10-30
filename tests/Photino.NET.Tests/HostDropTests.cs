using System.Runtime.InteropServices;
using Photino.NET;
using Xunit;

namespace Photino.NET.Tests;

// The host drop event as Photino.Native hands it over (a PhotinoHostDrop with UTF-16 strings, one item per line): what
// the handler sees and what goes back, without a native window.
public class HostDropTests
{
    private static unsafe uint Send(PhotinoWindow window, PhotinoHostDropKind kind, PhotinoDropEffects allowed,
        string formats = null, string files = null, string text = null)
    {
        var strings = new[] { formats, files, text }.Select(s => s == null ? IntPtr.Zero : Marshal.StringToHGlobalUni(s)).ToArray();
        try
        {
            var native = new PhotinoNativeHostDrop
            {
                Kind = (int)kind,
                X = 10,
                Y = 20,
                KeyState = 1,
                AllowedEffects = (uint)allowed,
                Formats = strings[0],
                Files = strings[1],
                Text = strings[2],
            };
            return window.OnHostDrop((IntPtr)(&native));
        }
        finally
        {
            foreach (var s in strings)
                Marshal.FreeHGlobal(s);
        }
    }

    [Fact]
    public void WithoutHandlerTheDragGoesToThePage()
    {
        var window = new PhotinoWindow();
        Assert.Equal(PhotinoWindow.HostDropToPage, Send(window, PhotinoHostDropKind.Enter, PhotinoDropEffects.Copy));
        Assert.Equal(0u, Send(window, PhotinoHostDropKind.Drop, PhotinoDropEffects.Copy));
    }

    [Fact]
    public void HandlerSeesTheDataAndItsAnswerIsMaskedByTheAllowedEffects()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var seen = new List<PhotinoHostDrop>();
        var window = new PhotinoWindow().RegisterHostDropHandler((_, drop) =>
        {
            seen.Add(drop);
            return PhotinoDropEffects.Copy | PhotinoDropEffects.Move;
        });

        Assert.Equal((uint)PhotinoDropEffects.Copy,
            Send(window, PhotinoHostDropKind.Enter, PhotinoDropEffects.Copy, "CF_HDROP\nCF_UNICODETEXT"));
        Assert.Equal((uint)PhotinoDropEffects.Copy, Send(window, PhotinoHostDropKind.Drop, PhotinoDropEffects.Copy,
            "CF_HDROP\nCF_UNICODETEXT", "C:\\a.txt\nC:\\b c.png", "some text"));

        Assert.Equal(PhotinoHostDropKind.Enter, seen[0].Kind);
        Assert.Equal(new[] { "CF_HDROP", "CF_UNICODETEXT" }, seen[0].Formats);
        Assert.Empty(seen[0].Files);
        Assert.Null(seen[0].Text);
        Assert.Equal((10, 20, 1u, PhotinoDropEffects.Copy), (seen[0].X, seen[0].Y, seen[0].KeyState, seen[0].AllowedEffects));
        Assert.Equal(new[] { "C:\\a.txt", "C:\\b c.png" }, seen[1].Files);
        Assert.Equal("some text", seen[1].Text);
    }

    [Fact]
    public void NullOnEnterPassesToThePageAndAThrowRefuses()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var passing = new PhotinoWindow().RegisterHostDropHandler((_, _) => null);
        Assert.Equal(PhotinoWindow.HostDropToPage, Send(passing, PhotinoHostDropKind.Enter, PhotinoDropEffects.Copy));
        Assert.Equal(0u, Send(passing, PhotinoHostDropKind.Over, PhotinoDropEffects.Copy));

        var throwing = new PhotinoWindow().RegisterHostDropHandler((_, _) => throw new InvalidOperationException("planted"));
        Assert.Equal(0u, Send(throwing, PhotinoHostDropKind.Enter, PhotinoDropEffects.Copy));
    }
}
