# AI Bug Reproducer

A natural-language bug report goes in. An AI agent uses MCP tools to drive a real browser (Playwright)
against a target web app, decides **REPRODUCED / NOT REPRODUCED / INCONCLUSIVE** based on collected
evidence (network responses, console errors, screenshots), and generates a deterministic Playwright
regression test from the recorded tool-call trace.

Built as a portfolio project for an Associate Software Developer application (.NET/C# focus, with an
AI/agent component that is functionally necessary, not decorative).

## Architecture

```
BugReproducer.Api        ASP.NET Core Web API — controllers, EF Core, JWT, Swagger
BugReproducer.Agent      Plain C# lib — agent loop, LLM client, MCP client, evidence/verdict logic
BugReproducer.Tests      xUnit — unit + API integration tests
mcp-tools/                TS/Node MCP server wrapping Playwright
e2e/                       Playwright TS — tests of this app's own UI
target-app/                Dockerized small OSS demo app, seeded with reintroduced bugs
```

The `Agent` library is C# but has no ASP.NET dependency — it's a reusable orchestration layer.
The MCP tool server is TypeScript because the same Playwright runtime that reproduces bugs also
executes the generated regression tests.

## Status

**All five planned milestones are complete**, plus one thing beyond the original plan: the
reproduce endpoint actually *runs* the agent now (originally scoped as a later nice-to-have,
but a product where clicking "Reproduce" does nothing isn't really a product).

- **Milestone 0** — ASP.NET Core Web API, Swagger UI with JWT auth wired in, EF Core + SQL
  Server (Azure SQL Edge locally, for Apple Silicon compatibility), `User`/`Project`/`BugReport`
  entities with owner-scoped CRUD, Docker Compose.
- **`target-app/`** — a minimal, deliberately buggy demo cart app (Express, in-memory, no
  framework) written for this project rather than adapted from an existing one. Reproduces a
  real cart-removal crash after logout/login, matching the project's original spec almost
  exactly — see its own comments for the root cause.
- **Milestone 1** — `AgentRun`/`AgentStep` entities; `POST /api/bugs/{id}/reproduce`.
- **Milestone 2** — `mcp-tools/`, a TypeScript MCP server (`@modelcontextprotocol/server`)
  wrapping Playwright with 10 tools: `navigate`, `click`, `fill`, `get_page_snapshot`,
  `get_console_errors`, `get_network_log`, `take_screenshot`, `execute_api_request`, plus
  `read_source_file`/`search_code` (scoped to `target-app/`, path-traversal-safe — added
  after the initial v1 trim, see "Possible next steps" below) for grounding in the
  application's actual code, not just its observed behavior. This is the *only* path from
  the agent to a browser or the filesystem — no raw Playwright or shell access anywhere
  above it.
- **Milestone 3** — `ReproductionAgent`, the LLM-driven loop. See "How the agent loop works"
  below.
- **Milestone 4** — `PlaywrightTestGenerator` turns a recorded trace into a runnable Playwright
  regression test deterministically (templated, not LLM-authored), and
  `POST /api/agent-runs/{id}/generate-test` executes it via the real Playwright CLI and
  persists the result (`GeneratedTest`/`TestRun` entities).
- **Milestone 5** — a minimal dashboard (`wwwroot/`, plain HTML/JS, served by the API itself)
  with hand-written Playwright e2e coverage (`e2e/dashboard/`), plus GitHub Actions CI.
- **Beyond the plan** — `POST /reproduce` now actually executes `ReproductionAgent` via a
  background task queue instead of only creating a row. See "Running a real reproduction".

### How the agent loop works

The loop is written out rather than delegated to an automatic tool-invocation helper, because owning it is what makes the rest possible: recording every step as it happens, enforcing a step budget, and ending the run on an explicit signal.

- A run ends only when the model calls the **`conclude`** tool, so the verdict arrives structured and "the model went quiet" is never mistaken for "the model reached a conclusion".
- **Observed fact and inference are separated structurally.** Evidence must cite the step number whose tool output it describes; the loop checks each citation against the steps that actually ran, and anything citing a step that never happened is returned separately as `UnverifiableEvidence` rather than presented as a finding.
- `NOT_REPRODUCED` requires having actually performed the steps. Being unable to test something is `INCONCLUSIVE` — reporting a bug as "not reproduced" when the agent never got there would be the most damaging possible output.
- Two seams (`IMessagesClient`, `IBrowserTools`) keep the loop deterministic and testable: the agent knows about *named tools*, not about MCP or Playwright, and the model is scripted in tests.

### How a generated regression test works

`PlaywrightTestGenerator` templates a `.spec.ts` file from the recorded trace. One deliberate
inversion from a literal reading of the original spec: the generated assertion encodes the
*expected correct* behavior (no server error, no new console errors), not the observed bug.
Asserting the bug itself (e.g. "expect status to be 500") would make the test pass forever,
including after a real fix — the opposite of what a regression test is for. This way the test
fails now (bug present) and starts passing the moment someone actually fixes it.

Two non-obvious things the generator has to handle, both found by actually running the
generated output against the real app rather than just reasoning about it: a click that
triggers an async fetch-then-redirect needs the same post-click pause the recording tool
itself uses, or Playwright's own navigation races the next action; and a recorded email value
has to be regenerated per run (behind a shared variable, so register and a later login still
agree with each other), or replaying the literal captured value tries to re-register an
account that already exists.

### Running a real reproduction

The loop needs a real Anthropic API key (defaults to `claude-opus-5`, configurable via
`AgentOptions`/`appsettings`):

```bash
export ANTHROPIC_API_KEY=sk-ant-...
```

Without one, a reproduction run still completes the full pipeline (queued → running →
persisted) — it just ends `Failed` with the real API error as `FailureReason`, rather than
getting stuck. That's by design: `Failed` (the agent broke) and `Inconclusive` (the agent ran
fine but couldn't decide) are different facts, and the executor is written to always resolve
one way or the other, never leave a run stuck.

### Known boundary: this runs from source, not from the containerized `api` service

Reproducing a bug for real needs `mcp-tools/dist/` (to spawn the MCP server) and generating a
test needs `e2e/` (to run the generated file) to exist on disk relative to the API's content
root. Neither is copied into the `api` Docker image. So: Docker Compose hosts `sql-server` and
`target-app`; run the API itself via `dotnet run` from source to exercise the full agent
pipeline and dashboard. This is a real, intentional scope boundary, not an oversight — see
"Running locally" below for the exact commands.

### Running locally

```bash
# SQL Server + target-app in Docker; API runs from source so it can find mcp-tools/ and e2e/
docker compose up -d sql-server target-app

dotnet tool install --global dotnet-ef   # first time only
cd src/BugReproducer.Api
ASPNETCORE_ENVIRONMENT=Development dotnet ef database update

# Build the MCP server once (needed for real reproductions, not for the default test suite)
cd ../../mcp-tools && npm install && npx playwright install chromium && npm run build && cd ..

export ANTHROPIC_API_KEY=sk-ant-...   # optional — without it, runs complete and end Failed
cd src/BugReproducer.Api && ASPNETCORE_ENVIRONMENT=Development dotnet run --urls http://localhost:5199
```

Dashboard: http://localhost:5199 — Swagger UI: http://localhost:5199/swagger

Tests (no database or containers required — they run against SQLite in-memory):

```bash
dotnet test --filter "Category!=RequiresBrowserAndTargetApp"
```

The `RequiresBrowserAndTargetApp` category is excluded by default because those tests
drive a real browser against `target-app`. To run them too:

```bash
docker compose up -d target-app
cd mcp-tools && npm install && npx playwright install chromium && npm run build && cd ..
dotnet test --filter "Category=RequiresBrowserAndTargetApp"
```

Hand-written e2e tests of the dashboard itself (needs the full stack above running natively,
not just gated xUnit tests — not yet wired into CI, see `.github/workflows/ci.yml`):

```bash
cd e2e && npx playwright test dashboard/
```

## Milestones

0. API skeleton: Swagger, EF Core + SQL Server, first migration, JWT auth working in Swagger UI — **done**
1. `Project` / `AgentRun` / `AgentStep` entities + relations; `reproduce` endpoint creates an `AgentRun` — **done**
2. MCP server (now 8 tools) + a proven hardcoded (non-LLM) wiring check against the target app — **done**
3. Real LLM tool-calling loop; REPRODUCED/NOT/INCONCLUSIVE decision with observed-vs-inferred evidence separation — **done**
4. Trace recording → deterministic Playwright test generation → execute generated test, store result — **done**
5. xUnit coverage of agent decision logic, Playwright e2e tests of this app's dashboard, Docker Compose, GitHub Actions CI — **done**

### Possible next steps (not started)

- Containerize the agent pipeline itself (bundle `mcp-tools`/`e2e` + Node into the `api` image) so a real reproduction doesn't require running the API from source.
- Wire the dashboard's e2e tests into CI (needs the API + migrations running there too, not just `target-app`).
- OpenAPI-spec reading tool (`read_source_file`/`search_code` are done — see Milestone 2).
- Role system beyond the single owner check (deliberately out of scope for this project, see memory of the original design discussion).
