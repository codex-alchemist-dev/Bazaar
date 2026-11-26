using Bazaar.Client.Athanor.Drafts;

namespace Bazaar.Client.Athanor.Tests.Drafts;

public sealed class InMemoryEditorDraftStoreTests : EditorDraftStoreContract
{
    private readonly ManualClock _clock = new();
    private readonly IEditorDraftStore _store;

    public InMemoryEditorDraftStoreTests()
    {
        _store = new InMemoryEditorDraftStore(_clock);
    }

    protected override ManualClock Clock => _clock;

    protected override IEditorDraftStore Store => _store;
}
