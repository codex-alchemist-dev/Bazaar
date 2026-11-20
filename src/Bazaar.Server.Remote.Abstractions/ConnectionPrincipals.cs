namespace Bazaar.Server.Wire.Connection;

/// <summary>An authenticated principal (a human or an agent).</summary>
/// <param name="Id">Principal id.</param>
/// <param name="Kind">Principal kind, such as <c>human</c> or <c>agent</c>.</param>
public sealed record PrincipalRef(Guid Id, string Kind);

/// <summary>The device a client says it is.</summary>
/// <param name="DeviceId">Device id from <c>hello</c>.</param>
/// <param name="DeviceClass">Device class from <c>hello</c>.</param>
/// <param name="ClientBuild">Client build from <c>hello</c>.</param>
public sealed record DeviceInfo(string DeviceId, string DeviceClass, string ClientBuild);
