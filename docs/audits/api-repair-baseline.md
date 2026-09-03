# API Repair Baseline Freeze

- **Date:** 2026-09-03
- **Branch:** feature/api-batch-automation
- **HEAD Commit:** 240b8d384332f06fde8c2504b41647974e0873d6
- **Status:** Clean working tree (except untracked plan and baseline doc)

## Verification
- `dotnet restore AssetProvenanceHelper.sln`: Succeeded.
- `dotnet build tests/AssetProvenanceHelper.Core.Tests/AssetProvenanceHelper.Core.Tests.csproj -c Release -warnaserror`: Succeeded (0 warnings, 0 errors).
- `dotnet test tests/AssetProvenanceHelper.Core.Tests/AssetProvenanceHelper.Core.Tests.csproj -c Release --no-build`: Passed (75 passed, 0 failed).
- `dotnet build AssetProvenanceHelper.sln -c Release -warnaserror`: Succeeded (0 warnings, 0 errors).
- `powershell -File scripts/run_tests_sac_safe.ps1 -Filter "FullyQualifiedName~Api" -SettleSeconds 0`: Passed (canary 19 passed, suite 27 passed, no code-integrity blocks).
