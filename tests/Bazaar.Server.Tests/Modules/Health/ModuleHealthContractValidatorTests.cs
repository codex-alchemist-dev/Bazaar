using Bazaar.Server.Modules.Health;

namespace Bazaar.Server.Tests.Modules.Health;

/// <summary>Contract validator: a module must declare all four probes (doc 42 section 11, D10). Pure.</summary>
public sealed class ModuleHealthContractValidatorTests
{
    private static readonly ModuleHealthProbe[] _all =
    [
        ModuleHealthProbe.Liveness,
        ModuleHealthProbe.Readiness,
        ModuleHealthProbe.Dependencies,
        ModuleHealthProbe.SelfTest,
    ];

    [Fact]
    public void Module_declaring_all_four_probes_is_valid()
    {
        var result = ModuleHealthContractValidator.Validate(new ModuleHealthDeclaration("postgresql", _all));

        Assert.True(result.IsValid);
        Assert.Empty(result.MissingProbes);
        Assert.Empty(result.Problems);
    }

    [Theory]
    [InlineData(ModuleHealthProbe.Liveness)]
    [InlineData(ModuleHealthProbe.Readiness)]
    [InlineData(ModuleHealthProbe.Dependencies)]
    [InlineData(ModuleHealthProbe.SelfTest)]
    public void Validator_rejects_a_module_missing_one_probe(ModuleHealthProbe missing)
    {
        var declared = _all.Where(p => p != missing).ToArray();

        var result = ModuleHealthContractValidator.Validate(new ModuleHealthDeclaration("third-party", declared));

        Assert.False(result.IsValid);
        Assert.Equal([missing], result.MissingProbes);
        Assert.Single(result.Problems);
    }

    [Fact]
    public void Module_with_no_health_block_is_rejected_for_all_four_probes()
    {
        var result = ModuleHealthContractValidator.Validate(new ModuleHealthDeclaration("gc", null));

        Assert.False(result.IsValid);
        Assert.Equal(_all, result.MissingProbes);
    }

    [Fact]
    public void Duplicate_declared_probes_do_not_count_twice()
    {
        var declared = new[] { ModuleHealthProbe.Liveness, ModuleHealthProbe.Liveness, ModuleHealthProbe.Readiness, ModuleHealthProbe.Dependencies };

        var result = ModuleHealthContractValidator.Validate(new ModuleHealthDeclaration("kanban", declared));

        Assert.False(result.IsValid);
        Assert.Equal([ModuleHealthProbe.SelfTest], result.MissingProbes);
    }

    [Fact]
    public void Blank_module_id_is_rejected_even_with_all_probes()
    {
        var result = ModuleHealthContractValidator.Validate(new ModuleHealthDeclaration(" ", _all));

        Assert.False(result.IsValid);
        Assert.Contains(result.Problems, p => p.Contains("id is blank", StringComparison.Ordinal));
    }

    [Fact]
    public void Required_probe_list_names_the_four_probes_in_doc_order()
    {
        Assert.Equal(_all, ModuleHealthContractValidator.RequiredProbes);
    }
}
