# Allocation benchmarks

BenchmarkDotNet benchmarks for the paths that handle large payloads. CI builds them but does not run them.

```bash
# BenchmarkDotNet 0.15 does not recognise net11.0 as a toolchain yet, so run in-process.
dotnet run -c Release --project benchmarks/SimpleArchitectureMcp.Benchmarks -- --filter '*' --inProcess
```

- `ConfluenceGetPageBenchmarks`: `confluence_get_page` end to end through the real module `HttpClient` pipeline
  (payload metrics, resilience), with a canned Data Center response instead of the network. `KnownLength=false`
  simulates a chunked or compressed response.
- `ConverterBenchmarks`: the Confluence storage ⇄ Markdown and Jira wiki ⇄ Markdown converters on their own.

`Sections=20` is about 35 KB of storage XHTML (a typical page). `Sections=300` is about 410 K characters, a very large
page whose strings land on the large object heap (LOH). Compare the `Allocated` and `Gen2` columns. Gen2 collections
per 1,000 operations are the best LOH proxy BenchmarkDotNet shows. The timings of a `--job short` run are too noisy
to compare.
