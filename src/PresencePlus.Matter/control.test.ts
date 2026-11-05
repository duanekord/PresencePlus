import { strict as assert } from "node:assert";
import { test } from "node:test";
import { DeviceAttestationCheck } from "@matter/protocol";
import { acceptProductionAttestation, applyColor, MatterError, parseRequest, publicError, rgbToMatter } from "./control.js";

test("status RGB palette maps to Matter hue, saturation and level", () => {
    for (const [color, hue, level] of [
        ["#00FF00", 85, 254], ["#FF8000", 21, 254], ["#FF0000", 0, 254],
        ["#800080", 212, 127], ["#FFFF00", 42, 254],
    ] as const) {
        assert.deepEqual(rgbToMatter(color, 100), { on: true, hue, saturation: 254, level });
    }
    assert.equal(rgbToMatter("#00FF00", 50).level, 127);
    assert.deepEqual(rgbToMatter("#000000", 100), { on: false, hue: 0, saturation: 0, level: 0 });
    assert.equal(rgbToMatter("#FFFFFF", 100).saturation, 0);
    assert.equal(rgbToMatter("#000001", 1).level, 1);
});

test("Off sends only off; colors and levels are set before power-on", async () => {
    const calls: unknown[] = [];
    const output = {
        on: async () => { calls.push("on"); }, off: async () => { calls.push("off"); },
        level: async (level: number) => { calls.push(["level", level]); },
        color: async (hue: number, saturation: number) => { calls.push(["color", hue, saturation]); },
    };
    await applyColor(output, "#000000", 100);
    assert.deepEqual(calls, ["off"]);
    calls.length = 0;
    await applyColor(output, "#800080", 100);
    assert.deepEqual(calls, [["color", 212, 254], ["level", 127], "on"]);
    output.color = async () => { throw new Error("Synthetic failure"); };
    await assert.rejects(() => applyColor(output, "#00FF00", 100), /Synthetic failure/);
});

test("protocol rejects invalid requests without reflecting secrets", () => {
    const secret = "private-value";
    for (const value of [
        null, { id: 1, action: "reset" }, { id: -1, action: "inspect" },
        { id: 1, action: "set", color: secret, brightnessPercent: 100 },
        { id: 1, action: "set", color: "#00FF00", brightnessPercent: 101 },
        { id: 1, action: "pair", pairingCode: secret, ssid: secret, password: secret },
    ]) {
        assert.throws(() => parseRequest(value), error => error instanceof Error && !error.message.includes(secret));
    }
    assert.deepEqual(parseRequest({ id: 1, action: "inspect" }), { id: 1, action: "inspect" });
    const pair = { id: 2, action: "pair", pairingCode: "3497-011-2332", ssid: "synthetic-network", password: "synthetic-password" };
    assert.equal(parseRequest(pair).action, "pair");
    assert.throws(() => parseRequest({ ...pair, ssid: "\u00e9".repeat(17) }));
    assert.throws(() => parseRequest({ ...pair, pairingCode: "34970112333" }));
});

test("production trust allows only the info-level root download timing note, even in mixed findings", () => {
    assert.equal(acceptProductionAttestation([]), true);
    const note = { type: DeviceAttestationCheck.PaaTrustStoreTimeMismatch, level: "info" as const, message: "Synthetic note" };
    for (const type of Object.values(DeviceAttestationCheck)) {
        for (const level of ["error", "warning", "info"] as const) {
            const permitted = type === note.type && level === note.level;
            const finding = { type, level, message: "Synthetic finding" };
            assert.equal(acceptProductionAttestation([finding]), permitted);
            assert.equal(acceptProductionAttestation([note, finding]), permitted);
        }
    }
});

test("unknown library errors never expose raw credentials or stack traces", () => {
    assert.ok(!JSON.stringify(publicError(new Error("synthetic-secret"))).includes("synthetic-secret"));
    assert.deepEqual(publicError(new MatterError("NotPaired", "Pair a light.")), { code: "NotPaired", message: "Pair a light." });
});
