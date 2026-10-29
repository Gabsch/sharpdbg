using ICorDebugSharp;

namespace SharpDbg.Infrastructure.Debugger;

internal static class EvalExecution
{
	internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
	internal static readonly TimeSpan AbortCompletionTimeout = TimeSpan.FromSeconds(5);

	internal static async Task<T> RunAsync<T>(
		Action startEval,
		Func<int> continueProcess,
		Func<int> stopProcess,
		Func<int> abortEval,
		Func<CancellationToken, Task<T>> waitForCompletion,
		EvalStatus evalStatus,
		TimeSpan timeout,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(startEval);
		ArgumentNullException.ThrowIfNull(continueProcess);
		ArgumentNullException.ThrowIfNull(stopProcess);
		ArgumentNullException.ThrowIfNull(abortEval);
		ArgumentNullException.ThrowIfNull(waitForCompletion);
		ArgumentNullException.ThrowIfNull(evalStatus);
		if (timeout <= TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
		{
			throw new ArgumentOutOfRangeException(nameof(timeout));
		}
		cancellationToken.ThrowIfCancellationRequested();
		if (evalStatus.IsRunning)
		{
			throw new ManagedDebugger.EvalException(
				"startEval",
				"A managed function evaluation is already running.");
		}
		if (evalStatus.RequiresDisconnect)
		{
			throw new ManagedDebugger.EvalException(
				"startEval",
				"A previous managed function evaluation did not produce a terminal callback. Disconnect before evaluating again.");
		}

		evalStatus.IsRunning = true;
		using var completionCancellation = new CancellationTokenSource();
		Task<T>? completion = null;
		var evalStarted = false;
		var terminalCallbackObserved = false;
		try
		{
			startEval();
			evalStarted = true;
			var initialContinueHResult = continueProcess();
			if (initialContinueHResult != Cor.S_OK)
			{
				var initialAbortHResult = abortEval();
				var abortEvidence = initialAbortHResult == Cor.S_OK
					? "The queued evaluation was cooperatively aborted while the process remained stopped."
					: "ICorDebugEval::Abort also rejected cleanup of the queued evaluation.";
				throw new ManagedDebugger.EvalException(
					"continueEval",
					$"The debuggee could not be continued after queuing a managed function evaluation. {abortEvidence} The session must disconnect before another managed function evaluation because its terminal callback cannot be safely observed.",
					initialContinueHResult,
					abortHResult: initialAbortHResult);
			}

			completion = waitForCompletion(completionCancellation.Token);
			var deadline = Task.Delay(timeout, cancellationToken);
			var completed = await Task.WhenAny(completion, deadline).ConfigureAwait(false);
			if (completed == completion)
			{
				terminalCallbackObserved = true;
				return await completion.ConfigureAwait(false);
			}

			var timedOut = !cancellationToken.IsCancellationRequested;
			var stopHResult = stopProcess();
			if (stopHResult != Cor.S_OK)
			{
				throw new ManagedDebugger.EvalException(
					"stopForAbort",
					$"Managed function evaluation {(timedOut ? "timed out" : "was cancelled")}, and the debuggee could not be stopped for a cooperative abort.",
					stopHResult,
					timedOut);
			}

			var abortHResult = abortEval();
			if (abortHResult != Cor.S_OK)
			{
				throw new ManagedDebugger.EvalException(
					"abortEval",
					$"Managed function evaluation {(timedOut ? "timed out" : "was cancelled")}, and ICorDebugEval::Abort rejected the cooperative abort.",
					abortHResult,
					timedOut);
			}

			var continueHResult = continueProcess();
			if (continueHResult != Cor.S_OK)
			{
				throw new ManagedDebugger.EvalException(
					"continueAbort",
					"ICorDebugEval::Abort succeeded, but the debuggee could not be continued to deliver the terminal eval callback.",
					continueHResult,
					timedOut,
					abortHResult);
			}

			var abortCompletionDeadline = Task.Delay(AbortCompletionTimeout);
			completed = await Task.WhenAny(completion, abortCompletionDeadline).ConfigureAwait(false);
			if (completed != completion)
			{
				var restopHResult = stopProcess();
				throw new ManagedDebugger.EvalException(
					"awaitAbortCompletion",
					$"ICorDebugEval::Abort succeeded, but no terminal eval callback arrived within {AbortCompletionTimeout.TotalSeconds:0} seconds; the final stop HRESULT was {FormatHResult(restopHResult)}.",
					restopHResult,
					timedOut,
					abortHResult);
			}

			terminalCallbackObserved = true;
			_ = await completion.ConfigureAwait(false);
			throw new ManagedDebugger.EvalException(
				timedOut ? "evalTimeout" : "evalCancelled",
				timedOut
					? $"Managed function evaluation exceeded its {timeout.TotalSeconds:0.###}-second timeout and was cooperatively aborted."
					: "Managed function evaluation was cancelled and cooperatively aborted.",
				Cor.S_OK,
				timedOut,
				abortHResult);
		}
		finally
		{
			if (evalStarted && !terminalCallbackObserved) evalStatus.RequiresDisconnect = true;
			completionCancellation.Cancel();
			if (completion is { IsCompleted: false })
			{
				try
				{
					await completion.ConfigureAwait(false);
				}
				catch (OperationCanceledException) when (completionCancellation.IsCancellationRequested)
				{
				}
			}
			evalStatus.IsRunning = false;
		}
	}

	internal static string FormatHResult(int hResult) => $"0x{unchecked((uint)hResult):X8}";
}
