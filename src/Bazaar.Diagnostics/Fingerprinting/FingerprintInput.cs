namespace Bazaar.Diagnostics.Fingerprinting;

/// <summary>One stack frame as the fingerprint sees it. Line numbers, IL offsets and build ids are not part of the key.</summary>
/// <param name="ModuleId">The module that declares the method.</param>
/// <param name="Type">The declaring type, as the runtime names it.</param>
/// <param name="Method">The method name, as the runtime names it. Parameter lists are ignored.</param>
/// <param name="App">True for a Bazaar module frame.</param>
public sealed record FingerprintFrame(string ModuleId, string Type, string Method, bool App);

/// <summary>The inputs of the <c>fp1</c> fingerprint (doc 43 section 8.2).</summary>
/// <param name="ErrorCode">The registered error code, or null for an unhandled exception.</param>
/// <param name="ErrorType">The exception type's full name.</param>
/// <param name="Frames">The stack, top frame first.</param>
/// <param name="Message">The exception message. Used only when <paramref name="ErrorCode"/> is null.</param>
public sealed record FingerprintInput(string? ErrorCode, string ErrorType, IReadOnlyList<FingerprintFrame> Frames, string? Message);
