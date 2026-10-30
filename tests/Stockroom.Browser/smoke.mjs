import assert from "node:assert/strict";
import { mkdir } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import { chromium } from "playwright";

const baseUrl = process.env.STOCKROOM_URL || "http://127.0.0.1:5080";
const browser = await chromium.launch({ headless: true });
const errors = [];
const context = await browser.newContext({ viewport: { width: 1440, height: 1080 } });
const page = await context.newPage();
page.setDefaultTimeout(15000);
page.on("pageerror", error => errors.push(error.message));
page.on("console", message => { if (message.type() === "error") errors.push(message.text()); });
const screenshots = process.env.UPDATE_SCREENSHOTS === "1";

try {
    await page.goto(baseUrl);
    await page.getByRole("button", { name: "Try as manager" }).click();
    await page.getByRole("heading", { name: "A little clarity. A lot of control." }).waitFor();
    if (screenshots) {
        await mkdir(new URL("../../docs/images/", import.meta.url), { recursive: true });
        await page.screenshot({ path: fileURLToPath(new URL("../../docs/images/dashboard.png", import.meta.url)), fullPage: true });
        const phone = await context.newPage();
        await phone.setViewportSize({ width: 390, height: 844 });
        await phone.goto(`${baseUrl}/#inventory`);
        await phone.getByRole("heading", { name: "A place for every product." }).waitFor();
        await phone.screenshot({ path: fileURLToPath(new URL("../../docs/images/mobile.png", import.meta.url)), fullPage: false });
        await phone.close();
    }
    await page.locator('[data-nav="inventory"]').click();
    await page.getByRole("button", { name: "Add product", exact: true }).click();
    const sku = `QA-${Date.now()}`;
    const name = `<QA> Browser keyboard ${sku}`;
    await page.getByLabel("SKU", { exact: true }).fill(sku);
    await page.getByLabel("Product name", { exact: true }).fill(name);
    await page.getByLabel("Unit selling price (USD)").fill("19.99");
    await page.getByLabel("Opening stock").fill("5");
    await page.getByLabel("Reorder point").fill("2");
    await page.getByRole("dialog").getByRole("button", { name: "Add product", exact: true }).click();
    await page.getByRole("dialog").waitFor({ state: "hidden" });
    await page.getByLabel("Search inventory").fill(sku);
    const row = page.getByRole("row").filter({ hasText: sku });
    await row.getByRole("button", { name: "Adjust stock" }).click();
    await page.getByLabel("Quantity change").fill("3");
    await page.getByLabel("Reason", { exact: true }).fill("Browser test restock");
    await page.getByRole("button", { name: "Save adjustment" }).click();
    await page.getByRole("dialog").waitFor({ state: "hidden" });
    await page.waitForFunction(sku => [...document.querySelectorAll("tbody tr")].some(row => row.textContent.includes(sku) && row.querySelectorAll("td")[2]?.textContent === "8"), sku);
    assert.match(await row.innerText(), /<QA> Browser keyboard/);
    console.log("PASS: manager creates a product, safely renders its name, searches it, and adjusts stock");

    await page.locator('[data-nav="orders"]').click();
    await page.getByRole("button", { name: "Create order" }).click();
    const customer = `Browser Studio ${sku}`;
    await page.getByLabel("Customer or company").fill(customer);
    const option = page.locator(".line-product option").filter({ hasText: name });
    await page.getByRole("combobox", { name: "Product", exact: true }).selectOption(await option.getAttribute("value"));
    await page.getByRole("spinbutton", { name: "Quantity", exact: true }).fill("2");
    assert.equal(await page.locator("#order-total").innerText(), "$39.98");
    await page.getByRole("button", { name: "Reserve inventory" }).click();
    await page.getByRole("dialog").waitFor({ state: "hidden" });
    const orderRow = page.getByRole("row").filter({ hasText: customer });
    await orderRow.getByRole("button", { name: "View order" }).click();
    await page.getByRole("button", { name: "Cancel order", exact: true }).click();
    await page.getByRole("dialog").waitFor({ state: "hidden" });
    await orderRow.getByText("Cancelled", { exact: true }).waitFor();
    console.log("PASS: order total, reservation, and cancellation work through the UI");

    await page.locator('[data-nav="activity"]').click();
    await page.getByRole("cell", { name: `Order for ${customer}`, exact: true }).first().waitFor();
    await page.locator('[data-nav="inventory"]').click();
    await page.getByLabel("Search inventory").fill(sku);
    await page.waitForFunction(sku => [...document.querySelectorAll("tbody tr")].some(row => row.textContent.includes(sku) && row.querySelectorAll("td")[3]?.textContent === "8"), sku);
    console.log("PASS: cancellation restores availability and records activity");

    // Phone navigation remains usable; wide data tables scroll inside their card.
    await page.setViewportSize({ width: 390, height: 844 });
    await page.getByLabel("Search inventory").fill("");
    await page.getByRole("heading", { name: "A place for every product." }).waitFor();
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), true, "The page should not overflow horizontally on mobile");
    console.log("PASS: 390px mobile layout has no page overflow");

    await page.setViewportSize({ width: 1440, height: 1080 });
    await page.getByRole("button", { name: "Sign out" }).click();
    await page.getByRole("button", { name: "Try as viewer" }).click();
    await page.getByRole("heading", { name: "A place for every product." }).waitFor();
    assert.equal(await page.getByRole("button", { name: "Add product", exact: true }).count(), 0);
    assert.equal(await page.getByRole("button", { name: "Adjust stock" }).count(), 0);
    await page.locator('[data-nav="orders"]').click();
    assert.equal(await page.getByRole("button", { name: "Create order" }).count(), 0);
    console.log("PASS: viewer has a read-only interface");
    assert.deepEqual(errors, [], "Browser should have no JavaScript or CSP errors");
    console.log("PASS: no browser JavaScript or content-security-policy errors");
} finally {
    await browser.close();
}
