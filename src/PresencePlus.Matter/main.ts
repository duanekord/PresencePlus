import "@matter/nodejs";
import { Environment, LogDestination, Logger, LogLevel, Seconds } from "@matter/general";
import { ClientNode, ControllerBehavior, DclBehavior, ServerNode } from "@matter/node";
import { BasicInformationClient } from "@matter/node/behaviors/basic-information";
import { ColorControlClient } from "@matter/node/behaviors/color-control";
import { LevelControlClient } from "@matter/node/behaviors/level-control";
import { OnOffClient } from "@matter/node/behaviors/on-off";
import { DclCertificateService } from "@matter/protocol";
import { ManualPairingCodeCodec } from "@matter/types";
import { createInterface } from "node:readline";
import { isAbsolute } from "node:path";
import { acceptProductionAttestation, applyColor, MatterError, parseRequest, publicError, type Request } from "./control.js";

Logger.defaultLogLevel = LogLevel.WARN;
Logger.destinations.default = LogDestination({
    level: LogLevel.WARN,
    format: message => `Matter ${LogLevel.names[message.level]}: protocol payload omitted`,
    write: text => process.stderr.write(`${text}\n`),
});

const storage = process.argv[process.argv.indexOf("--presenceplus-data-dir") + 1];
if (!process.argv.includes("--presenceplus-data-dir") || !storage || !isAbsolute(storage)) {
    throw new MatterError("InvalidStorage", "An absolute private storage directory is required.");
}
Environment.default.vars.set("storage.path", storage);
let controller: ServerNode | undefined;
let bluetoothEnabled = false;

async function loadBluetooth() {
    await import("@matter/nodejs-ble");
}

async function getController(pairing: boolean) {
    if (controller) {
        if (pairing && !bluetoothEnabled) throw new MatterError("RestartRequired", "Restart the controller before pairing.");
        return controller;
    }
    bluetoothEnabled = pairing;
    Environment.default.vars.set("ble.enable", pairing);
    if (pairing) await loadBluetooth();
    const node = await ServerNode.create(ServerNode.RootEndpointWithoutGroupcast.with(ControllerBehavior, DclBehavior), {
        id: "presenceplus-ge-controller",
        network: { ble: false, tcp: false, autoStartCommissionedPeers: false },
        controller: { adminFabricLabel: "PresencePlus", ble: pairing },
        commissioning: { enabled: false },
        dcl: {
            productionUrl: "https://on.dcl.csa-iot.org",
            fetchTestCertificates: false, acceptTestCertificates: false,
            fetchGithubCertificates: false, offline: false,
        },
    });
    controller = node;
    await node.start();
    return node;
}

function lightEndpoint(peer: ClientNode) {
    const lights = [...peer.parts].filter(endpoint => endpoint.maybeStateOf(OnOffClient) !== undefined
        && endpoint.maybeStateOf(ColorControlClient) !== undefined);
    if (lights.length !== 1) throw new MatterError("UnsupportedLight", "The paired device must expose exactly one color-light endpoint.");
    const light = lights[0];
    if (!light.maybeStateOf(ColorControlClient)?.colorCapabilities.hueSaturation
        || !light.maybeStateOf(LevelControlClient)) {
        throw new MatterError("UnsupportedLight", "This light must support hue/saturation color and brightness.");
    }
    return light;
}

function deviceInfo(peer: ClientNode) {
    const basic = peer.maybeStateOf(BasicInformationClient);
    const endpoint = lightEndpoint(peer);
    return {
        paired: true,
        name: (basic?.productName || "Matter color light").replace(/[\u0000-\u001f\u007f-\u009f]/g, "").slice(0, 80),
        endpoint: endpoint.number, vendorId: basic?.vendorId, productId: basic?.productId,
    };
}

async function execute(request: Request, signal: AbortSignal) {
    const node = await getController(request.action === "pair");
    if (request.action === "pair") {
        if ([...node.peers.commissioned].length !== 0) throw new MatterError("AlreadyPaired", "A light is already paired. No reset or replacement was performed.");
        const service = await node.act(agent => agent.get(DclBehavior).certificateService);
        await service.construction;
        if (service.offline || service.acceptsTestCertificates
            || !service.certificates.some(cert => cert.isProduction && (cert.kind === "PAA" || cert.kind === undefined))) {
            throw new MatterError("TrustUnavailable", "Production certificate verification is unavailable. Check internet access and retry.");
        }
        const { passcode, shortDiscriminator, vendorId, productId } = ManualPairingCodeCodec.decode(request.pairingCode);
        const peer = await node.peers.commission({
            passcode, shortDiscriminator, vendorId, productId,
            wifiNetwork: { wifiSsid: request.ssid, wifiCredentials: request.password },
            discoveryCapabilities: { ble: true, onIpNetwork: true }, timeout: Seconds(120), abort: signal,
            autoSubscribe: false,
            onAttestationFailure: findings => {
                if (acceptProductionAttestation(findings)) return true;
                throw new MatterError("AttestationRejected", `Device certificate verification rejected: ${findings.map(f => `${f.type} (${f.level})`).join(", ")}.`);
            },
        });
        return deviceInfo(peer);
    }
    const peers = [...node.peers.commissioned];
    if (peers.length === 0) {
        if (request.action === "inspect") return { paired: false, name: "No light paired" };
        throw new MatterError("NotPaired", "Preview only - choose Pair light to connect your Matter strip.");
    }
    if (peers.length !== 1) throw new MatterError("MultipleLights", "This version supports one paired light per controller.");
    const peer = peers[0];
    await peer.start();
    if (request.action === "set") {
        const light = lightEndpoint(peer);
        await applyColor({
            on: () => light.commandsOf(OnOffClient).on(),
            off: () => light.commandsOf(OnOffClient).off(),
            level: level => light.commandsOf(LevelControlClient).moveToLevel({
                level, transitionTime: 0, optionsMask: { executeIfOff: true }, optionsOverride: { executeIfOff: true },
            }),
            color: (hue, saturation) => light.commandsOf(ColorControlClient).moveToHueAndSaturation({
                hue, saturation, transitionTime: 0,
                optionsMask: { executeIfOff: true }, optionsOverride: { executeIfOff: true },
            }),
        }, request.color, request.brightnessPercent);
    }
    return deviceInfo(peer);
}

async function main() {
    if (process.argv.includes("--check-bluetooth")) {
        await loadBluetooth();
        const { require } = await import("@matter/nodejs-ble/require");
        const noble = require("@stoprocent/noble") as { startScanningAsync?: unknown };
        if (typeof noble.startScanningAsync !== "function") throw new Error("Windows Bluetooth binding is unavailable.");
        process.stdout.write("Windows Bluetooth runtime loaded.\n");
        return;
    }
    const input = createInterface({ input: process.stdin, crlfDelay: Infinity });
    try {
        for await (const line of input) {
            let id = 0;
            let request: Request | undefined;
            const abort = new AbortController();
            const deadline = setTimeout(() => abort.abort(), 150_000);
            try {
                if (Buffer.byteLength(line, "utf8") > 8192) throw new MatterError("InvalidRequest", "The local request is too large.");
                let raw: unknown;
                try { raw = JSON.parse(line); }
                catch { throw new MatterError("InvalidRequest", "The local request is not valid JSON."); }
                if (typeof raw === "object" && raw !== null && "id" in raw && typeof raw.id === "number") id = raw.id;
                request = parseRequest(raw);
                id = request.id;
                if (request.action === "shutdown") {
                    process.stdout.write(`${JSON.stringify({ id, ok: true })}\n`);
                    break;
                }
                const device = await execute(request, abort.signal);
                process.stdout.write(`${JSON.stringify({ id, ok: true, device })}\n`);
            } catch (error) {
                process.stdout.write(`${JSON.stringify({ id, ok: false, ...publicError(error) })}\n`);
            } finally {
                clearTimeout(deadline);
                if (request?.action === "pair") { request.password = ""; request.pairingCode = ""; }
            }
        }
    } finally {
        const deadline = setTimeout(() => process.exit(1), 10_000);
        deadline.unref();
        input.close();
        await controller?.close();
        await Environment.default.close(DclCertificateService);
        await Environment.default.runtime.close();
        clearTimeout(deadline);
    }
}

try { await main(); }
catch {
    process.stderr.write("Matter controller stopped unexpectedly; no protocol payload was logged.\n");
    process.exitCode = 1;
}
