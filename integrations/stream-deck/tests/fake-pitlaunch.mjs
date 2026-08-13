import net from "node:net";

const PROTOCOL_NAME = "PitLaunch.Integration.v1";

/**
 * A stand-in for the PitLaunch side of the pipe.
 *
 * It is a real Windows named pipe speaking the real framing, so the tests exercise the actual
 * socket path rather than a mocked-out transport. Behaviour is steerable per test: which
 * profiles exist, what is active, whether a method fails, and how slowly it answers.
 */
export class FakePitLaunch {
	constructor(pipeName) {
		this.pipePath = `\\\\.\\pipe\\${pipeName}`;
		this.profiles = [];
		this.activeProfileId = null;
		this.failWith = null;
		this.delayMs = 0;
		this.received = [];
		this.sockets = new Set();
		this.server = null;
	}

	start() {
		return new Promise((resolve, reject) => {
			this.server = net.createServer((socket) => {
				this.sockets.add(socket);
				socket.setEncoding("utf8");
				let buffer = "";
				socket.on("data", (chunk) => {
					buffer += chunk;
					let newline = buffer.indexOf("\n");
					while (newline >= 0) {
						const line = buffer.slice(0, newline).trim();
						buffer = buffer.slice(newline + 1);
						if (line) this.#handle(socket, line);
						newline = buffer.indexOf("\n");
					}
				});
				socket.on("close", () => this.sockets.delete(socket));
				socket.on("error", () => this.sockets.delete(socket));
			});
			this.server.once("error", reject);
			this.server.listen(this.pipePath, () => resolve());
		});
	}

	#handle(socket, line) {
		const request = JSON.parse(line);
		this.received.push(request);

		const reply = (body) => {
			const frame = JSON.stringify({ protocol: PROTOCOL_NAME, version: 1, id: request.id, ...body });
			if (this.delayMs > 0) setTimeout(() => socket.write(frame + "\n"), this.delayMs);
			else socket.write(frame + "\n");
		};

		if (this.failWith) {
			reply({ ok: false, error: this.failWith });
			return;
		}

		switch (request.method) {
			case "profiles.list":
				reply({
					ok: true,
					result: {
						profiles: this.profiles.map((p) => ({ ...p, isActive: p.id === this.activeProfileId }))
					}
				});
				break;
			case "profile.activate": {
				const id = request.params?.profileId;
				const found = this.profiles.find((p) => p.id.toLowerCase() === String(id).toLowerCase());
				if (!found) {
					reply({ ok: false, error: { code: "PROFILE_NOT_FOUND", message: `No setup with id ${id}.` } });
					break;
				}
				this.activeProfileId = found.id;
				reply({ ok: true, result: { profileId: found.id, complete: true } });
				break;
			}
			case "profile.toggle": {
				if (this.profiles.length === 0) {
					reply({ ok: false, error: { code: "PROFILE_NOT_FOUND", message: "No setups exist." } });
					break;
				}
				const active = this.profiles.find((p) => p.id === this.activeProfileId);
				const desiredKind = active?.kind === "SimRacing" ? "Desk" : "SimRacing";
				const found = this.profiles.find((p) => p.kind === desiredKind && p.id !== active?.id)
					?? this.profiles[(Math.max(0, this.profiles.indexOf(active)) + 1) % this.profiles.length];
				this.activeProfileId = found.id;
				reply({
					ok: true,
					result: { profileId: found.id, profileName: found.name, complete: true, message: "Profile active" }
				});
				break;
			}
			case "status.get":
				reply({ ok: true, result: { activeProfileId: this.activeProfileId, appVersion: "0.9.9-beta.1" } });
				break;
			case "displays.restore":
				reply({ ok: true, result: { restored: true } });
				break;
			default:
				reply({ ok: false, error: { code: "UNSUPPORTED_METHOD", message: `Unknown method ${request.method}.` } });
		}
	}

	/** Pushes an unsolicited notification to every connected client. */
	notify(event) {
		const frame = JSON.stringify({ protocol: PROTOCOL_NAME, version: 1, event }) + "\n";
		for (const socket of this.sockets) socket.write(frame);
	}

	async stop() {
		for (const socket of this.sockets) socket.destroy();
		this.sockets.clear();
		if (!this.server) return;
		await new Promise((resolve) => this.server.close(resolve));
		this.server = null;
	}
}
