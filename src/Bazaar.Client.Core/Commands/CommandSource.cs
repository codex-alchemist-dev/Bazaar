namespace Bazaar.Client.Core.Commands;

/// <summary>Where a command comes from. Server commands arrive in the UI profile.</summary>
public enum CommandSource
{
    Local,
    Server,
}
