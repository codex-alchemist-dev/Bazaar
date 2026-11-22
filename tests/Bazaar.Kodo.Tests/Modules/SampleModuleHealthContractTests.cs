using Bazaar.Kodo.Testing;

namespace Bazaar.Kodo.Tests.Modules;

/// <summary>
/// Proves the xUnit base class runs for a module that inherits it. A real module's test project does the same
/// with its own contract; no real module is wired in this slice.
/// </summary>
internal sealed class SampleModuleHealthContractTests : ModuleHealthContractTests<FakeModuleHealthProbe>
{
    protected override string ExpectedModuleId => "kodo";

    protected override FakeModuleHealthProbe CreateContract() => new("kodo");
}
