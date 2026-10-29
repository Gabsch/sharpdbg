using ICorDebugSharp;
using SharpDbg.Infrastructure.Debugger;

namespace SharpDbg.Cli.Tests;

public sealed class EvalExecutionTests
{
	[Fact]
	public async Task RunAsync_PreCancelledTokenDoesNotStartOrMutateEvalState()
	{
		var status = new EvalStatus();
		var operations = new List<string>();
		using var cancellation = new CancellationTokenSource();
		cancellation.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EvalExecution.RunAsync(
			() => operations.Add("start"),
			() => { operations.Add("continue"); return Cor.S_OK; },
			() => { operations.Add("stop"); return Cor.S_OK; },
			() => { operations.Add("abort"); return Cor.S_OK; },
			_ => { operations.Add("wait"); return Task.FromResult(0); },
			status,
			TimeSpan.FromSeconds(1),
			cancellation.Token));

		Assert.Empty(operations);
		Assert.False(status.IsRunning);
	}

	[Fact]
	public async Task RunAsync_ClearsStatusWhenStartThrows()
	{
		var status = new EvalStatus();

		await Assert.ThrowsAsync<InvalidOperationException>(() => EvalExecution.RunAsync(
			() => throw new InvalidOperationException("start failed"),
			() => Cor.S_OK,
			() => Cor.S_OK,
			() => Cor.S_OK,
			_ => Task.FromResult(0),
			status,
			TimeSpan.FromSeconds(1),
			TestContext.Current.CancellationToken));

		Assert.False(status.IsRunning);
	}

	[Fact]
	public async Task RunAsync_InitialContinueFailureAbortsQueuedEvalAndPreservesBothHResults()
	{
		const int continueFailure = unchecked((int)0x80004005);
		var status = new EvalStatus();
		var operations = new List<string>();

		var exception = await Assert.ThrowsAsync<ManagedDebugger.EvalException>(() => EvalExecution.RunAsync(
			() => operations.Add("start"),
			() => { operations.Add("continue"); return continueFailure; },
			() => { operations.Add("stop"); return Cor.S_OK; },
			() => { operations.Add("abort"); return Cor.S_OK; },
			_ => { operations.Add("wait"); return Task.FromResult(0); },
			status,
			TimeSpan.FromSeconds(1),
			TestContext.Current.CancellationToken));

		Assert.Equal("continueEval", exception.Operation);
		Assert.Equal(continueFailure, exception.OperationHResult);
		Assert.Equal(Cor.S_OK, exception.AbortHResult);
		Assert.Contains("hresult=0x80004005", exception.Message);
		Assert.Contains("abortHResult=0x00000000", exception.Message);
		Assert.Contains("must disconnect", exception.Message);
		Assert.Equal(["start", "continue", "abort"], operations);
		Assert.True(status.RequiresDisconnect);
		Assert.False(status.IsRunning);

		var retryException = await Assert.ThrowsAsync<ManagedDebugger.EvalException>(() => EvalExecution.RunAsync(
			() => operations.Add("retryStart"),
			() => Cor.S_OK,
			() => Cor.S_OK,
			() => Cor.S_OK,
			_ => Task.FromResult(0),
			status,
			TimeSpan.FromSeconds(1),
			TestContext.Current.CancellationToken));
		Assert.Equal("startEval", retryException.Operation);
		Assert.DoesNotContain("retryStart", operations);
	}

	[Fact]
	public async Task RunAsync_TimeoutStopsAbortsContinuesAndClearsStatus()
	{
		var status = new EvalStatus();
		var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
		var operations = new List<string>();

		var exception = await Assert.ThrowsAsync<ManagedDebugger.EvalException>(() => EvalExecution.RunAsync(
			() => operations.Add("start"),
			() =>
			{
				operations.Add("continue");
				if (operations.Count(item => item == "continue") == 2) completion.TrySetResult(42);
				return Cor.S_OK;
			},
			() => { operations.Add("stop"); return Cor.S_OK; },
			() => { operations.Add("abort"); return Cor.S_OK; },
			_ => completion.Task,
			status,
			TimeSpan.FromMilliseconds(1),
			TestContext.Current.CancellationToken));

		Assert.Equal("evalTimeout", exception.Operation);
		Assert.True(exception.TimedOut);
		Assert.Equal(Cor.S_OK, exception.AbortHResult);
		Assert.Equal(["start", "continue", "stop", "abort", "continue"], operations);
		Assert.False(status.RequiresDisconnect);
		Assert.False(status.IsRunning);
	}

	[Fact]
	public async Task RunAsync_CancellationUsesCooperativeAbortAndReportsExactHResult()
	{
		var status = new EvalStatus();
		var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
		using var cancellation = new CancellationTokenSource();
		var continueCalls = 0;

		var exception = await Assert.ThrowsAsync<ManagedDebugger.EvalException>(() => EvalExecution.RunAsync(
			() => { },
			() =>
			{
				continueCalls++;
				if (continueCalls == 1) cancellation.Cancel();
				else completion.TrySetResult(0);
				return Cor.S_OK;
			},
			() => Cor.S_OK,
			() => Cor.S_OK,
			_ => completion.Task,
			status,
			Timeout.InfiniteTimeSpan,
			cancellation.Token));

		Assert.Equal("evalCancelled", exception.Operation);
		Assert.False(exception.TimedOut);
		Assert.Equal(Cor.S_OK, exception.AbortHResult);
		Assert.Contains("abortHResult=0x00000000", exception.Message);
		Assert.False(status.IsRunning);
	}

	[Fact]
	public async Task RunAsync_AbortFailureLeavesProcessStoppedAndPreservesExactHResult()
	{
		const int abortFailure = unchecked((int)0x8013130A);
		var status = new EvalStatus();
		var continueCalls = 0;
		var readerExited = false;

		var exception = await Assert.ThrowsAsync<ManagedDebugger.EvalException>(() => EvalExecution.RunAsync(
			() => { },
			() => { continueCalls++; return Cor.S_OK; },
			() => Cor.S_OK,
			() => abortFailure,
			async cancellationToken =>
			{
				try
				{
					await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
					return 0;
				}
				finally
				{
					readerExited = true;
				}
			},
			status,
			TimeSpan.FromMilliseconds(1),
			TestContext.Current.CancellationToken));

		Assert.Equal("abortEval", exception.Operation);
		Assert.Equal(abortFailure, exception.OperationHResult);
		Assert.Contains("hresult=0x8013130A", exception.Message);
		Assert.Equal(1, continueCalls);
		Assert.True(readerExited);
		Assert.True(status.RequiresDisconnect);
		Assert.False(status.IsRunning);
	}

	[Fact]
	public async Task RunAsync_CompletionDoesNotAbort()
	{
		var status = new EvalStatus();
		var abortCalls = 0;

		var result = await EvalExecution.RunAsync(
			() => { },
			() => Cor.S_OK,
			() => Cor.S_OK,
			() => { abortCalls++; return Cor.S_OK; },
			_ => Task.FromResult(7),
			status,
			TimeSpan.FromSeconds(1),
			TestContext.Current.CancellationToken);

		Assert.Equal(7, result);
		Assert.Equal(0, abortCalls);
		Assert.False(status.RequiresDisconnect);
		Assert.False(status.IsRunning);
	}
}
