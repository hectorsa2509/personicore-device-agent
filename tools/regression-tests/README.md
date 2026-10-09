# Core regression checks

Run from the repository root:

```sh
dotnet run --project tools/regression-tests/RegressionTests.csproj
```

Requires the .NET 9 SDK. No third-party test packages. Production Core files are compiled
unchanged; only AgentPaths and Log are replaced by test doubles. All state/config files
live in a unique temporary directory removed afterwards. The harness does not start the
agent, call production, access the real desktop outbox, or change system proxies.

Checks cover two concurrent OS processes saving different state halves, error propagation,
configuration layer fallback, outbox failure/success, automatic prompt preferences, manual
registration eligibility, and the actual assignment payload used by simulation.
# Certificate activation guard

The suite also exercises the shared guard used by both agents: missing trust, explicit
retry after installation, revocation while active, saved-on startup/network resume,
trust-check exceptions and unrestricted switch-off. These tests use a fake proxy and
never change the host trust store or network settings. Native OS trust policies and
administrator prompts still require Windows/macOS integration checks.
