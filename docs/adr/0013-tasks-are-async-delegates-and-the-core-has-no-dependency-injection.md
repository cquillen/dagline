# Tasks are async delegates and the core has no dependency injection

A task body is an async delegate that receives a context and returns a `Task`. Everything a task reports (status text, a count, warnings, a message, detail lines, a reported failure, its checkpoint, waiting) goes through that context. A task that needs a service captures it where the task is created, in the consumer's graph-building code. `src/Dagline` takes no dependency on `Microsoft.Extensions.*`, and there is no hosting package in v0.1.

Dependencies are declared on node handles, which a node's creation returns, and never by path string. A gate is a dependency on a collection of group handles, so it shrinks with the consumer's lists and is valid when empty. A handle from one builder cannot be used in another, and that is the ordinary build error.

## Considered options

- **Task classes resolved from a service provider.** Rejected: it would make `Microsoft.Extensions.DependencyInjection.Abstractions` the core's first dependency, for a single consumer who can capture a variable just as well. A class-based task is a method on any class.
- **Dependencies by path string.** Rejected: a typo is found at build instead of compile time, and the dependency on a path the filter dropped is the very mistake handles make impossible to write.
- **A task returns an outcome object.** Rejected: the context already carries warnings, a message and detail, so a second route to the same data would give two ways to say one thing.

## Consequences

- Nothing in the core needs a host, so a later hosting package can be added without changing it.
- Sync task bodies are not offered. A consumer wraps blocking work in `Task.Run`.
- A consumer who wants DI builds the graph from services it already resolved.
