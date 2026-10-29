using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;

namespace SharpDbg.Cli.Tests.Helpers;

public class DisposableDebugProtocolHost(Stream debugAdapterStdIn, Stream debugAdapterStdOut, bool registerStandardHandlers, bool terminateDebuggeeOnDispose = false) : DebugProtocolHost(debugAdapterStdIn, debugAdapterStdOut, registerStandardHandlers), IDisposable
{
	public void Dispose()
	{
		GC.SuppressFinalize(this);
		try
		{
			SendRequestSync(new DisconnectRequest { TerminateDebuggee = terminateDebuggeeOnDispose });
		}
		catch (ProtocolException exception) when (
			!terminateDebuggeeOnDispose &&
			exception.Message.Contains("Live detach is disabled for Windows .NET 10.", StringComparison.Ordinal))
		{
			// The product correctly refuses the affected CoreCLR live-detach path. Test cleanup
			// may terminate the disposable fixture process after observing that explicit refusal.
			SendRequestSync(new DisconnectRequest { TerminateDebuggee = true });
		}
		Stop();
		WaitForReader();
	}
}
