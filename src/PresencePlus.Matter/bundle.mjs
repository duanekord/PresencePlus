import { build } from "esbuild";
import { copyFile, mkdir, readFile, readdir, writeFile } from "node:fs/promises";
import { dirname, join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const root = dirname(fileURLToPath(import.meta.url));
const destination = resolve(process.argv[2] ?? join(root, "dist", "bundle"));
const modules = join(root, "node_modules");
await mkdir(join(destination, "dist"), { recursive: true });

const result = await build({
    absWorkingDir: root,
    entryPoints: ["main.ts"],
    outdir: join(destination, "dist"),
    chunkNames: "chunks/[name]-[hash]",
    // Keep platform-specific dynamic imports lazy (for example Bun's optional SQLite backend).
    splitting: true,
    bundle: true,
    platform: "node",
    target: "node24",
    format: "esm",
    conditions: ["import"],
    external: ["@stoprocent/noble", "@stoprocent/bleno", "bun:sqlite"],
    banner: { js: 'import { createRequire as presencePlusRequire } from "node:module"; const require = presencePlusRequire(import.meta.url);' },
    metafile: true,
    legalComments: "inline",
    logLevel: "info",
});
await writeFile(join(destination, "package.json"), JSON.stringify({ private: true, type: "module" }) + "\n");

// Noble's Windows binding resolves its native addon relative to the original package directory.
const runtime = {
    "@stoprocent/noble": [
        "package.json", "index.js", "lib/characteristic.js", "lib/characteristics.json",
        "lib/descriptor.js", "lib/descriptors.json", "lib/hci-status-message.js",
        "lib/noble-event-emitter.js", "lib/noble.js", "lib/pairing-kinds.js",
        "lib/pairing-protection-level.js", "lib/peripheral.js", "lib/resolve-bindings.js",
        "lib/service.js", "lib/services.json", "lib/uint32.js", "lib/win/bindings.js",
        "lib/hci-socket/hci-status.json",
        "prebuilds/win32-x64/node.napi.node",
    ],
    "node-gyp-build": ["package.json", "index.js", "node-gyp-build.js"],
    "debug": ["package.json", "src/index.js", "src/node.js", "src/common.js", "src/browser.js"],
    "ms": ["package.json", "index.js"],
};
for (const [name, files] of Object.entries(runtime)) {
    for (const file of files) {
        const target = join(destination, "node_modules", name, file);
        await mkdir(dirname(target), { recursive: true });
        await copyFile(join(modules, name, file), target);
    }
}

const packages = new Set(Object.keys(runtime));
for (const input of Object.keys(result.metafile.inputs)) {
    const segments = input.replaceAll("\\", "/").split("node_modules/").at(-1)?.split("/");
    if (!input.includes("node_modules/") || !segments) continue;
    packages.add(segments[0].startsWith("@") ? segments.slice(0, 2).join("/") : segments[0]);
}
const inventory = [];
for (const name of [...packages].sort()) {
    const source = join(modules, name);
    const metadata = JSON.parse(await readFile(join(source, "package.json"), "utf8"));
    const files = (await readdir(source, { withFileTypes: true }))
        .filter(entry => entry.isFile() && /^(licen[cs]e|notice|copyright)([.-]|$)/i.test(entry.name));
    if (!files.some(file => /^licen[cs]e/i.test(file.name))) throw new Error(`Missing license for ${name}`);
    const target = join(destination, "licenses", name.replaceAll("/", "_"));
    await mkdir(target, { recursive: true });
    for (const file of files) await copyFile(join(source, file.name), join(target, file.name));
    inventory.push({ name, version: metadata.version, license: metadata.license, notices: relative(destination, target) });
}
await writeFile(join(destination, "DEPENDENCIES.json"), JSON.stringify(inventory, null, 2) + "\n");
console.log(`Bundled Matter controller and ${inventory.length} dependency notices; Windows x64 Bluetooth assets retained.`);
