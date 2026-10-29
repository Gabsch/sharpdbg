using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SharpDbg.Cli.Tests;

internal static class WindowsErrorMode
{
	private const uint SemFailCriticalErrors = 0x0001;
	private const uint SemNoGpFaultErrorBox = 0x0002;

	[ModuleInitializer]
	internal static void Initialize()
	{
		if (!OperatingSystem.IsWindows()) return;

		SetErrorMode(GetErrorMode() | SemFailCriticalErrors | SemNoGpFaultErrorBox);
	}

	[DllImport("kernel32.dll")]
	private static extern uint GetErrorMode();

	[DllImport("kernel32.dll")]
	private static extern uint SetErrorMode(uint mode);
}
