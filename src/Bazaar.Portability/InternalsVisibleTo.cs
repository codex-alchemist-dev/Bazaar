using System.Runtime.CompilerServices;

// Lets the portability tests reach the deterministic cipher overload used for the checked-in test vector.
[assembly: InternalsVisibleTo("Bazaar.Portability.Tests")]
