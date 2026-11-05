import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";

const directory = resolve(process.argv[2]);
const storage = mkdtempSync(join(tmpdir(), "PresencePlus-bundle-"));
try {
    const node = join(directory, "node.exe");
    const args = [join(directory, "dist", "main.js"), "--presenceplus-data-dir", storage];
    const run = (extra, input = "") => {
        const child = spawnSync(node, [...args, ...extra], {
            cwd: directory, input, encoding: "utf8", windowsHide: true, timeout: 45_000,
        });
        if (child.error) throw child.error;
        assert.equal(child.status, 0, `Packaged helper failed: ${child.stderr}`);
        return child.stdout;
    };
    assert.equal(run(["--check-bluetooth"]), "Windows Bluetooth runtime loaded.\n");
    const replies = run([], '{"id":1,"action":"inspect"}\n{"id":2,"action":"set","color":"#3366FF","brightnessPercent":70}\n{"id":3,"action":"shutdown"}\n')
        .trim().split("\n").map(line => JSON.parse(line));
    assert.equal(replies.length, 3);
    assert.equal(replies[0].id, 1);
    assert.equal(replies[0].ok, true);
    assert.equal(replies[0].device.paired, false);
    assert.equal(replies[1].id, 2);
    assert.equal(replies[1].ok, false);
    assert.equal(replies[1].code, "NotPaired");
    assert.equal(replies[2].id, 3);
    assert.equal(replies[2].ok, true);
    console.log("Packaged helper passed Bluetooth native loading, fresh-storage inspect, protocol, and shutdown checks.");
} finally {
    rmSync(storage, { recursive: true, force: true });
}
