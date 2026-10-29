using AwesomeAssertions;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using SharpDbg.Cli.Tests.Helpers;
using SharpDbg.Infrastructure.Debugger;

namespace SharpDbg.Cli.Tests;

public class ModuleTests(ITestOutputHelper testOutputHelper)
{
	[Fact]
	public async Task SharpDbgCli_Modules_ReturnsLoadedModulesWithPagination()
	{
		var startSuspended = true;
		var (debugProtocolHost, initializedEventTcs, debugEventTcs, adapter, process) = TestHelper.GetRunningDebugProtocolHost(testOutputHelper, startSuspended);
		using var _ = adapter;
		using var __ = new ProcessKiller(process);
		using var ___ = debugProtocolHost;

		var initializeResponse = debugProtocolHost.SendRequestSync(DebugAdapterProcessHelper.GetInitializeRequest());
		initializeResponse.SupportsModulesRequest.Should().BeTrue();

		await debugProtocolHost
			.WithAttachRequest(process.Id)
			.WaitForInitializedEvent(initializedEventTcs);

		var breakpointedFilePath = Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "MyClass.cs");
		debugProtocolHost
			.WithBreakpointsRequest([11], breakpointedFilePath)
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(process.Id, startSuspended);

		await debugProtocolHost.WaitForStoppedEvent(debugEventTcs);

		var allModules = debugProtocolHost.SendRequestSync(new ModulesRequest());
		allModules.TotalModules.Should().Be(allModules.Modules.Count);
		allModules.Modules.Should().NotBeEmpty();
		allModules.Modules.Should().OnlyContain(module =>
			!string.IsNullOrWhiteSpace(module.Name) &&
			!string.IsNullOrWhiteSpace(module.Path));

		var page = debugProtocolHost.SendRequestSync(new ModulesRequest
		{
			StartModule = 1,
			ModuleCount = 1
		});
		page.TotalModules.Should().Be(allModules.TotalModules);
		page.Modules.Should().ContainSingle();
		page.Modules[0].Should().BeEquivalentTo(allModules.Modules[1]);

		var unboundedPage = debugProtocolHost.SendRequestSync(new ModulesRequest
		{
			StartModule = 1,
			ModuleCount = 0
		});
		unboundedPage.TotalModules.Should().Be(allModules.TotalModules);
		unboundedPage.Modules.Should().BeEquivalentTo(allModules.Modules.Skip(1));
	}
}
