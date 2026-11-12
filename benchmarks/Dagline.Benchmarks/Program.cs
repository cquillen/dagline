using BenchmarkDotNet.Running;

// With no arguments every scenario runs. Arguments are BenchmarkDotNet's own, such as --filter *Warnings*.
string[] arguments = args.Length == 0 ? ["--filter", "*"] : args;

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(arguments);
