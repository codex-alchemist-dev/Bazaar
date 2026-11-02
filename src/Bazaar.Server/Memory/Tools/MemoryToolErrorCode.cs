namespace Bazaar.Server.Memory.Tools;

/// <summary>Why a memory tool call was refused. Messages never quote note content.</summary>
public enum MemoryToolErrorCode
{
    /// <summary>The request is malformed or names an unsupported value.</summary>
    Invalid,

    /// <summary>The caller may not use this operation for the agent named in the request.</summary>
    Forbidden,

    /// <summary>No note the caller may see has that id or path. Hidden notes look the same as missing ones.</summary>
    NotFound,

    /// <summary>SoulGuard refused the content.</summary>
    Rejected,
}
