// MCP server exposing a small, fixed set of browser tools backed by Playwright.
// This is the only way the agent touches a browser: it never gets raw Playwright
// access or a shell, only these named tools over stdio.
import { McpServer } from "@modelcontextprotocol/server";
import { StdioServerTransport } from "@modelcontextprotocol/server/stdio";
import { chromium, type Browser, type Page } from "playwright";
import * as path from "node:path";
import * as fs from "node:fs/promises";
import { fileURLToPath } from "node:url";
import * as z from "zod/v4";

const server = new McpServer({ name: "bug-reproducer-browser-tools", version: "1.0.0" });

const ARTIFACTS_DIR = process.env.ARTIFACTS_DIR ?? path.resolve("artifacts");
const MAX_NETWORK_ENTRIES = 200;
const MAX_TEXT_CHARS = 2000;

// Resolved from this file's own location, not process.cwd() -- the C# agent spawns this
// process from whatever directory it happens to run in, so cwd can't be trusted.
// Defaults to target-app/ (the one app this project actually reproduces bugs against);
// override with SOURCE_ROOT to point the agent at a different codebase.
const THIS_DIR = path.dirname(fileURLToPath(import.meta.url));
const SOURCE_ROOT = path.resolve(process.env.SOURCE_ROOT ?? path.join(THIS_DIR, "..", "..", "target-app"));
const IGNORED_DIR_NAMES = new Set(["node_modules", ".git", "dist"]);
const MAX_SEARCH_RESULTS = 50;

/// Resolves a path the model supplied against SOURCE_ROOT and rejects anything that
/// escapes it (`../../etc/passwd`, an absolute path elsewhere, a symlink out). The model
/// gets read access to one specific application's source, not the host filesystem.
function resolveWithinSourceRoot(relativePath: string): string {
  const resolved = path.resolve(SOURCE_ROOT, relativePath);
  if (resolved !== SOURCE_ROOT && !resolved.startsWith(SOURCE_ROOT + path.sep)) {
    throw new Error(`Path escapes the source root: ${relativePath}`);
  }
  return resolved;
}

async function* walkSourceFiles(dir: string): AsyncGenerator<string> {
  const entries = await fs.readdir(dir, { withFileTypes: true });
  for (const entry of entries) {
    if (entry.isDirectory()) {
      if (IGNORED_DIR_NAMES.has(entry.name)) continue;
      yield* walkSourceFiles(path.join(dir, entry.name));
    } else if (entry.isFile()) {
      yield path.join(dir, entry.name);
    }
  }
}

interface NetworkEntry {
  method: string;
  url: string;
  status: number;
  body?: string;
}

let browser: Browser | undefined;
let page: Page | undefined;
const consoleErrors: string[] = [];
const networkLog: NetworkEntry[] = [];

async function getPage(): Promise<Page> {
  if (page) return page;

  browser = await chromium.launch({ headless: true });
  page = await browser.newPage();

  page.on("console", (message) => {
    if (message.type() === "error") consoleErrors.push(message.text());
  });
  page.on("pageerror", (error) => {
    consoleErrors.push(error.message);
  });
  page.on("response", async (response) => {
    const entry: NetworkEntry = {
      method: response.request().method(),
      url: response.url(),
      status: response.status(),
    };
    // Only capture bodies for failures: that's where the evidence is, and it keeps
    // the log small enough to hand to a model.
    if (response.status() >= 400) {
      try {
        entry.body = (await response.text()).slice(0, MAX_TEXT_CHARS);
      } catch {
        // Body already consumed or the response has none; the status still stands as evidence.
      }
    }
    networkLog.push(entry);
    if (networkLog.length > MAX_NETWORK_ENTRIES) networkLog.shift();
  });

  return page;
}

function ok(value: unknown) {
  return { content: [{ type: "text" as const, text: JSON.stringify(value) }] };
}

function toolError(message: string) {
  return { isError: true, content: [{ type: "text" as const, text: message }] };
}

server.registerTool(
  "navigate",
  {
    description: "Navigate the browser to the given URL and return the resulting page title and HTTP status.",
    inputSchema: z.object({ url: z.string().describe("Absolute URL to navigate to") }),
  },
  async ({ url }) => {
    const currentPage = await getPage();
    const response = await currentPage.goto(url, { waitUntil: "load" });
    return ok({
      status: response?.status() ?? null,
      title: await currentPage.title(),
      url: currentPage.url(),
    });
  },
);

server.registerTool(
  "click",
  {
    description: "Click the first element matching the given CSS selector, then report the resulting URL.",
    inputSchema: z.object({ selector: z.string().describe("CSS selector of the element to click") }),
  },
  async ({ selector }) => {
    const currentPage = await getPage();
    try {
      await currentPage.click(selector, { timeout: 5000 });
      // Give any fetch/navigation the click triggered a moment to produce evidence.
      await currentPage.waitForTimeout(500);
      return ok({ clicked: selector, url: currentPage.url() });
    } catch (error) {
      return toolError(`Could not click "${selector}": ${(error as Error).message}`);
    }
  },
);

server.registerTool(
  "fill",
  {
    description: "Type a value into the form field matching the given CSS selector.",
    inputSchema: z.object({
      selector: z.string().describe("CSS selector of the input to fill"),
      value: z.string().describe("Text to type into the field"),
    }),
  },
  async ({ selector, value }) => {
    const currentPage = await getPage();
    try {
      await currentPage.fill(selector, value, { timeout: 5000 });
      return ok({ filled: selector });
    } catch (error) {
      return toolError(`Could not fill "${selector}": ${(error as Error).message}`);
    }
  },
);

server.registerTool(
  "get_page_snapshot",
  {
    description:
      "Describe the current page: its URL, title, visible text, and every interactive element with a " +
      "CSS selector you can pass to click or fill. Use this to discover how to drive the page.",
    inputSchema: z.object({}),
  },
  async () => {
    const currentPage = await getPage();
    const interactive = await currentPage.$$eval("a, button, input, select, textarea", (elements) =>
      elements.slice(0, 50).map((el) => {
        const id = el.getAttribute("id");
        const name = el.getAttribute("name");
        const tag = el.tagName.toLowerCase();
        // Prefer an id selector; fall back to name, then to a positional tag selector.
        const selector = id ? `#${id}` : name ? `${tag}[name="${name}"]` : tag;
        return {
          selector,
          tag,
          type: el.getAttribute("type"),
          text: (el.textContent ?? "").trim().slice(0, 80),
          href: el.getAttribute("href"),
        };
      }),
    );

    return ok({
      url: currentPage.url(),
      title: await currentPage.title(),
      text: (await currentPage.innerText("body")).slice(0, MAX_TEXT_CHARS),
      interactive,
    });
  },
);

server.registerTool(
  "get_console_errors",
  {
    description: "Return every browser console error and uncaught page error observed so far.",
    inputSchema: z.object({}),
  },
  async () => ok(consoleErrors),
);

server.registerTool(
  "get_network_log",
  {
    description:
      "Return recent HTTP requests the page made, with method, URL and status. Response bodies are " +
      "included for failures (status >= 400). This is the primary source of hard evidence.",
    inputSchema: z.object({}),
  },
  async () => ok(networkLog.slice(-50)),
);

server.registerTool(
  "take_screenshot",
  {
    description: "Save a screenshot of the current page and return the file path.",
    inputSchema: z.object({ name: z.string().describe("Short file name, without extension") }),
  },
  async ({ name }) => {
    const currentPage = await getPage();
    await fs.mkdir(ARTIFACTS_DIR, { recursive: true });
    const safeName = name.replace(/[^a-zA-Z0-9_-]/g, "_");
    const filePath = path.join(ARTIFACTS_DIR, `${safeName}-${Date.now()}.png`);
    await currentPage.screenshot({ path: filePath });
    return ok({ path: filePath });
  },
);

server.registerTool(
  "execute_api_request",
  {
    description:
      "Send an HTTP request directly, reusing the browser's cookies so it runs as the logged-in user. " +
      "Use this to check an endpoint's behaviour independently of the UI.",
    inputSchema: z.object({
      method: z.enum(["GET", "POST", "PUT", "PATCH", "DELETE"]),
      url: z.string().describe("Absolute URL"),
      body: z.string().optional().describe("JSON request body, as a string"),
    }),
  },
  async ({ method, url, body }) => {
    const currentPage = await getPage();
    try {
      const response = await currentPage.request.fetch(url, {
        method,
        data: body,
        headers: body ? { "Content-Type": "application/json" } : undefined,
        failOnStatusCode: false,
      });
      return ok({
        status: response.status(),
        body: (await response.text()).slice(0, MAX_TEXT_CHARS),
      });
    } catch (error) {
      return toolError(`Request failed: ${(error as Error).message}`);
    }
  },
);

server.registerTool(
  "read_source_file",
  {
    description:
      "Read a file from the target application's own source code, by path relative to the " +
      "application root (e.g. 'server.js'). Use this to check what the code actually does " +
      "instead of inferring it from UI behaviour alone.",
    inputSchema: z.object({
      path: z.string().describe("File path relative to the application's source root"),
    }),
  },
  async ({ path: relativePath }) => {
    try {
      const filePath = resolveWithinSourceRoot(relativePath);
      const content = await fs.readFile(filePath, "utf-8");
      return ok({ path: relativePath, content: content.slice(0, 10_000) });
    } catch (error) {
      return toolError(`Could not read "${relativePath}": ${(error as Error).message}`);
    }
  },
);

server.registerTool(
  "search_code",
  {
    description:
      "Search the target application's source code for a literal substring (case-insensitive) " +
      "and return matching file paths with line numbers. Use this to locate the handler for a " +
      "route or feature before reading the whole file.",
    inputSchema: z.object({ query: z.string().describe("Substring to search for") }),
  },
  async ({ query }) => {
    const needle = query.toLowerCase();
    const matches: { path: string; line: number; text: string }[] = [];

    try {
      for await (const filePath of walkSourceFiles(SOURCE_ROOT)) {
        if (matches.length >= MAX_SEARCH_RESULTS) break;

        let content: string;
        try {
          content = await fs.readFile(filePath, "utf-8");
        } catch {
          continue; // binary or unreadable; skip rather than fail the whole search
        }

        const lines = content.split("\n");
        for (let i = 0; i < lines.length && matches.length < MAX_SEARCH_RESULTS; i++) {
          if (lines[i].toLowerCase().includes(needle)) {
            matches.push({
              path: path.relative(SOURCE_ROOT, filePath),
              line: i + 1,
              text: lines[i].trim().slice(0, 200),
            });
          }
        }
      }

      return ok(matches);
    } catch (error) {
      return toolError(`Search failed: ${(error as Error).message}`);
    }
  },
);

async function main() {
  const transport = new StdioServerTransport();
  await server.connect(transport);
}

main().catch((error) => {
  console.error("Fatal error starting MCP server:", error);
  process.exit(1);
});
