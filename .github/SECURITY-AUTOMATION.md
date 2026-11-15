# Security automation

## Vulnerability lanes

The repository has two complementary Trivy paths.

- **Blocking supply-chain audit** (`supply-chain-audit.yml`): runs on pull requests, pushes to the maintained branches, manual dispatch, and weekly schedule. It scans `HIGH,CRITICAL`, respects `.trivyignore.yaml`, uploads SARIF, and fails on new unignored findings.
- **Medium+ report** (`medium-vulnerability-report.yml`): runs daily, on relevant dependency/security changes to `marc/proton-main`, on matching pull requests, and by manual dispatch. It scans `MEDIUM,HIGH,CRITICAL` without failing pull requests for Medium findings.

The Medium+ lane emits three durable views:

1. a JSON report containing active findings plus Trivy's suppressed/accepted findings;
2. a SARIF report containing only active findings, uploaded to GitHub Security on PR and default-branch runs;
3. a GitHub job summary and rolling 14-day workflow artifact that separate active findings from accepted/suppressed risk and show exception expiry dates.

High/Critical findings still belong to the blocking policy. The Medium+ lane also fails non-PR runs when active High/Critical findings appear so newly disclosed severe advisories become conspicuous between weekly blocking audits.

Syft remains the independent SBOM generator. Grype is not run in parallel solely to duplicate Trivy vulnerability results; add a second vulnerability scanner only when it supplies a concrete coverage or validation benefit.

### Coverage boundary

Trivy scans source manifests independently of the restored graph. The NuGet submission and Syft SBOM workflows restore supported Windows client, service and launcher projects with .NET 10, generate temporary `packages.lock.json` files, and validate nonempty direct/transitive graphs. Off-solution legacy projects remain outside this claim.

The scoped Windows dependency-graph restoration explicitly uses `NuGetAuditMode=all`. It refuses snapshots with fewer than 30 resolved NuGet packages, missing log4net, or missing project lockfiles. The write-capable submission job is separate from read-only PR validation.

Treat a successful job with an empty dependency graph as a failure. GitHub Dependency Submission and Syft lockfile evidence complement, rather than replace, the existing manifest-oriented Trivy scans.

## Vulnerability exception rules

Entries in `.trivyignore.yaml` must be:

- tied to a specific finding ID;
- scoped by path and/or package PURL where practical;
- documented with a concrete compatibility/risk rationale;
- assigned an `expired_at` review date.

The Medium+ workflow validates the canonical exception metadata format before scanning. Unexpected entry indentation, duplicate IDs, invalid/missing expiry dates, or missing statements fail closed instead of being interpreted as zero exceptions. The report then surfaces suppressed findings and their statements; an expired exception, or a vulnerability exception with no expiry date, also makes the workflow fail loudly.

## log4net 3.6.0 runtime acceptance

The maintained fork uses log4net **3.6.0**, packaged through the complete FastPatch runtime-dependency closure for Proton VPN 5.1.10. The official source baseline remains at 3.2.0.

On 2026-11-15, the maintainer tested PR #166's installer from workflow run 43679387030 on the installed 5.1.10 application. Both ProtonVPN.Client and ProtonVPNService reported loading `v5.1.10\\log4net.dll` version **3.6.0.0**. The maintainer confirmed the startup/reconnection and fresh-log checks, and that the existing dependency manifests remained unchanged.

The former CVE-2026-40021 compatibility exception, corresponding NuGet audit suppression, and Dependabot 3.3.x ignore have been removed. New log4net advisories and updates must be evaluated normally.

Build and test success alone does not establish compatibility with installed official dependency manifests. Future logging upgrades still require client/service runtime loading and logging checks, package closure validation, and a retained rollback backup.

## Advanced CodeQL and Trivy versions

GitHub Default Setup previously ran C# buildless extraction on Linux and failed to
resolve Windows/WinRT types. The candidate `.github/workflows/codeql-advanced.yml`
uses Windows .NET 10 manual builds for C# and Python 3.13 for Python. Its PR scans
are validation-only and **do not upload** findings while GitHub Default Setup
remains enabled. To switch, disable Default Setup in Settings → Advanced Security,
then set repository variable `ENABLE_ADVANCED_CODEQL=true`. Update required
CodeQL status checks only after new uploads and checks are confirmed.

The Trivy action code stays SHA-pinned. Both vulnerability workflows explicitly
request the `latest` scanner binary on each run. Treat a newly released
scanner's failures as real review events, not a reason to silence scanning.
