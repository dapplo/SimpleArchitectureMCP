using BenchmarkDotNet.Running;

BenchmarkSwitcher.FromAssembly(typeof(SimpleArchitectureMcp.Benchmarks.Samples).Assembly).Run(args);
