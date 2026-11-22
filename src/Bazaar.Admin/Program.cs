using Bazaar.Admin;

// The entry point only. Parsing, dispatch and output live in AdminCommandLine so the tests can capture them.
return await AdminCommandLine.RunAsync(args, Console.Out, Console.Error);
