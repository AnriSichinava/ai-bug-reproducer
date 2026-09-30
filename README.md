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

Milestone 0 in progress. Done: ASP.NET Core Web API with Swagger UI, EF Core + SQL Server (Azure SQL Edge locally, for arm64 compatibility) with a `User` entity and initial migration, JWT auth (register/login/protected endpoint), Docker Compose for `api` + `sql-server`. Remaining: `Project`/`BugReport` entities, CRUD endpoints, xUnit tests.

### Running locally

```bash
docker compose up --build -d
dotnet tool install --global dotnet-ef   # first time only
cd src/BugReproducer.Api
ASPNETCORE_ENVIRONMENT=Development dotnet ef database update
```

Swagger UI: http://localhost:5199/swagger

## Milestones

0. API skeleton: Swagger, EF Core + SQL Server, first migration, JWT auth working in Swagger UI
1. `Project` / `AgentRun` / `AgentStep` entities + relations; `reproduce` endpoint creates an `AgentRun` (no LLM yet)
2. MCP server (3 tools) + hardcoded (non-LLM) agent loop against the target app — proves the wiring
3. Real LLM tool-calling loop; REPRODUCED/NOT/INCONCLUSIVE decision with observed-vs-inferred evidence separation
4. Trace recording → deterministic Playwright test generation → execute generated test, store result
5. xUnit coverage of agent decision logic, Playwright e2e tests of this app's dashboard, Docker Compose, GitHub Actions CI
