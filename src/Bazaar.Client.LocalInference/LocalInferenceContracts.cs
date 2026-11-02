namespace Bazaar.Client.LocalInference;

/// <summary>Kinds of on-device task (doc 31, Design). Each has a fixed prompt template, an input cap and an output cap.</summary>
public enum LocalTaskKind { ChipSummary, VoiceCleanup, PromptRewrite, UiAssist }

/// <summary>Where a session actually runs. <c>Fake</c> is for tests only.</summary>
public enum BackendTier { Npu, Gpu, Cpu, Fake }

public sealed record LocalTaskRequest(LocalTaskKind Kind, string Input, int MaxOutputTokens, string CacheKey);

public sealed record LocalTaskResult(string Text, BackendTier Tier, bool Degraded, TimeSpan Elapsed);

/// <summary>A runnable session for one model on one execution provider.</summary>
public interface ILocalModelBackend : IAsyncDisposable
{
    BackendTier Tier { get; }

    Task<LocalTaskResult> RunAsync(LocalTaskRequest request, CancellationToken ct);
}

/// <summary>Signed model manifest (doc 31, Model packaging). Only manifest-listed models can be selected.</summary>
public sealed record ModelManifest(
    string Model,
    string Version,
    string License,
    IReadOnlyList<ManifestFile> Files,
    string MinRuntime,
    string? Signature);

public sealed record ManifestFile(string Name, string Sha256, long Bytes);

/// <summary>Identifies one compiled or loaded session: which model, which provider, which driver and runtime.</summary>
public sealed record SessionInfo(string ModelSha256, BackendTier Tier, string ExecutionProvider, string? DriverVersion, string RuntimeVersion);

/// <summary>Result of reading an execution provider's placement report after compile (doc 31, Placement verification).</summary>
public sealed record PlacementReport(int TotalNodes, int NpuNodes, bool AttentionOrKvOnCpu)
{
    public double NpuShare => TotalNodes == 0 ? 0 : (double)NpuNodes / TotalNodes;
}

public interface IPlacementVerifier
{
    PlacementReport Verify(SessionInfo session);
}

public interface IExecutionProviderSelector
{
    Task<ILocalModelBackend> SelectAsync(ModelManifest manifest, CancellationToken ct);
}
