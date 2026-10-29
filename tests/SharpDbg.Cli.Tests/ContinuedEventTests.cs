using AwesomeAssertions;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using SharpDbg.Cli.Tests.Helpers;

namespace SharpDbg.Cli.Tests;

public class ContinuedEventTests(ITestOutputHelper testOutputHelper)
{
	[Fact]
	public async Task SharpDbgCli_Continue_EmitsContinuedEventAfterResume()
	{
		var startSuspended = true;
		var (debugProtocolHost, initializedEventTcs, debugEventTcs, adapter, process) = TestHelper.GetRunningDebugProtocolHost(testOutputHelper, startSuspended);
		using var _ = adapter;
		using var __ = new ProcessKiller(process);
		using var ___ = debugProtocolHost;

		var continuedEventTcs = new TaskCompletionSource<ContinuedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
		debugProtocolHost.RegisterEventType<ContinuedEvent>(@event => continuedEventTcs.TrySetResult(@event));

		await debugProtocolHost
			.WithInitializeRequest()
			.WithAttachRequest(process.Id)
			.WaitForInitializedEvent(initializedEventTcs);

		var breakpointedFilePath = Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "MyClass.cs");
		debugProtocolHost
			.WithBreakpointsRequest([11], breakpointedFilePath)
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(process.Id, startSuspended);

		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent(debugEventTcs);
		debugProtocolHost.WithContinueRequest(stoppedEvent.ThreadId!.Value);

		var continuedEvent = await continuedEventTcs.Task.WaitAsync(TestContext.Current.CancellationToken);
		continuedEvent.ThreadId.Should().Be(stoppedEvent.ThreadId);
		continuedEvent.AllThreadsContinued.Should().BeTrue();
	}
}
