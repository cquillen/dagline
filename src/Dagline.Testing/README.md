# Dagline.Testing

Test helpers for [Dagline](https://www.nuget.org/packages/Dagline) task authors, for test projects only. A harness runs one task body through a real run and hands back what the task left behind: its status, message, warnings, status text, waits and checkpoint. A scripted run starts a whole graph that the test pumps on its own thread, one happening at a time. An instant clock ends every finite delay at once, so a task that waits an hour in production takes milliseconds in a test.

It depends on `Dagline` alone: no test framework, no assertion library and no terminal library, so it works with whatever your test project already uses.

```shell
dotnet add package Dagline.Testing
```

## Example

```csharp
[Test]
public async Task The_export_reports_what_it_wrote()
{
    await using var harness = TaskHarness.For(async context =>
    {
        await Task.Delay(TimeSpan.FromHours(1), context.TimeProvider, context.CancellationToken);
        context.SetMessage("17 rows written");
    });

    var outcome = await harness.RunAsync();

    outcome.Status.ShouldBe(NodeStatus.Succeeded);
    outcome.Message.ShouldBe("17 rows written");
}
```

The hour-long delay ends at once on the harness's own clock, which the harness moves on whenever nothing else is left to do. The example uses TUnit and Shouldly, but any test framework and assertion library do the same job.

## Learn more

- [Testing your tasks](https://cquillen.github.io/dagline/articles/testing-your-tasks.html): the harness, its clock and guard, waiting tasks, checkpoints across runs, and scripted runs
- [A worked example](https://github.com/cquillen/dagline/tree/main/samples/Dagline.Samples.Batch.Tests): harness tests of realistic task bodies
- [Documentation and API reference](https://cquillen.github.io/dagline/)
- [Changelog](https://github.com/cquillen/dagline/blob/main/CHANGELOG.md)
- [Source](https://github.com/cquillen/dagline)
