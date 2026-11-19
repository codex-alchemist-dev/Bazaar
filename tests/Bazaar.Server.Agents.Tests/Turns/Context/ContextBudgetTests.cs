using Bazaar.Server.Agents.Turns.Context;

namespace Bazaar.Server.Tests.Agents.Turns.Context;

public class ContextBudgetTests
{
    [Fact]
    public void Defaults_are_the_documented_runtime_budget_values()
    {
        var budget = ContextBudget.Defaults;

        Assert.Equal(4000, budget.SystemTokens);
        Assert.Equal(4000, budget.MemoryTokens);
        Assert.Equal(2000, budget.TaskTokens);
        Assert.Equal(4000, budget.VaultTokens);
        Assert.Equal(12000, budget.BackreadTokens);
    }

    [Fact]
    public void For_maps_each_budgeted_section_to_its_ceiling()
    {
        var budget = new ContextBudget(1, 2, 3, 4, 5);

        Assert.Equal(1, budget.For(ContextSection.System));
        Assert.Equal(2, budget.For(ContextSection.Memory));
        Assert.Equal(3, budget.For(ContextSection.TaskState));
        Assert.Equal(4, budget.For(ContextSection.Vault));
        Assert.Equal(5, budget.For(ContextSection.GcBackread));
    }

    [Fact]
    public void Inbox_has_no_ceiling()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ContextBudget.Defaults.For(ContextSection.Inbox));
    }

    [Fact]
    public void A_negative_ceiling_is_refused_by_the_assembler()
    {
        var budget = new ContextBudget(4000, 4000, -1, 4000, 12000);

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new TurnContextAssembler([], budget));

        Assert.Equal("TaskTokens", ex.ParamName);
    }
}
