// MCP server exposing a small, fixed set of browser tools backed by Playwright.
// This is the only way the agent touches a browser: it never gets raw Playwright
// access or a shell, only these named tools over stdio.
import { McpServer } from "@modelcontextprotocol/server";
import { StdioServerTransport } from "@modelcontextprotocol/server/stdio";
import { chromium, type Browser, type Page } from "playwright";
import * as z from "zod/v4";

const server = new McpServer({ name: "bug-reproducer-browser-tools", version: "1.0.0" });

let browser: Browser | undefined;
let page: Page | undefined;
const consoleErrors: string[] = [];

async function getPage(): Promise<Page> {
  if (page) return page;

  browser = await chromium.launch({ headless: true });
  page = await browser.newPage();
  page.on("console", (message) => {
    if (message.type() === "error") {
      consoleErrors.push(message.text());
    }
  });
  page.on("pageerror", (error) => {
    consoleErrors.push(error.message);
  });

  return page;
}

server.registerTool(
  "navigate",
  {
    description: "Navigate the browser to the given URL and return the resulting page title.",
    inputSchema: z.object({ url: z.string().describe("Absolute URL to navigate to") }),
  },
  async ({ url }) => {
    const currentPage = await getPage();
    const response = await currentPage.goto(url, { waitUntil: "load" });
    return {
      content: [
        {
          type: "text",
          text: JSON.stringify({
            status: response?.status() ?? null,
            title: await currentPage.title(),
            url: currentPage.url(),
          }),
        },
      ],
    };
  },
);

server.registerTool(
  "click",
  {
    description: "Click the first element matching the given CSS selector.",
    inputSchema: z.object({ selector: z.string().describe("CSS selector of the element to click") }),
  },
  async ({ selector }) => {
    const currentPage = await getPage();
    try {
      await currentPage.click(selector, { timeout: 5000 });
      return { content: [{ type: "text", text: JSON.stringify({ clicked: selector }) }] };
    } catch (error) {
      return {
        isError: true,
        content: [{ type: "text", text: `Could not click "${selector}": ${(error as Error).message}` }],
      };
    }
  },
);

server.registerTool(
  "get_console_errors",
  {
    description: "Return every browser console error and uncaught page error observed so far.",
    inputSchema: z.object({}),
  },
  async () => {
    return { content: [{ type: "text", text: JSON.stringify(consoleErrors) }] };
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
