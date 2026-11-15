# Security automation

## Vulnerability lanes

The repository has two complementary Trivy paths.

- **Blocking supply-chain audit** (`supply-chain-audit.yml`): runs on pull requests, pushes to the maintained branches, manual dispatch, and weekly schedule. It scans `HIGH,CRITICAL`, respects `.trivyignore.yaml`, uploads SARIF, and fails on new unignored findings.
- **Medium+ report** (`medium-vulnerability-report.yml`): runs daily, on relevant dependency/security changes to `marc/proton-main`, on matching pull requests, and by manual dispatch. It scans `MEDIUM,HIGH,CRITICAL` without failing pull requests for Medium findings.

The Medium+ lane emits three durable views:

1. a JSON report containing active findings plus Trivy's suppressed/accepted findings;
2. a SARIF report containing only active findings, uploaded to GitHub Security on non-PR runs;
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

## CVE-2026-40021 / log4net 3.2.0

Official Proton VPN 5.1.10 source and the maintained fork still pin `log4net` 3.2.0. The complete FastPatch can stage changed third-party dependencies but cannot assume compatibility with the existing official `.deps.json` and strongly named assembly bindings.

Apache describes CVE-2026-40021 as a Medium-severity log-event suppression issue in `XmlLayout` and `XmlLayoutSchemaLog4J` before log4net 3.3.0. Forbidden XML 1.0 characters in attacker-influenced MDC property keys/values or the identity field can cause XML serialization to fail and silently drop the affected log event.

The current ProtonVPN logging implementation is programmatic: `Log4NetLoggerInitializer` builds a `RollingFileAppender` using `PatternLayout`, wrapped by `HangingIndentLayout`, and configures it with `BasicConfigurator`. The fork does not intentionally configure either affected XML layout. This makes the advisory low-reachability in the current application path, but it is still a vulnerable dependency version and remains an explicit temporary exception rather than being declared unreachable.

### Review date

Review this exception no later than **2026-12-31**, matching `.trivyignore.yaml`. The isolated log4net 3.6.0 trial is PR #166. Never merge solely because compile/test CI passes; validate runtime binding on an actual 5.1.10 install.

### Removal conditions

Remove the compatibility exception as soon as one of these paths is proven safe:

1. the installed/released Proton VPN runtime moves to log4net **3.3.0 or newer** and FastPatch targets that runtime; or
2. FastPatch's runtime packaging model is changed so the fork can safely ship the newer strongly named log4net assembly without assembly/binding incompatibility.

Before removal:

1. verify the installed target's log4net assembly/version and binding behavior;
2. update `Directory.Packages.props` to the compatible fixed version;
3. run the logging tests, client/service build, FastPatch runtime-dependency closure, packaging, and installer validation;
4. perform a target-runtime smoke test that exercises application/service logging;
5. remove the CVE entry from `.trivyignore.yaml`;
6. remove the corresponding `NuGetAuditSuppress` from `ProtonVPN.Logging.csproj`;
7. remove or revise the log4net Dependabot compatibility ignore.

Dependabot intentionally ignores only the current **3.3.x** line using the NuGet `3.3.*` version pattern. A future 3.4+ release is allowed to surface as a new compatibility-review signal rather than being hidden indefinitely.

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
