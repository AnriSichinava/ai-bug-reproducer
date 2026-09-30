// MCP server exposing a small, fixed set of browser tools backed by Playwright.
// This is the only way the agent touches a browser: it never gets raw Playwright
// access or a shell, only these named tools over stdio.
import { McpServer } from "@modelcontextprotocol/server";
import { StdioServerTransport } from "@modelcontextprotocol/server/stdio";
import { chromium, type Browser, type Page } from "playwright";
import * as path from "node:path";
import * as fs from "node:fs/promises";
import * as z from "zod/v4";

const server = new McpServer({ name: "bug-reproducer-browser-tools", version: "1.0.0" });

const ARTIFACTS_DIR = process.env.ARTIFACTS_DIR ?? path.resolve("artifacts");
const MAX_NETWORK_ENTRIES = 200;
const MAX_TEXT_CHARS = 2000;

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

async function main() {
  const transport = new StdioServerTransport();
  await server.connect(transport);
}

main().catch((error) => {
  console.error("Fatal error starting MCP server:", error);
  process.exit(1);
});
