# Fork changes

This is a fork of [tryphotino/photino.NET](https://github.com/tryphotino/photino.NET) (Apache-2.0). Changes on
branch `windows-composition-hosting`: the C# side of the Windows hosting options of the Photino.Native fork on the
same branch name. A window that sets none of the options is created exactly as upstream.

- Modified: `Photino.NET/PhotinoWindow.NET.cs` (one line: the native constructor call), `Photino.NET/Photino.NET.csproj`
  (the Photino.Native version as a property), `Photino.NET.sln` (the two test projects).
- Added: `Photino.NET/PhotinoWindowsHosting.cs`, `Photino.NET/PhotinoWindow.WindowsHosting.cs`, `packaging/pack.ps1`,
  `tests/Photino.NET.Tests/` (struct layout and guard tests), `tests/Photino.NET.HostingCheck/` (a headless check run
  on a desktop of its own).

Known limit, carried from upstream: the message loop's state is process-wide (`_messageLoopIsStarted` here,
`hwndToPhotino` and the message loop's root window in the native library), so a process has one main window, whose
closing ends the loop; other windows are its popups or are made on its thread.
