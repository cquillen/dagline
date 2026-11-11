# Coding standards

How C# in this repository is factored. The rules bind writing and review alike.

Two other sources also apply and are not repeated here:

- `.editorconfig` and the build analyzers: formatting, naming and style.
- The Rules section of `AGENTS.md`: package boundaries, tests, comments and docs.

Where all three are silent, Microsoft's [Framework Design Guidelines](https://learn.microsoft.com/dotnet/standard/design-guidelines/) and [C# coding conventions](https://learn.microsoft.com/dotnet/csharp/fundamentals/coding-style/coding-conventions) decide.

## Severity

- **Hard**: a breach is fixed before merge. Every rule below is hard unless marked otherwise.
- **Judgement**: a prompt to look closer. The author may have a good reason, and gives it in the PR.

An exception to a hard rule is written into this file in the same PR, usually under Endorsed patterns, so the next review sees it.

A departure from the Microsoft guidelines is a judgement call.

## Factoring

- **One home per decision.** Each rule, format and calculation is written once in production code and called from everywhere else. The same logic shape twice under `src/` is a breach, in one file or across packages.
- **One answer per question.** A question asked of a closed set (a status, a dependency kind, a run outcome) is answered by one member, such as `IsSettled`, and every caller uses it. The same `switch` or pattern over that set at two sites is a breach.
- **Changes travel together.** Adding one concept (a status, an option, an event) is one edit in the type that owns it, plus whatever genuinely new behaviour its callers need. When one logical change repeats the same kind of edit across three or more production files, gather that knowledge into one module first.
- **Glossary names.** An identifier for a domain concept uses that concept's `GLOSSARY.md` term. Naming a concept with a term its own entry lists under _Avoid_ is a breach.
- **One reason to change** (judgement). A type serves one purpose that fits in a sentence without "and". A file edited in one PR for unrelated reasons is ready to split.
- **One level per method** (judgement). A method either coordinates steps or performs one. Look closer at a method over 40 lines, nesting deeper than three levels, more than four parameters, or a `bool` parameter that selects behaviour.
- **Types for glossary concepts** (judgement). A concept with rules of its own, such as a path or a segment, becomes a small type once validated, so the rule is checked in one place. Raw strings stop at the public boundary.

## Types and surface

- **Smallest surface.** A new type is `internal`, and a new concrete class or record is also `sealed`. A type becomes `public` when a consumer needs it, and unsealed when a derived type exists in the same change.
- **Abstractions are earned.** An interface or abstract class has two implementations in the repository, or is a documented consumer extension point, or is a seam named in an ADR. A test double counts as the second implementation only when the real one reaches outside the process (clock, console, file system).
- **Immutable by default.** Data is a `record` or has get-only and `init` members. Mutable state has one owning type, which is the only code that changes it.
- **Tests use the public surface.** Tests reach internal types through the public API. `InternalsVisibleTo` is a breach: an internal that cannot be reached that way is a design problem to fix.
- **State is passed in.** A type receives what it depends on through its constructor or parameters. A mutable `static` is a breach.
- **Time is injected.** Code under `src/` takes time from an injected `TimeProvider`: the current time, delays, sleeps and elapsed durations alike. `src/BannedSymbols.txt` lists the clock APIs the build rejects.

## SOLID

Design follows the SOLID principles. Each one applies here through a rule a reviewer can check:

- **Single responsibility** is _One reason to change_.
- **Open/closed** (judgement). Behaviour that varies by consumer or by reporter sits behind an extension point, so a new variant adds a type and leaves the engine untouched. For a closed set, _One answer per question_ keeps the change to one place.
- **Liskov substitution.** An implementation honours the whole contract it implements: it accepts every input the contract allows and provides every member. A member that throws `NotSupportedException` or `NotImplementedException` is a breach.
- **Interface segregation** (judgement). An interface holds only the members every implementer provides and its callers use together. One whose callers each use a different half is ready to split.
- **Dependency inversion** is _State is passed in_, within the limit of _Abstractions are earned_: a type depends on an abstraction where one is earned, and on a sealed concrete type otherwise.

## Async and failure

- **Async all the way.** Asynchronous work is awaited. `.Result`, `.Wait()`, `GetAwaiter().GetResult()` and `async void` are breaches.
- **Awaits capture no context.** Under `src/`, every `await` ends in `ConfigureAwait(false)`, so a continuation is never posted back to the caller's synchronization context. The build rejects a missing call. `ConfigureAwait(true)` passes the build, so review treats it as a breach.
- **Cancellation flows through.** A method that awaits takes a `CancellationToken` as its last parameter and passes it to every awaited call that accepts one. An `await foreach` passes it with `WithCancellation`, unless the stream was created with the token. A signature fixed by an interface or base type, such as `DisposeAsync`, is exempt.
- **Fail at the boundary.** A public member validates its arguments on entry with the `ThrowIf` helpers and throws the most specific exception type.
- **Catch what you handle.** A `catch` names the exception it can deal with. A catch-all belongs only where consumer code is invoked, to record an unexpected failure.

## Endorsed patterns

These are deliberate here and override general code-smell heuristics:

- **Fluent chains.** LINQ, builder APIs and Spectre.Console calls chain by design.
- **Repetition in tests.** Each test reads top to bottom on its own, so repeated arrange code under `tests/` is fine. Extract a helper when it names a concept, not to save lines.
- **ADR seams.** A seam recorded in `docs/adr/` is a decision already made, even while it has one implementation.
- **Samples.** Code under `samples/` is written for a newcomer to read: inline, in few files, with minimal abstraction.
