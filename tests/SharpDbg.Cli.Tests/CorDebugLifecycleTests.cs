using System.Reflection;
using ICorDebugSharp;
using SharpDbg.Infrastructure.Debugger;

namespace SharpDbg.Cli.Tests;

public sealed class CorDebugLifecycleTests
{
	[Fact]
	public async Task Disconnect_TerminatesInitializedCorDebugWithoutAttachedProcess_ExactlyOnce()
	{
		var corDebug = new RecordingCorDebug();
		var debugger = new ManagedDebugger();
		SetField(debugger, "_corDebug", corDebug);
		SetField(debugger, "_corDebugInitialized", true);

		await debugger.Disconnect(terminateDebuggee: false);
		await debugger.Disconnect(terminateDebuggee: false);

		Assert.Equal(1, corDebug.TerminateCallCount);
		Assert.Null(GetField<ICorDebug>(debugger, "_corDebug"));
		Assert.False(GetField<bool>(debugger, "_corDebugInitialized"));
	}

	[Fact]
	public async Task Disconnect_DoesNotTerminateCorDebugWhenInitializeDidNotComplete()
	{
		var corDebug = new RecordingCorDebug();
		var debugger = new ManagedDebugger();
		SetField(debugger, "_corDebug", corDebug);

		await debugger.Disconnect(terminateDebuggee: false);

		Assert.Equal(0, corDebug.TerminateCallCount);
		Assert.Null(GetField<ICorDebug>(debugger, "_corDebug"));
	}

	[Fact]
	public async Task CompleteTermination_WaitsForExitProcessBeforeTerminatingCorDebug()
	{
		var corDebug = new RecordingCorDebug();
		var debugger = new ManagedDebugger();
		SetField(debugger, "_corDebug", corDebug);
		SetField(debugger, "_corDebugInitialized", true);

		var completion = (Task)typeof(ManagedDebugger)
			.GetMethod("CompleteTerminationAfterExitProcessAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
			.Invoke(debugger, [TimeSpan.FromSeconds(1)])!;
		Assert.Equal(0, corDebug.TerminateCallCount);

		GetField<TaskCompletionSource>(debugger, "_processExitObserved").SetResult();
		await completion;

		Assert.Equal(1, corDebug.TerminateCallCount);
	}

	[Fact]
	public async Task CompleteTermination_LeavesCallbackDispatchAliveWhenExitProcessIsLate()
	{
		var corDebug = new RecordingCorDebug();
		var debugger = new ManagedDebugger();
		SetField(debugger, "_corDebug", corDebug);
		SetField(debugger, "_corDebugInitialized", true);

		var completion = (Task)typeof(ManagedDebugger)
			.GetMethod("CompleteTerminationAfterExitProcessAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
			.Invoke(debugger, [TimeSpan.FromMilliseconds(10)])!;

		await Assert.ThrowsAsync<TimeoutException>(() => completion);

		Assert.Equal(0, corDebug.TerminateCallCount);
		Assert.Same(corDebug, GetField<ICorDebug>(debugger, "_corDebug"));
		Assert.True(GetField<bool>(debugger, "_corDebugInitialized"));
	}

	private static void SetField<T>(ManagedDebugger debugger, string name, T value) =>
		typeof(ManagedDebugger).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(debugger, value);

	private static T GetField<T>(ManagedDebugger debugger, string name) =>
		(T)typeof(ManagedDebugger).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(debugger)!;

	private sealed class RecordingCorDebug : ICorDebug
	{
		public int TerminateCallCount { get; private set; }

		public int TryInitialize() => Cor.S_OK;
		public int TryTerminate()
		{
			TerminateCallCount++;
			return Cor.S_OK;
		}
		public int TrySetManagedHandler(ICorDebugManagedCallback pCallback) => throw new NotSupportedException();
		public int TrySetUnmanagedHandler(ICorDebugUnmanagedCallback pCallback) => throw new NotSupportedException();
		public int TryCreateProcess(string lpApplicationName, string lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string lpCurrentDirectory, IntPtr lpStartupInfo, IntPtr lpProcessInformation, CorDebugCreateProcessFlags debuggingFlags, out ICorDebugProcess ppProcess) => throw new NotSupportedException();
		public int TryDebugActiveProcess(uint id, bool win32Attach, out ICorDebugProcess ppProcess) => throw new NotSupportedException();
		public int TryEnumerateProcesses(out ICorDebugProcessEnum ppProcess) => throw new NotSupportedException();
		public int TryGetProcess(uint dwProcessId, out ICorDebugProcess ppProcess) => throw new NotSupportedException();
		public int TryCanLaunchOrAttach(uint dwProcessId, bool win32DebuggingEnabled) => throw new NotSupportedException();
	}
}
