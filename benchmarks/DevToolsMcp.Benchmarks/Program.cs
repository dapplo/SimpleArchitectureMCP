using BenchmarkDotNet.Running;

BenchmarkSwitcher.FromAssembly(typeof(DevToolsMcp.Benchmarks.Samples).Assembly).Run(args);
