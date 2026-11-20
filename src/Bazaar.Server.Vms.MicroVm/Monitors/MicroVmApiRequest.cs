namespace Bazaar.Server.Vms.MicroVm.Monitors;

/// <summary>One REST call to a monitor: method, path and an exact JSON body (null for no body).</summary>
public sealed record MicroVmApiRequest(HttpMethod Method, string Path, string? Body);

/// <summary>A monitor's reply.</summary>
public sealed record MicroVmApiResponse(int StatusCode, string Body)
{
    public bool IsSuccess => StatusCode is >= 200 and < 300;
}
