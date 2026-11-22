using Bazaar.Server.Portability.Verifier;

// Exit codes: 0 verified, 1 failed, 2 usage. All logic lives in the library so tests can reach it.
return VerifierCli.Run(args, Console.Out, Console.Error);
