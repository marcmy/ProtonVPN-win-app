# Server measurements and browser implementation plan

**Goal** — Ship a dedicated Browse all servers view and consistent direct-ICMP reporting across the list, current-server card and history.

**Architecture** — Preserve discovery as a first-reply workload, qualify independent fixed bursts for loss, and publish a single snapshot to each presentation surface. Use the native WinUI navigation and existing server eligibility and connect commands.

**Spec** — [Server measurements and Browse all servers](../specs/2026-11-22-server-measurements-and-browser-design.md). The main-window card and details also show the same rolling percentage and denominator, explicitly identify the latest check, and distinguish loss-marked graph points from clean ones.

- [x] Add and verify versioned probe outcomes and paced service sampling. Keep the quick discovery path, endpoint cap and route ownership. Test a slow first echo, local errors and cancellation.
- [x] Add and verify qualified client aggregation, provenance-aware cache and network-generation handling. Test mixed discovery, saved, failed and fixed checks so only qualified fixed samples contribute loss.
- [x] Use the shared measurement presentation in the card, history and browser. Test the latest-versus-rolling example and ensure a lost echo is visible even in a mostly green history.
- [x] Wire the dedicated list into main-window navigation with virtualization, search, filters, sort, selection, details and explicit connect. Verify Windows client and XAML compilation.
- [x] Run the client tests and focused service tests. Verify 1,000- and 10,000-endpoint discovery, the concurrency bound and the paced-send schedule using deterministic inputs.
- [ ] Compare live timing against the installed baseline and verify navigation, keyboard focus, accessibility and telemetry with a running candidate.

The client suite passed 74 tests and the focused Windows service suite passed 30. The broader service suite passed 190 tests, skipped two, and could not create the temporary registry key required by one NRPT test in the sandbox.
