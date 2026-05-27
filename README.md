# maichess-match-manager-service

See `CLAUDE.md` for architecture, contracts, and design notes.

## Mutation Testing (Stryker.NET)

Stryker is installed as a local .NET tool. Configuration lives in
`MaichessMatchManagerService.Tests/stryker-config.json`. REST endpoint
adapters, the MongoDB-backed `MatchRepository`, and fire-and-forget bot-move
helpers are excluded to mirror the coverage exclusions.

```powershell
# First time on a clean checkout — restore the local tool
dotnet tool restore

# Run mutation tests (from the test project directory)
cd MaichessMatchManagerService.Tests
dotnet stryker
```

After the run, open `StrykerOutput/<timestamp>/reports/mutation-report.html`
in a browser to inspect surviving mutants.

To bump the Stryker version: `dotnet tool update dotnet-stryker`.
