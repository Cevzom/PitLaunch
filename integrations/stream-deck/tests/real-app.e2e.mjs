import assert from "node:assert/strict";
import { randomUUID } from "node:crypto";
import { spawn } from "node:child_process";
import { mkdtemp, rm, writeFile } from "node:fs/promises";
import net from "node:net";
import os from "node:os";
import path from "node:path";

import { PitLaunchClient, PitLaunchRequestError } from "./dist/pitlaunch.mjs";

const executable = path.resolve(
	process.env.PITLAUNCH_E2E_EXE ?? path.join("..", "..", "bin", "Debug", "net8.0-windows", "win-x64", "PitLaunch.exe")
);
const pipeName = `PitLaunch.Integration.e2e.${randomUUID()}`;
const pipePath = `\\\\.\\pipe\\${pipeName}`;
const dataDirectory = await mkdtemp(path.join(os.tmpdir(), "PitLaunch-integration-e2e-"));
let app;
let socket;
let client;
let ownsPrimaryInstance = false;

try {
	await writeFile(
		path.join(dataDirectory, "profiles.json"),
		JSON.stringify({ schemaVersion: 1, settings: {}, runtime: {}, profiles: [] }),
		"utf8"
	);

	app = spawn(executable, ["--background"], {
		env: {
			...process.env,
			PITLAUNCH_DATA_DIR: dataDirectory,
			PITLAUNCH_INTEGRATION_PIPE: pipeName,
			PITLAUNCH_UPDATE_POLICY_URL: "off"
		},
		stdio: "ignore",
		windowsHide: true
	});

	socket = await connectWithRetry(pipePath, app, 15_000);
	ownsPrimaryInstance = true; // The unique test pipe can only be hosted by the process above.
	client = new PitLaunchClient(pipePath, { autoLaunch: false });
	await client.ensureConnected();
	const request = makeRequester(socket);

	// Use the production plugin client while a second raw connection remains open. This proves
	// the real desktop server supports the same client and multiple simultaneous controllers.
	const listed = await client.listProfiles();
	assert.deepEqual(listed, []);

	const status = await client.status();
	assert.equal(status.activeProfileId, null);
	assert.equal(status.busy, false);
	assert.match(status.appVersion, /^\d+\.\d+/);

	await assert.rejects(
		() => client.toggleProfile(),
		(error) => error instanceof PitLaunchRequestError && error.code === "PROFILE_NOT_FOUND"
	);

	const unsupported = await request("does.not.exist", {});
	assert.equal(unsupported.ok, false);
	assert.equal(unsupported.error.code, "UNSUPPORTED_METHOD");

	console.log("Real PitLaunch integration smoke test passed (list, status, toggle error, protocol error).");
} finally {
	client?.dispose();
	socket?.destroy();
	if (ownsPrimaryInstance) {
		try {
			const exit = spawn(executable, ["--exit"], { stdio: "ignore", windowsHide: true });
			await waitForExit(exit, 5_000);
			await waitForExit(app, 5_000);
		} catch {
			app?.kill();
		}
	} else {
		app?.kill();
	}
	await rm(dataDirectory, { recursive: true, force: true });
}

function connectWithRetry(target, child, timeoutMs) {
	const deadline = Date.now() + timeoutMs;
	return new Promise((resolve, reject) => {
		const tryOnce = () => {
			if (child.exitCode !== null) {
				reject(new Error(`PitLaunch exited before opening its integration pipe (code ${child.exitCode}).`));
				return;
			}
			const candidate = net.connect({ path: target });
			candidate.once("connect", () => resolve(candidate));
			candidate.once("error", (error) => {
				candidate.destroy();
				if (Date.now() >= deadline) reject(error);
				else setTimeout(tryOnce, 150);
			});
		};
		tryOnce();
	});
}

function makeRequester(connectedSocket) {
	connectedSocket.setEncoding("utf8");
	let buffer = "";
	const pending = new Map();
	connectedSocket.on("data", (chunk) => {
		buffer += chunk;
		let newline = buffer.indexOf("\n");
		while (newline >= 0) {
			const line = buffer.slice(0, newline).trim();
			buffer = buffer.slice(newline + 1);
			if (line) {
				const frame = JSON.parse(line);
				const resolve = pending.get(frame.id);
				if (resolve) {
					pending.delete(frame.id);
					resolve(frame);
				}
			}
			newline = buffer.indexOf("\n");
		}
	});

	return (method, params) => new Promise((resolve, reject) => {
		const id = randomUUID();
		const timer = setTimeout(() => {
			pending.delete(id);
			reject(new Error(`${method} timed out.`));
		}, 10_000);
		pending.set(id, (frame) => {
			clearTimeout(timer);
			resolve(frame);
		});
		connectedSocket.write(`${JSON.stringify({
			protocol: "PitLaunch.Integration.v1",
			version: 1,
			id,
			method,
			params
		})}\n`);
	});
}

function waitForExit(child, timeoutMs) {
	if (!child || child.exitCode !== null) return Promise.resolve();
	return new Promise((resolve, reject) => {
		const timer = setTimeout(() => reject(new Error("Process exit timed out.")), timeoutMs);
		child.once("exit", () => {
			clearTimeout(timer);
			resolve();
		});
	});
}
