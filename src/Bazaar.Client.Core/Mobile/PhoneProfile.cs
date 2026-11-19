namespace Bazaar.Client.Core.Mobile;

/// <summary>
/// The layout profile the phone shows: the server revision and the pane kinds in pager order (doc 29, doc 27).
/// It is the only data the offline cache keeps. It holds no message bodies and no GC content (DECISIONS-LOG C27).
/// </summary>
/// <remarks>
/// The server's <c>UiProfile</c> (doc 02) is not in the codebase yet. This record carries the part the phone pager
/// needs today; the full profile maps onto it when the wire types land.
/// </remarks>
public sealed record PhoneProfile(long Revision, IReadOnlyList<string> PaneKinds);

/// <summary>A profile read back from the cache, with the time it was saved so the UI can show its age (doc 27).</summary>
public sealed record CachedProfile(PhoneProfile Profile, DateTimeOffset SavedAt);
