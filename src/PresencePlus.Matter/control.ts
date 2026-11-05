import { DeviceAttestationCheck, type AttestationFinding } from "@matter/protocol";
import { ManualPairingCodeCodec } from "@matter/types";

export class MatterError extends Error {
    constructor(readonly code: string, message: string) { super(message); }
}

export type Request =
    | { id: number; action: "inspect" | "shutdown" }
    | { id: number; action: "set"; color: string; brightnessPercent: number }
    | { id: number; action: "pair"; pairingCode: string; ssid: string; password: string };

export function parseRequest(value: unknown): Request {
    if (typeof value !== "object" || value === null || !("id" in value)
        || typeof value.id !== "number" || !Number.isSafeInteger(value.id) || value.id < 1
        || !("action" in value)) {
        throw new MatterError("InvalidRequest", "Invalid local controller request.");
    }
    const id = value.id;
    if (value.action === "inspect" || value.action === "shutdown") return { id, action: value.action };
    if (value.action === "set" && "color" in value && typeof value.color === "string"
        && /^#[0-9a-f]{6}$/i.test(value.color) && "brightnessPercent" in value
        && typeof value.brightnessPercent === "number" && Number.isInteger(value.brightnessPercent)
        && value.brightnessPercent >= 1 && value.brightnessPercent <= 100) {
        return { id, action: "set", color: value.color, brightnessPercent: value.brightnessPercent };
    }
    if (value.action === "pair" && "pairingCode" in value && typeof value.pairingCode === "string"
        && "ssid" in value && typeof value.ssid === "string" && "password" in value
        && typeof value.password === "string") {
        const pairingCode = value.pairingCode.replace(/[\s-]/g, "");
        if (!/^(?:\d{11}|\d{21})$/.test(pairingCode)) {
            throw new MatterError("InvalidCode", "Enter the printed 11- or 21-digit Matter setup code.");
        }
        try { ManualPairingCodeCodec.decode(pairingCode); }
        catch { throw new MatterError("InvalidCode", "The Matter setup code is invalid; check its digits."); }
        if (Buffer.byteLength(value.ssid, "utf8") < 1 || Buffer.byteLength(value.ssid, "utf8") > 32
            || Buffer.byteLength(value.password, "utf8") < 1 || Buffer.byteLength(value.password, "utf8") > 64) {
            throw new MatterError("InvalidWifi", "Enter a Wi-Fi name of 1-32 bytes and a password of 1-64 bytes.");
        }
        return { id, action: "pair", pairingCode, ssid: value.ssid, password: value.password };
    }
    throw new MatterError("InvalidRequest", "Unsupported or invalid local controller request.");
}

export function acceptProductionAttestation(findings: readonly AttestationFinding[]) {
    return findings.every(finding =>
        finding.level === "info" && finding.type === DeviceAttestationCheck.PaaTrustStoreTimeMismatch);
}

export function rgbToMatter(color: string, brightnessPercent: number) {
    if (!/^#[0-9a-f]{6}$/i.test(color) || !Number.isInteger(brightnessPercent)
        || brightnessPercent < 1 || brightnessPercent > 100) {
        throw new MatterError("InvalidColor", "Invalid LED color or brightness.");
    }
    const r = parseInt(color.slice(1, 3), 16) / 255;
    const g = parseInt(color.slice(3, 5), 16) / 255;
    const b = parseInt(color.slice(5, 7), 16) / 255;
    const max = Math.max(r, g, b), delta = max - Math.min(r, g, b);
    let hue = 0;
    if (delta !== 0) {
        if (max === r) hue = ((g - b) / delta) % 6;
        else if (max === g) hue = (b - r) / delta + 2;
        else hue = (r - g) / delta + 4;
        hue = (hue * 60 + 360) % 360;
    }
    return {
        on: max !== 0,
        hue: Math.round(hue / 360 * 254),
        saturation: max === 0 ? 0 : Math.round(delta / max * 254),
        level: max === 0 ? 0 : Math.max(1, Math.round(max * brightnessPercent / 100 * 254)),
    };
}

export interface LightOutput {
    on(): Promise<unknown>;
    off(): Promise<unknown>;
    level(value: number): Promise<unknown>;
    color(hue: number, saturation: number): Promise<unknown>;
}

export async function applyColor(output: LightOutput, color: string, brightnessPercent: number) {
    const state = rgbToMatter(color, brightnessPercent);
    if (!state.on) {
        await output.off();
        return;
    }
    await output.color(state.hue, state.saturation);
    await output.level(state.level);
    await output.on();
}

export function publicError(error: unknown) {
    if (error instanceof MatterError) return { code: error.code, message: error.message };
    return { code: "DeviceUnavailable", message: "Matter communication failed. Check the light's power and your local network, then retry." };
}
