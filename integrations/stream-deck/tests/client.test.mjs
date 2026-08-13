import assert from "node:assert/strict";
import { after, before, describe, it } from "node:test";
import { randomUUID } from "node:crypto";

import { FakePitLaunch } from "./fake-pitlaunch.mjs";
import { PitLaunchClient, PitLaunchRequestError, candidateExecutablePaths, createRequest } from "./dist/pitlaunch.mjs";

const DESK = "8f14e45f-ce8f-4a1b-9c2d-1a2b3c4d5e6f";
const SIM = "1b9d6bcd-bbfd-4b2d-9b5d-ab8dfbbd4bed";

describe("PitLaunch pipe client", () => {
	let fake;
	let client;

	before(async () => {
		// A unique pipe per run, so a crashed previous run cannot poison this one.
		fake = new FakePitLaunch(`PitLaunch.Integration.test.${randomUUID()}`);
		fake.profiles = [
			{ id: DESK, name: "Desk", kind: "Desk" },
			{ id: SIM, name: "Sim Racing", kind: "SimRacing" }
		];
		await fake.start();
		client = new PitLaunchClient(fake.pipePath);
		await client.ensureConnected();
	});

	after(async () => {
		client?.dispose();
		await fake?.stop();
	});

	it("lists setups", async () => {
		const profiles = await client.listProfiles();
		assert.equal(profiles.length, 2);
		assert.deepEqual(profiles.map((p) => p.name), ["Desk", "Sim Racing"]);
	});

	it("sends a protocol name, version and unique request id on every call", async () => {
		const before = fake.received.length;
		await client.listProfiles();
		await client.status();
		const sent = fake.received.slice(before);

		for (const request of sent) {
			assert.equal(request.protocol, "PitLaunch.Integration.v1");
			assert.equal(request.version, 1);
			assert.equal(typeof request.id, "string");
			assert.ok(request.id.length > 0);
		}
		assert.notEqual(sent[0].id, sent[1].id, "request ids must not repeat");
	});

	it("activates by id and reports it as active", async () => {
		const result = await client.activate(SIM);
		assert.equal(result.profileId, SIM);
		const status = await client.status();
		assert.equal(status.activeProfileId, SIM);
	});

	it("matches ids case-insensitively, the way .NET Guids round trip", async () => {
		const result = await client.activate(DESK.toUpperCase());
		assert.equal(result.profileId, DESK);
	});

	it("toggles between Sim Racing and Desk without button settings", async () => {
		await client.activate(DESK);
		const toRig = await client.toggleProfile();
		assert.equal(toRig.profileId, SIM);
		assert.equal(toRig.profileName, "Sim Racing");
		const toDesk = await client.toggleProfile();
		assert.equal(toDesk.profileId, DESK);
	});

	it("surfaces a deleted setup as PROFILE_NOT_FOUND rather than hanging", async () => {
		await assert.rejects(
			() => client.activate("00000000-0000-0000-0000-000000000000"),
			(error) => error instanceof PitLaunchRequestError && error.code === "PROFILE_NOT_FOUND"
		);
	});

	it("keeps a renamed setup working, because the id never changes", async () => {
		fake.profiles[0].name = "Desk (4K)";
		const profiles = await client.listProfiles();
		const renamed = profiles.find((p) => p.id === DESK);
		assert.equal(renamed.name, "Desk (4K)");
		const result = await client.activate(DESK);
		assert.equal(result.profileId, DESK);
	});

	it("correlates concurrent requests to the right replies", async () => {
		const [profiles, status, restore] = await Promise.all([
			client.listProfiles(),
			client.status(),
			client.restoreDisplays()
		]);
		assert.ok(Array.isArray(profiles));
		assert.equal(typeof status.activeProfileId, "string");
		assert.equal(restore.restored, true);
	});

	it("restores displays", async () => {
		const result = await client.restoreDisplays();
		assert.equal(result.restored, true);
	});

	it("raises profilesChanged when PitLaunch pushes a notification", async () => {
		const seen = new Promise((resolve) => client.once("profilesChanged", resolve));
		fake.notify("profiles.changed");
		await seen;
	});
});

describe("connection loss", () => {
	it("rejects in-flight requests instead of leaving a button hanging", async () => {
		const fake = new FakePitLaunch(`PitLaunch.Integration.test.${randomUUID()}`);
		fake.profiles = [{ id: DESK, name: "Desk" }];
		fake.delayMs = 5_000; // Never actually answers within the test.
		await fake.start();

		const client = new PitLaunchClient(fake.pipePath);
		await client.ensureConnected();

		const pending = client.listProfiles();
		setTimeout(() => void fake.stop(), 50);

		await assert.rejects(pending, (error) => /closed|not responding|timed out/i.test(error.message));
		client.dispose();
	});

	it("fails cleanly when nothing is listening", async () => {
		// autoLaunch off so the suite can never start a real PitLaunch on the test machine.
		const client = new PitLaunchClient(`\\\\.\\pipe\\PitLaunch.Integration.absent.${randomUUID()}`, { autoLaunch: false });
		await assert.rejects(() => client.ensureConnected(), /not responding|not found/i);
		client.dispose();
	});

	it("does not start PitLaunch from background polling", async () => {
		const client = new PitLaunchClient(`\\\\.\\pipe\\PitLaunch.Integration.absent.${randomUUID()}`);
		// A poll must be passive: one attempt, no launch, no retry storm.
		const started = Date.now();
		await assert.rejects(() => client.ensureConnected(false), /not responding/i);
		assert.ok(Date.now() - started < 1_000, "a passive attempt must not run the retry ladder");
		client.dispose();
	});
});

describe("launcher", () => {
	it("probes both the root and the Velopack current folder", () => {
		const paths = candidateExecutablePaths({ LOCALAPPDATA: "C:\\Users\\Test\\AppData\\Local" });
		assert.deepEqual(paths, [
			"C:\\Users\\Test\\AppData\\Local\\PitLaunch\\PitLaunch.exe",
			"C:\\Users\\Test\\AppData\\Local\\PitLaunch\\current\\PitLaunch.exe"
		]);
	});

	it("returns nothing rather than guessing when LOCALAPPDATA is missing", () => {
		assert.deepEqual(candidateExecutablePaths({}), []);
	});
});

describe("protocol helpers", () => {
	it("stamps requests with the shared contract", () => {
		const request = createRequest("abc", "profiles.list", {});
		assert.equal(request.protocol, "PitLaunch.Integration.v1");
		assert.equal(request.version, 1);
		assert.equal(request.id, "abc");
		assert.equal(request.method, "profiles.list");
	});
});
