using System.Diagnostics;
using System.Reflection;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using SharpDbg.Application;
using SharpDbg.Cli.Tests.Helpers;

namespace SharpDbg.Cli.Tests;

public sealed class DisconnectTests(ITestOutputHelper testOutputHelper)
{
	[Fact]
	public void TransportCloseAfterNonTerminatingDisconnectIntent_DoesNotTerminateDebuggee()
	{
		var log = new List<string>();
		var adapter = new DebugAdapter(log.Add);
		var recordIntent = typeof(DebugAdapter).GetMethod("RecordDisconnectIntent", BindingFlags.Instance | BindingFlags.NonPublic)!;

		recordIntent.Invoke(adapter, [false]);
		adapter.DapAborted_ShutdownDebugger();

		Assert.Contains(log, message => message.Contains("preserving the debuggee", StringComparison.Ordinal));
		Assert.DoesNotContain(log, message => message.Contains("Disconnect (terminate: True)", StringComparison.OrdinalIgnoreCase));

		recordIntent.Invoke(adapter, [true]);
		adapter.DapAborted_ShutdownDebugger();
	}

	[Fact]
	public async Task DisconnectWithoutTermination_PreservesAttachedProcessOrRejectsUnsafeRuntime()
	{
		await VerifyDisconnectLeavesProcessRunning(continueBeforeDisconnect: true);
	}

	[Fact]
	public async Task DisconnectWithoutTermination_FromStoppedState_PreservesProcessOrRejectsUnsafeRuntime()
	{
		await VerifyDisconnectLeavesProcessRunning(continueBeforeDisconnect: false);
	}

	[Fact]
	public async Task DisconnectWithoutTermination_PreservesLaunchedProcessOrRejectsUnsafeRuntime()
	{
		var (host, initialized, debugEvents, adapter) = TestHelper.GetLaunchDebugProtocolHostInProc(testOutputHelper);
		using var adapterLifetime = adapter;
		using var hostLifetime = host;
		var processStarted = new TaskCompletionSource<ProcessEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
		var output = new List<string>();
		host.RegisterEventType<ProcessEvent>(@event => processStarted.TrySetResult(@event));
		host.RegisterEventType<OutputEvent>(@event => output.Add(@event.Output));
		var program = Path.JoinFromGitRoot("artifacts", "bin", "DebuggableConsoleApp", "debug", "DebuggableConsoleApp.dll");

		await host
			.WithInitializeRequest()
			.WithLaunchRequest(program, stopAtEntry: false, justMyCode: true)
			.WaitForInitializedEvent(initialized);

		var sourcePath = Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "MyClass.cs");
		host
			.WithBreakpointsRequest([11], sourcePath)
			.WithConfigurationDoneRequest();

		_ = await host.WaitForStoppedEvent(debugEvents);
		host.WithContinueRequest();
		var processEvent = await processStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
		using var process = Process.GetProcessById(processEvent.SystemProcessId!.Value);
		using var processLifetime = new ProcessKiller(process);
		if (IsAffectedWindowsRuntime())
		{
			AssertUnsafeLiveDetachRejected(host);
			process.Refresh();
			Assert.False(process.HasExited, "The debuggee exited even though unsafe live detach was rejected before ICorDebugProcess.Detach.");
			return;
		}

		host.SendRequestSync(new DisconnectRequest { TerminateDebuggee = false });

		await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
		process.Refresh();
		Assert.False(process.HasExited, $"Debuggee exited after detach.{Environment.NewLine}{string.Join(string.Empty, output)}");
	}

	private async Task VerifyDisconnectLeavesProcessRunning(bool continueBeforeDisconnect)
	{
		const bool startSuspended = false;
		var (host, initialized, debugEvents, adapter, process) = TestHelper.GetRunningDebugProtocolHost(testOutputHelper, startSuspended);
		using var adapterLifetime = adapter;
		using var processLifetime = new ProcessKiller(process);
		using var hostLifetime = host;

		await host
			.WithInitializeRequest()
			.WithAttachRequest(process.Id)
			.WaitForInitializedEvent(initialized);

		var sourcePath = Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "MyClass.cs");
		host
			.WithBreakpointsRequest([11], sourcePath)
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(process.Id, startSuspended);

		_ = await host.WaitForStoppedEvent(debugEvents);
		if (continueBeforeDisconnect) host.WithContinueRequest();
		if (IsAffectedWindowsRuntime())
		{
			AssertUnsafeLiveDetachRejected(host);
			process.Refresh();
			Assert.False(process.HasExited, "The debuggee exited even though unsafe live detach was rejected before ICorDebugProcess.Detach.");
			host.SendRequestSync(new DisconnectRequest { TerminateDebuggee = true });
			return;
		}

		host.SendRequestSync(new DisconnectRequest { TerminateDebuggee = false });

		await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
		process.Refresh();
		Assert.False(process.HasExited, "Debuggee exited after detach.");
	}

	private static bool IsAffectedWindowsRuntime() => OperatingSystem.IsWindows() && Environment.Version.Major == 10;

	private static void AssertUnsafeLiveDetachRejected(DebugProtocolHost host)
	{
		var exception = Assert.Throws<ProtocolException>(() =>
			host.SendRequestSync(new DisconnectRequest { TerminateDebuggee = false }));
		Assert.Contains("Live detach is disabled for Windows .NET 10.", exception.Message);
		Assert.Contains("terminateDebuggee=true", exception.Message);
	}
}
