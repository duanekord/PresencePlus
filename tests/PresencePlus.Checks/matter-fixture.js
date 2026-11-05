const { createInterface } = require("node:readline");
const input = createInterface({ input: process.stdin });
input.on("line", line => {
    const request = JSON.parse(line);
    if (request.color === "#020202") return;
    if (request.color === "#030303") process.exit(2);
    process.stderr.write("Fixture diagnostic payload omitted.\n");
    const valid = request.action !== "set" ||
        (/^#[0-9a-f]{6}$/i.test(request.color) && Number.isInteger(request.brightnessPercent));
    process.stdout.write(JSON.stringify({
        id: request.color === "#010101" ? -1 : request.id,
        ok: valid,
        device: { paired: true, name: "IPC fixture", endpoint: 1 },
        message: valid ? undefined : "Invalid set request"
    }) + "\n");
});
input.on("close", () => process.exit(0));
