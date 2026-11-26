using System.Runtime.Versioning;

// The module under test is Windows-only at run time; the integration tests skip themselves elsewhere, the unit tests do not touch native code.
[assembly: SupportedOSPlatform("windows")]
