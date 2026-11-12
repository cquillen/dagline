# Agent instructions

Dagline: an in-process .NET 10 library for running DAGs of tasks in CLI apps, with a Spectre.Console terminal UI.

## Commands

- Build: `dotnet build`
- Test all: `dotnet test` (TUnit on Microsoft.Testing.Platform)
- Test one: `dotnet test --project tests/Dagline.Tests -- --treenode-filter "/*/*/ClassName/MethodName"`
- Measure a run: `dotnet run -c Release --project benchmarks/Dagline.Benchmarks` (BenchmarkDotNet; minutes, not part of `dotnet test`)
- Pack: `dotnet pack -c Release` → `artifacts/package/release/`
- Docs preview: `dotnet tool restore && dotnet docfx docs/docfx.json --serve`

## Layout

- `src/Dagline`: core engine, NuGet package
- `src/Dagline.Spectre`: Spectre.Console UI, NuGet package
- `src/Dagline.Testing`: helpers for consumers' test projects (the task harness and the instant clock), NuGet package
- `tests/*.Tests`: one TUnit project per package, `tests/Dagline.Testing.Tests` among them
- `tests/Dagline.TestSupport`: helpers the test projects share (recording reporter, paused task bodies, fake clock helper); holds no tests, never packed
- `samples/`: runnable console apps
- `samples/Dagline.Samples.Batch`: the acceptance sample, about 1,500 tasks over 10 shards of 75 tenants, run against a fake entity store and a fake job scheduler
- `samples/Dagline.Samples.Batch.Tests`: the sample's three harness tests, written as a consumer's; the acceptance test of `Dagline.Testing` and the worked example its docs point to
- `benchmarks/Dagline.Benchmarks`: what a whole run costs in time and allocation, per scenario; never packed, holds no tests
- `docs/`: DocFX site, published to GitHub Pages

## Rules

- `src/Dagline` must never reference Spectre.Console or any UI or console library.
- `src/Dagline.Testing` references `Dagline` only: never a test framework, an assertion library, Spectre.Console or Microsoft's fake clock. It is built on Dagline's public API only: no `InternalsVisibleTo`, and `Dagline` gains no member for it.
- Package versions go only in `Directory.Packages.props`. No `Version=` on a `PackageReference`.
- Tests: TUnit `[Test]` with Shouldly assertions only (not TUnit's `Assert.That`).
- Nullable on, file-scoped namespaces, LF line endings, `.editorconfig` style.
- Every public type and member needs an XML doc comment (CS1591 is an error in CI). These comments become the published API reference: write them for that reader, with as much behavioural and implementation detail as they would want (ordering, concurrency, failure modes).
- Inside the code, names and structure are the documentation. Reserve `//` comments for the surprising: a non-obvious reason, a workaround, a constraint the code cannot show.
- Factoring and design rules live in `CODING_STANDARDS.md`. Read it before writing or reviewing C#.
- Any change to public API or behaviour updates `docs/` and `CHANGELOG.md` (`[Unreleased]`) in the same PR.
- Keep the GitHub Pages docs site current: each new feature gets user-facing docs in `docs/articles/` (new page or section, added to `toc.yml`), and replace TODO stubs as the features they describe land.
- Never commit to `main`. Use a branch and PR. Versions come from git tags (MinVer); never set `<Version>`.

## Agent skills

### Issue tracker

GitHub Issues for this repository, via the `gh` CLI. See `docs/agents/issue-tracker.md`.

### Triage labels

Default vocabulary: `needs-triage`, `needs-info`, `ready-for-agent`, `ready-for-human`, `wontfix`. See `docs/agents/triage-labels.md`.

### Domain docs

Single-context: root `GLOSSARY.md` plus `docs/adr/`, created lazily. See `docs/agents/domain.md`.
