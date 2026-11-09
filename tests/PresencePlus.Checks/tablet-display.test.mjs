import { readFile } from "node:fs/promises";
import vm from "node:vm";
import { test } from "node:test";
import assert from "node:assert/strict";

const source = await readFile(new URL("../../src/PresencePlus/TabletDisplay/display.js", import.meta.url), "utf8");
const html = await readFile(new URL("../../src/PresencePlus/TabletDisplay/index.html", import.meta.url), "utf8");
const key = "A".repeat(64);
const tick = () => new Promise(resolve => setImmediate(resolve));

async function viewer(responses, fragment = key) {
    const elements = { status: {} };
    const events = {};
    const timers = new Map();
    const requests = [];
    let timerId = 0;
    const document = {
        documentElement: { style: {} },
        getElementById: id => elements[id],
        addEventListener: (name, callback) => { events[name] = callback; },
        hidden: false
    };
    const context = {
        document,
        location: { hash: `#${fragment}` },
        window: { addEventListener: (name, callback) => { events[name] = callback; } },
        AbortController,
        setTimeout: (callback, delay) => { timers.set(++timerId, { callback, delay }); return timerId; },
        clearTimeout: id => timers.delete(id),
        fetch: async (path, options) => {
            requests.push({ path, options });
            const response = responses.shift();
            if (response instanceof Error) throw response;
            if (typeof response === "function") return response(options);
            return { ok: true, status: 200, json: async () => response };
        }
    };
    vm.runInNewContext(source, context);
    await tick();
    return {
        elements, events, document, requests,
        async fireTimer(delay) {
            const entry = [...timers.entries()].find(([, timer]) => timer.delay === delay);
            assert.ok(entry, `expected a ${delay}ms timer`);
            timers.delete(entry[0]);
            entry[1].callback();
            await tick();
        }
    };
}

test("viewer has a personal heading and no line below the status", () => {
    assert.match(html, /<p class="brand">Duane's Status<\/p>/);
    assert.match(html, /<title>Duane's Status<\/title>/);
    assert.match(html, /<\/h1>\s*<\/main>/);
    assert.doesNotMatch(html, /id="connection"/);
});

test("missing link never requests or shows a status", async () => {
    const page = await viewer([], "");
    assert.equal(page.elements.status.textContent, "Link required");
    assert.equal(page.requests.length, 0);
});

test("all status names and custom colors appear without controls", async () => {
    for (const [name, color, foreground] of [
        ["Available", "#00FF00", "#000000"],
        ["Busy", "#FF8000", "#000000"],
        ["Do not disturb", "#FF0000", "#000000"],
        ["In a meeting", "#123ABC", "#ffffff"],
        ["Presenting", "#800080", "#ffffff"],
        ["Away", "#FFFF00", "#000000"],
        ["Off", "#000000", "#ffffff"]
    ]) {
        const page = await viewer([{ name, color }]);
        assert.equal(page.elements.status.textContent, name);
        assert.equal(page.document.documentElement.style.backgroundColor, color);
        assert.equal(page.document.documentElement.style.color, foreground);
        assert.equal(page.document.title, `Duane's Status - ${name}`);
        assert.equal(page.requests[0].path, "/api/status");
        assert.equal(page.requests[0].options.headers["X-PresencePlus-Display-Key"], key);
        assert.equal(page.requests[0].options.cache, "no-store");
    }
});

test("polling updates status, clears stale color on failure, and recovers", async () => {
    const page = await viewer([
        { name: "Available", color: "#00FF00" },
        { name: "Busy", color: "#FF8000" },
        new Error("offline"),
        { name: "Away", color: "#FFFF00" }
    ]);
    await page.fireTimer(1000);
    assert.equal(page.elements.status.textContent, "Busy");
    await page.fireTimer(1000);
    assert.equal(page.elements.status.textContent, "Disconnected");
    assert.equal(page.document.documentElement.style.backgroundColor, "#202124");
    await page.fireTimer(1000);
    assert.equal(page.elements.status.textContent, "Away");
});

test("expired viewing key is explained instead of showing the last status", async () => {
    const page = await viewer([() => ({ status: 401, ok: false })]);
    assert.equal(page.elements.status.textContent, "Link expired");
    assert.equal(page.document.title, "Duane's Status - Link expired");
});

test("invalid responses and HTTP errors never look live", async () => {
    for (const value of [
        { name: "Busy", color: "red" }, { name: "", color: "#FFFFFF" },
        () => ({ status: 500, ok: false })
    ]) {
        const page = await viewer([value]);
        assert.equal(page.elements.status.textContent, "Disconnected");
    }
});

test("hung requests time out after four seconds and clear stale status", async () => {
    const page = await viewer([
        { name: "Available", color: "#00FF00" },
        options => new Promise((resolve, reject) => {
            options.signal.addEventListener("abort", () => reject(new Error("aborted")));
        })
    ]);
    await page.fireTimer(1000);
    await page.fireTimer(4000);
    assert.equal(page.elements.status.textContent, "Disconnected");
});

test("offline events invalidate pending replies; online events reconnect", async () => {
    let finish;
    const page = await viewer([
        () => new Promise(resolve => { finish = resolve; }),
        { name: "Presenting", color: "#800080" }
    ]);
    page.events.offline();
    finish({ status: 200, ok: true, json: async () => ({ name: "Busy", color: "#FF8000" }) });
    await tick();
    assert.equal(page.elements.status.textContent, "Disconnected");
    page.events.online();
    await tick();
    assert.equal(page.elements.status.textContent, "Presenting");
});

test("waking a background tablet clears old status until revalidated", async () => {
    let finish;
    const page = await viewer([
        { name: "Available", color: "#00FF00" },
        () => new Promise(resolve => { finish = resolve; })
    ]);
    page.events.visibilitychange();
    assert.equal(page.elements.status.textContent, "Connecting...");
    assert.equal(page.document.documentElement.style.backgroundColor, "#202124");
    finish({ status: 200, ok: true, json: async () => ({ name: "Busy", color: "#FF8000" }) });
    await tick();
    assert.equal(page.elements.status.textContent, "Busy");
});
