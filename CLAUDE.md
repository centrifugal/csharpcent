# CLAUDE.md

See [AGENTS.md](AGENTS.md). It is the guide for working on this repository and
applies here in full: the invariants, how to run the suite, how to update to a
new Centrifugo API, and the list of things that look wrong but are deliberate.

Three points from it are worth repeating, because they are the ones most easily
lost:

- **Never edit `src/Centrifugal.Cent/Generated/`.** Change `proto/` or
  `tools/Generator`, then `dotnet run --project tools/Generator`.
- **Every call throws unless it did everything asked.** A broadcast or batch in
  which some parts failed throws `BroadcastFailedChannelsException` or
  `BatchFailedCommandsException`, holding every part's outcome.
- **Automatic batching never delays a call below `MaxInFlight`** and never gives
  a caller a reply that is not its own.

Run the local checks of CONTRIBUTING.md before reporting a change as done, and
the integration tests with Centrifugo running for a change to the transport,
errors or batching.
