# Contributing

## Development

Use Windows with the .NET SDK selected by `global.json`. Before opening a pull request, run:

```powershell
dotnet format ValorantMatureContentManager.sln
dotnet build ValorantMatureContentManager.sln --configuration Release
dotnet test ValorantMatureContentManager.sln --configuration Release --no-build
```

Keep business logic in `ValorantMatureContent.Core`; GUI code must not call the CLI and the CLI must not call WPF. Do not test against a real VALORANT installation. Use temporary fake installations and add integration coverage for every destructive failure path.

## Payload Changes

Do not add proprietary game assets without confirming redistribution rights. Every payload change requires exact sizes, SHA-256 hashes, a new payload version, and explicitly verified game builds. Never use wildcard compatibility.

## Pull Requests

Keep changes focused, preserve rollback behavior, document assumptions, and include test results. No license has been selected; do not add one without maintainer approval.