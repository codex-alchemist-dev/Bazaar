namespace Bazaar.Contracts.Gateway;

/// <summary>
/// The answer to an <see cref="EmbeddingRequest"/>. <c>Vectors</c> has one vector per input, in input order.
/// <c>ModelId</c> is the model that served the call (the upstream's name for it, or the requested model id when the
/// upstream did not say). <c>InputTokens</c> is the input token count that was recorded in the call's usage row.
/// </summary>
public sealed record EmbeddingResult(
    IReadOnlyList<float[]> Vectors,
    string ModelId,
    long InputTokens);
