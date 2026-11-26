#pragma warning disable CA1861 // Test data is written inline as constant arrays.
using Bazaar.Client.Calcinato.Modal;

namespace Bazaar.Client.Calcinato.Tests.Modal;

public sealed class ModalKeymapTests
{
    [Theory]
    [InlineData('h', ModalAction.MoveLeft)]
    [InlineData('w', ModalAction.WordForward)]
    [InlineData('b', ModalAction.WordBackward)]
    [InlineData('G', ModalAction.GoBottom)]
    [InlineData('B', ModalAction.PageUp)]
    [InlineData('f', ModalAction.PageDown)]
    [InlineData('n', ModalAction.SearchNext)]
    [InlineData('N', ModalAction.SearchPrevious)]
    [InlineData(':', ModalAction.CommandLine)]
    [InlineData('y', ModalAction.Yank)]
    public void DefaultBindingsCoverTheMotionSet(char key, ModalAction action)
    {
        var map = ModalKeymap.CreateDefault();

        Assert.True(map.TryGetAction(key, out var bound));
        Assert.Equal(action, bound);
    }

    [Theory]
    [InlineData('x')]
    [InlineData('z')]
    [InlineData('Q')]
    [InlineData('1')]
    public void UnboundCharactersAreFreeForMnemonics(char key)
    {
        Assert.False(ModalKeymap.CreateDefault().TryGetAction(key, out _));
    }

    [Theory]
    [InlineData('1')]
    [InlineData('9')]
    [InlineData(' ')]
    [InlineData('\n')]
    [InlineData('é')]
    public void InvalidKeysCannotBeBound(char key)
    {
        Assert.Throws<ArgumentException>(() => new ModalKeymap().Bind(key, ModalAction.MoveLeft));
    }

    [Fact]
    public void ZeroCanBeBound()
    {
        var map = new ModalKeymap();

        map.Bind('0', ModalAction.LineStart);

        Assert.True(map.TryGetAction('0', out var action));
        Assert.Equal(ModalAction.LineStart, action);
    }

    [Fact]
    public void BindReplacesAndUnbindFrees()
    {
        var map = ModalKeymap.CreateDefault();

        map.Bind('n', ModalAction.MoveDown);
        Assert.Equal(ModalAction.MoveDown, Get(map, 'n'));

        Assert.True(map.Unbind('n'));
        Assert.False(map.TryGetAction('n', out _));
        Assert.False(map.Unbind('n'));
    }

    [Fact]
    public void CloneIsIndependent()
    {
        var map = ModalKeymap.CreateDefault();
        var copy = map.Clone();

        copy.Unbind('j');

        Assert.True(map.TryGetAction('j', out _));
        Assert.False(copy.TryGetAction('j', out _));
    }

    [Fact]
    public void BindingsViewReflectsChanges()
    {
        var map = new ModalKeymap();
        var view = map.Bindings;

        map.Bind('h', ModalAction.MoveLeft);

        Assert.Single(view);
    }

    private static ModalAction Get(ModalKeymap map, char key)
    {
        Assert.True(map.TryGetAction(key, out var action));
        return action;
    }
}
