# Coding standards

How C# in this repository is factored. The rules bind writing and review alike.

Two other sources also apply and are not repeated here:

- `.editorconfig` and the build analyzers: formatting, naming and style.
- The Rules section of `AGENTS.md`: package boundaries, tests, comments and docs.

Where all three are silent, Microsoft's [Framework Design Guidelines](https://learn.microsoft.com/dotnet/standard/design-guidelines/) and [C# coding conventions](https://learn.microsoft.com/dotnet/csharp/fundamentals/coding-style/coding-conventions) decide.

## Severity

- **Hard**: a breach is fixed before merge. Every rule below is hard unless marked otherwise. An exception is written into this file in the same PR, usually under Endorsed patterns, so the next review sees it.
- **Judgement**: the rule is the default, and a departure is justified. The author gives the reason in a `//` comment at the site, or in the PR description when the departure has no single site. A departure with no reason given, or with a reason that does not hold, is fixed before merge.

A departure from the Microsoft guidelines is a judgement call.

## Factoring

- **One home per decision** (judgement). Each rule, format and calculation is written once in production code and called from everywhere else. The same logic shape twice under `src/` is a departure, in one file or across packages.
- **One answer per question.** A question asked of a closed set (a status, a dependency kind, a run outcome) is answered by one member, such as `IsSettled`, and every caller uses it. The same `switch` or pattern over that set at two sites is a breach.
- **Changes travel together** (judgement). Adding one concept (a status, an option, an event) is one edit in the type that owns it, plus whatever genuinely new behaviour its callers need. One logical change that repeats the same kind of edit across three or more production files is a departure: gather that knowledge into one module first.
- **Glossary names.** An identifier for a domain concept uses that concept's `GLOSSARY.md` term. Naming a concept with a term its own entry lists under _Avoid_ is a breach.
- **One reason to change** (judgement). A type serves one purpose that fits in a sentence without "and". A file edited in one PR for unrelated reasons is ready to split.
- **One level per method** (judgement). A method either coordinates steps or performs one. Look closer at a method over 40 lines, nesting deeper than three levels, more than four parameters, or a `bool` parameter that selects behaviour. These are triggers to look, not departures in themselves.
- **Types for glossary concepts** (judgement). A concept with rules of its own, such as a path or a segment, becomes a small type once validated, so the rule is checked in one place. Raw strings stop at the public boundary.

## Types and surface

- **Smallest surface.** A new type is `internal`, and a new concrete class or record is also `sealed`. A type becomes `public` when a consumer needs it, and unsealed when a derived type exists in the same change. A member goes onto the snapshot types only when it answers a question by a rule the core owns (an ADR or glossary rule, or a join over snapshot data) and a reporter in this repository asks it; wording, number and duration formats, and ordering stay with each reporter.
- **Abstractions are earned.** An interface or abstract class has two implementations in the repository, or is a documented consumer extension point, or is a seam named in an ADR. A test double counts as the second implementation only when the real one reaches outside the process (clock, console, file system).
- **Immutable by default.** Data is a `record` or has get-only and `init` members. Mutable state has one owning type, which is the only code that changes it.
- **Only Dagline creates what it hands out.** A public type that only Dagline creates, such as an event or a value on the snapshot, has an internal constructor and get-only properties and is not positional, so that a member can be added to it later without breaking a consumer. It may stay a `record` when value equality means something for it. One with no meaningful value equality, such as a run's result or a node's snapshot, is a sealed class instead: the exception to _data is a record_. Options that a consumer builds, `RunOptions` and `SpectreOptions`, stay records, so that one can be derived from another with `with`.
- **Tests use the public surface.** Tests reach internal types through the public API. `InternalsVisibleTo` is a breach: an internal that cannot be reached that way is a design problem to fix. The one exception is the journal write hook under _Tests_, for which `src/Dagline` names `tests/Dagline.TestSupport` and no other assembly.
- **State is passed in.** A type receives what it depends on through its constructor or parameters. A mutable `static` is a breach.
- **Time is injected.** Code under `src/` takes time from an injected `TimeProvider`: the current time, delays, sleeps and elapsed durations alike. `src/BannedSymbols.txt` lists the clock APIs the build rejects.

## SOLID

Design follows the SOLID principles. Each one applies here through a rule a reviewer can check:

- **Single responsibility** is _One reason to change_.
- **Open/closed** (judgement). Behaviour that varies by consumer or by reporter sits behind an extension point, so a new variant adds a type and leaves the engine untouched. For a closed set, _One answer per question_ keeps the change to one place.
- **Liskov substitution** (judgement). An implementation honours the whole contract it implements: it accepts every input the contract allows and provides every member. A member that throws `NotSupportedException` or `NotImplementedException` is a departure, unless it implements a framework contract that documents the exception, such as `Stream.Seek`.
- **Interface segregation** (judgement). An interface holds only the members every implementer provides and its callers use together. One whose callers each use a different half is ready to split.
- **Dependency inversion** is _State is passed in_, within the limit of _Abstractions are earned_: a type depends on an abstraction where one is earned, and directly on the concrete type otherwise.

## Async and failure

- **Async all the way.** Asynchronous work is awaited. `.Result`, `.Wait()`, `GetAwaiter().GetResult()` and `async void` are breaches.
- **Awaits capture no context.** Under `src/`, every `await` ends in `ConfigureAwait(false)`, so a continuation is never posted back to the caller's synchronization context. The build rejects a missing call. `ConfigureAwait(true)` passes the build, so review treats it as a breach.
- **Cancellation flows through.** A method that awaits takes a `CancellationToken` as its last parameter and passes it to every awaited call that accepts one. An `await foreach` passes it with `WithCancellation`, unless the stream was created with the token. A signature fixed by an interface or base type, such as `DisposeAsync`, is exempt.
- **Fail at the boundary.** A public member validates its arguments on entry with the `ThrowIf` helpers and throws the most specific exception type.
- **Catch what you handle.** A `catch` names the exception it can deal with. A catch-all belongs only where consumer code is invoked, to record an unexpected failure.

## Tests

_Tests use the public surface_ (above) and the test rules in `AGENTS.md` apply as well.

- **A test is a whole run.** A behaviour of the engine is tested by building a small graph, running it, and asserting on what a reporter received and on the final snapshot. A value type with rules of its own, such as a path, is tested directly. No public seam is added only so that a test can reach something.
- **No test waits on real time.** A test never sleeps, and never asserts that nothing happened within a duration. A task body that must be caught mid-flight pauses until the test releases it. Order is asserted on the recorded events once the run has ended, and a limit or the max running tasks on the highest number of task bodies seen running at once. Real time appears only as the timeout that fails a stuck test: 30 seconds unless the test states another.
- **Time is faked.** A test that controls time passes Microsoft's `FakeTimeProvider` to the run. That fake cannot say when a timer is pending, so the test advances it in steps until a signal the test owns completes, such as a check having run a second time. A test in which time is not the subject may use the clock in `Dagline.Testing`, on which every finite delay ends at once. The package is referenced by test projects only: ADR 0013 keeps `Microsoft.Extensions.*` out of `src/Dagline`.
- **Races are repeated** (judgement). A test of a stop, the grace period, a limit, the max running tasks, or a task body that ends after the run has let go of it repeats 50 times in every run of the suite. One that writes a real journal each time is heavy on the disk, and is marked `[NotInParallel(DiskHeavy.Key)]` with the other heavy tests of its project so that they take the disk in turns: run together on a slow disk, each ends only when nearly all of them have, past the timeout.
- **Journals are real files.** A journal test writes to a temporary directory of its own, and there is no file system abstraction. The one exception is the journal write hook below, which wraps the stream of one real journal file and still writes that file up to the failure. A killed process is simulated by cutting the journal of a real run short, at each line boundary and inside a line, and continuing what is left.
- **Frames come from the display.** A frame is asserted as lines of text by forcing the display on over a captured output of a fixed size. The snapshot behind it comes from a real run whose task bodies are paused, never from one built by hand.
- **Shared support is not shipped.** A helper that both test projects use and a consumer would not lives in `tests/Dagline.TestSupport`, which holds no tests and is never packed. A helper a consumer would want belongs in `Dagline.Testing`.

One internal test hook makes a journal write fail during a run. `Journal.WithFailingWrite` in `src/Dagline` returns the same journal choice, whose file, once opened, writes through a `FailingWriteStream`: every write and flush reaches the real file except the first write a predicate over its text picks, which throws an `IOException`. The hook applies to that one journal, with no static or ambient state and nothing on `RunSettings`, and it sits below `JournalFile.Append`, so the recorder's catch and its guard on writing nothing after a failure run as in production. It is opened by the core's only `InternalsVisibleTo`, to `tests/Dagline.TestSupport`, where `JournalWriteFailure` is its one caller: a public helper that names the write to fail, such as a task's settle, and the only test code that knows the journal's line format. Test projects reach the hook through that helper, never directly, and the hook is used for nothing else.

## Endorsed patterns

These are deliberate here and override general code-smell heuristics:

- **Fluent chains.** LINQ, builder APIs and Spectre.Console calls chain by design.
- **Repetition in tests.** Each test reads top to bottom on its own, so repeated arrange code under `tests/` is fine. Extract a helper when it names a concept, not to save lines.
- **ADR seams.** A seam recorded in `docs/adr/` is a decision already made, even while it has one implementation.
- **A run takes no token.** `Runner.RunAsync` takes no `CancellationToken`, and neither do the private methods that carry a run to its end and invoke a task body. This is an exception to _Cancellation flows through_: a run is not stopped by cancelling a token, and a task body's signature has no token parameter, so there is no token to pass on. The same holds for the run methods of `TaskHarness` in `Dagline.Testing` and the private method behind them: each carries one run to its end, the run is stopped through its stop signal, and the test's own timeout is what fails a stuck test. A call that does accept a token, such as a reporter's `OnEventAsync`, is still passed one.
- **The app's console may throw anything.** In `Dagline.Spectre`, the display catches `Exception` around every write to the `IAnsiConsole` and the plain-text `TextWriter` the consumer supplied. These are consumer code under _Catch what you handle_: no exception type can be named for them, and a failure there must restore the terminal or fall back to plain text rather than end the run's use of the reporter. Each such `catch` says in a `//` comment what it does with the failure.
- **A snapshot member a spec asks for.** A member that a spec asks for on behalf of consumers' reporters goes onto the snapshot types though no reporter in this repository asks it, as an exception to _Smallest surface_. It still answers by a rule the core owns, and the spec names the question it answers. `Snapshot.EarlierSuccesses` is one: the reporters here need only its count, and print what they did before.
- **Samples.** Code under `samples/` is written for a newcomer to read: inline, in few files, with minimal abstraction. Graph building stays inline, so the shape of the graph reads top to bottom. A task body that is tested alone is a named method, because a test cannot reach a lambda written inside the graph-building code.
