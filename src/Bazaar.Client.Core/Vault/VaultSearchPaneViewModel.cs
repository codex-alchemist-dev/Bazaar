using System.Text.Json;
using Bazaar.Client.Core.Panes;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Core.Vault;

/// <summary>
/// The Vault search pane (doc 18). The model names the Vault: <c>{ "vault": "&lt;guid&gt;" }</c>. Holds the query, the last
/// result set and the open document; it references no Avalonia control, so the head-neutral view builds from it.
/// </summary>
public sealed class VaultSearchPaneViewModel : IPaneViewModel
{
    private readonly VaultApi _api;
    private readonly Guid _vaultId;
    private int _searchGeneration;

    public VaultSearchPaneViewModel(PaneDescriptor descriptor, VaultApi api)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        _api = api ?? throw new ArgumentNullException(nameof(api));

        Kind = descriptor.Kind;
        Title = descriptor.Title;
        _vaultId = ParseVault(descriptor.Model);
    }

    public string Kind { get; }

    public string Title { get; }

    /// <summary>The text in the search box.</summary>
    public string Query { get; set; } = string.Empty;

    /// <summary>Cards from the last search, best first.</summary>
    public IReadOnlyList<VaultCardDto> Results { get; private set; } = [];

    /// <summary>True when the last answer used no embedding (the response says so).</summary>
    public bool LexicalOnly { get; private set; }

    /// <summary>What the user needs to read next: a count, a refusal, or an error. Null when there is nothing to say.</summary>
    public string? StatusMessage { get; private set; }

    /// <summary>The card whose body is shown, or null.</summary>
    public VaultCardDto? OpenedCard { get; private set; }

    /// <summary>The opened document's body. Null until a card is opened.</summary>
    public string? OpenedBody { get; private set; }

    /// <summary>Raised after any change a view should redraw from.</summary>
    public event EventHandler? Changed;

    /// <summary>Runs a search for <see cref="Query"/>. A newer search supersedes an older one still in flight.</summary>
    public async Task SearchAsync(CancellationToken cancellationToken)
    {
        var text = Query.Trim();
        var generation = Interlocked.Increment(ref _searchGeneration);
        if (text.Length == 0)
        {
            Results = [];
            LexicalOnly = false;
            OpenedCard = null;
            OpenedBody = null;
            StatusMessage = "Type a question to search the Vault.";
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        try
        {
            var response = await _api.SearchAsync(_vaultId, text, null, null, 10, cancellationToken);
            if (generation != _searchGeneration) return;

            Results = response.Cards;
            LexicalOnly = response.LexicalOnly;
            StatusMessage = response.Cards.Length == 0
                ? "No matches you can read."
                : $"{response.Cards.Length} match{(response.Cards.Length == 1 ? string.Empty : "es")}"
                  + (response.LexicalOnly ? " (text match only; no embedding profile)." : ".");
        }
        catch (VaultApiException ex)
        {
            if (generation != _searchGeneration) return;
            Results = [];
            StatusMessage = ex.Status switch
            {
                System.Net.HttpStatusCode.Unauthorized => "Sign in again to search the Vault.",
                System.Net.HttpStatusCode.ServiceUnavailable => "Vault search is not available on this server.",
                _ => $"Search failed (HTTP {(int)ex.Status}).",
            };
        }
        catch (HttpRequestException)
        {
            if (generation != _searchGeneration) return;
            Results = [];
            StatusMessage = "Could not reach the server.";
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Loads one card's body into <see cref="OpenedBody"/>.</summary>
    public async Task OpenAsync(VaultCardDto card, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(card);
        try
        {
            OpenedBody = await _api.FetchBodyAsync(_vaultId, card.DocId, card.Ordinal, cancellationToken);
            OpenedCard = card;
        }
        catch (VaultApiException ex) when (ex.Status == System.Net.HttpStatusCode.NotFound)
        {
            OpenedCard = card;
            OpenedBody = "This document is not available to you.";
        }
        catch (VaultApiException ex)
        {
            OpenedCard = card;
            OpenedBody = $"Could not open the document (HTTP {(int)ex.Status}).";
        }
        catch (HttpRequestException)
        {
            OpenedCard = card;
            OpenedBody = "Could not reach the server.";
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static Guid ParseVault(JsonElement model)
    {
        var raw = PaneValues.RequiredString(model, "vault");
        if (!Guid.TryParse(raw, out var id)) throw new PaneModelException("model field 'vault' must be a GUID");
        return id;
    }
}
