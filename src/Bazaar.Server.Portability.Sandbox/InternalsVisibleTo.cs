using System.Runtime.CompilerServices;

// Lets the sandbox tests reach the Linux process guard to check it never signals an exited child's group.
[assembly: InternalsVisibleTo("Bazaar.Server.Portability.Sandbox.Tests")]
