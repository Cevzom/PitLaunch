import { EventEmitter } from "node:events";
import net from "node:net";
import { randomUUID } from "node:crypto";

import { launchPitLaunch } from "./launcher";
import {
	ActivateParams,
	ActivateResult,
	Method,
	PIPE_PATH,
	ProfileSummary,
	ProfilesListResult,
	Request,
	RestoreResult,
	StatusResult,
	createRequest,
	isNotification,
	isResponse
} from "./protocol";

/** Waits between reconnect attempts. Bounded on purpose: a dead pipe must not spin forever. */
const RETRY_DELAYS_MS = [200, 400, 800, 1200, 1600];

/** Listing and status are cheap. Activating changes monitors and audio, so it gets far longer. */
const FAST_TIMEOUT_MS = 5_000;
const SLOW_TIMEOUT_MS = 25_000;

export class PitLaunchUnavailableError extends Error {
	public constructor(message: string) {
		super(message);
		this.name = "PitLaunchUnavailableError";
	}
}

export class PitLaunchRequestError extends Error {
	public constructor(public readonly code: string, message: string) {
		super(message);
		this.name = "PitLaunchRequestError";
	}
}

interface Pending {
	resolve: (value: unknown) => void;
	reject: (reason: Error) => void;
	timer: NodeJS.Timeout;
}

/**
 * Talks to PitLaunch over its integration pipe.
 *
 * One connection is shared by every button, because a Stream Deck page can hold several
 * PitLaunch actions and each of them would otherwise open its own pipe.
 */
export class PitLaunchClient extends EventEmitter {
	readonly #pipePath: string;
	readonly #autoLaunch: boolean;
	readonly #pending = new Map<string, Pending>();
	#socket?: net.Socket;
	#buffer = "";
	#connecting?: Promise<void>;
	#disposed = false;
	#lastProfileSignature = "";
	#lastActiveProfileId: string | null | undefined;
	#watchTimer?: NodeJS.Timeout;

	public constructor(pipePath: string = PIPE_PATH, options: { autoLaunch?: boolean } = {}) {
		super();
		this.#pipePath = pipePath;
		this.#autoLaunch = options.autoLaunch ?? true;
	}

	public get connected(): boolean {
		return this.#socket !== undefined && !this.#socket.destroyed;
	}

	/**
	 * Connects, starting PitLaunch first if the pipe is not there. A single launch is attempted
	 * per call: if the app is genuinely broken, retrying the spawn just stacks up processes.
	 *
	 * `allowLaunch` is false for background polling. Someone who closed PitLaunch on purpose
	 * should not have it reappear every few seconds because a deck is sitting on their desk;
	 * only a deliberate button press may start it.
	 */
	public async ensureConnected(allowLaunch = true): Promise<void> {
		if (this.connected) return;
		if (this.#connecting) return this.#connecting;

		this.#connecting = this.#connectWithRetry(allowLaunch && this.#autoLaunch).finally(() => {
			this.#connecting = undefined;
		});
		return this.#connecting;
	}

	async #connectWithRetry(allowLaunch: boolean): Promise<void> {
		let launched = false;
		let lastError = "the pipe did not accept a connection";

		for (let attempt = 0; attempt <= RETRY_DELAYS_MS.length; attempt++) {
			if (this.#disposed) throw new PitLaunchUnavailableError("The client was disposed.");
			try {
				await this.#openSocket();
				this.emit("connected");
				return;
			} catch (error) {
				lastError = error instanceof Error ? error.message : String(error);

				// A passive attempt gets exactly one try: it is a poll, not a request.
				if (!allowLaunch) break;

				// First failure is the interesting one: PitLaunch is probably just closed.
				if (!launched) {
					launched = true;
					const outcome = launchPitLaunch();
					if (!outcome.started && outcome.reason) lastError = outcome.reason;
				}
			}

			const delay = RETRY_DELAYS_MS[attempt];
			if (delay === undefined) break;
			await new Promise((resolve) => setTimeout(resolve, delay));
		}

		throw new PitLaunchUnavailableError(`PitLaunch is not responding: ${lastError}`);
	}

	#openSocket(): Promise<void> {
		return new Promise((resolve, reject) => {
			const socket = net.connect({ path: this.#pipePath });
			const onError = (error: Error): void => {
				socket.destroy();
				reject(error);
			};

			socket.once("error", onError);
			socket.once("connect", () => {
				socket.off("error", onError);
				socket.setEncoding("utf8");
				socket.on("data", (chunk: string) => this.#onData(chunk));
				socket.on("close", () => this.#onClose());
				socket.on("error", () => this.#onClose());
				this.#socket = socket;
				this.#buffer = "";
				resolve();
			});
		});
	}

	#onClose(): void {
		this.#socket = undefined;
		this.#buffer = "";
		// Nothing will ever answer these now; failing fast beats a button that hangs.
		for (const [id, pending] of this.#pending) {
			clearTimeout(pending.timer);
			pending.reject(new PitLaunchUnavailableError("The connection to PitLaunch closed."));
			this.#pending.delete(id);
		}
		this.emit("disconnected");
	}

	#onData(chunk: string): void {
		this.#buffer += chunk;
		let newline = this.#buffer.indexOf("\n");
		while (newline >= 0) {
			const line = this.#buffer.slice(0, newline).trim();
			this.#buffer = this.#buffer.slice(newline + 1);
			if (line.length > 0) this.#onLine(line);
			newline = this.#buffer.indexOf("\n");
		}
	}

	#onLine(line: string): void {
		let parsed: unknown;
		try {
			parsed = JSON.parse(line);
		} catch {
			this.emit("protocolError", `Ignored a frame that was not JSON: ${line.slice(0, 120)}`);
			return;
		}

		if (isNotification(parsed)) {
			this.emit(parsed.event === "profiles.changed" ? "profilesChanged" : "statusChanged");
			return;
		}

		if (!isResponse(parsed)) {
			this.emit("protocolError", "Ignored a frame that was neither a response nor a notification.");
			return;
		}

		const pending = this.#pending.get(parsed.id);
		// An unknown id means a reply to something that already timed out. Dropping it is correct.
		if (!pending) return;
		clearTimeout(pending.timer);
		this.#pending.delete(parsed.id);

		if (parsed.ok) pending.resolve(parsed.result);
		else pending.reject(new PitLaunchRequestError(parsed.error.code, parsed.error.message));
	}

	async #send<TResult>(method: Method, params: unknown, timeoutMs: number, allowLaunch = true): Promise<TResult> {
		await this.ensureConnected(allowLaunch);
		const socket = this.#socket;
		if (!socket) throw new PitLaunchUnavailableError("PitLaunch is not connected.");

		const id = randomUUID();
		const request: Request = createRequest(id, method, params);

		return new Promise<TResult>((resolve, reject) => {
			const timer = setTimeout(() => {
				this.#pending.delete(id);
				reject(new PitLaunchUnavailableError(`${method} timed out after ${timeoutMs} ms.`));
			}, timeoutMs);

			this.#pending.set(id, { resolve: resolve as (value: unknown) => void, reject, timer });
			socket.write(`${JSON.stringify(request)}\n`, (error) => {
				if (!error) return;
				clearTimeout(timer);
				this.#pending.delete(id);
				reject(new PitLaunchUnavailableError(error.message));
			});
		});
	}

	public async listProfiles(): Promise<ProfileSummary[]> {
		const result = await this.#send<ProfilesListResult>("profiles.list", {}, FAST_TIMEOUT_MS);
		return Array.isArray(result?.profiles) ? result.profiles : [];
	}

	public activate(profileId: string): Promise<ActivateResult> {
		return this.#send<ActivateResult>("profile.activate", { profileId } satisfies ActivateParams, SLOW_TIMEOUT_MS);
	}

	/** Switches to the opposite Desk/Sim Racing setup without per-button configuration. */
	public toggleProfile(): Promise<ActivateResult> {
		return this.#send<ActivateResult>("profile.toggle", {}, SLOW_TIMEOUT_MS);
	}

	public status(): Promise<StatusResult> {
		return this.#send<StatusResult>("status.get", {}, FAST_TIMEOUT_MS);
	}

	public restoreDisplays(): Promise<RestoreResult> {
		return this.#send<RestoreResult>("displays.restore", {}, SLOW_TIMEOUT_MS);
	}

	/**
	 * Polls so the buttons stay honest even if the server never sends a notification. Changes
	 * are diffed before anything is emitted, so a quiet machine causes no redraws.
	 */
	public startWatching(intervalMs = 4_000): void {
		if (this.#watchTimer) return;
		this.#watchTimer = setInterval(() => void this.#poll(), intervalMs);
		// Do not hold the process open on our account.
		this.#watchTimer.unref?.();
	}

	async #poll(): Promise<void> {
		if (this.#disposed) return;
		try {
			// Passive: reconnects to a PitLaunch that came back on its own, never starts one.
			const status = await this.#send<StatusResult>("status.get", {}, FAST_TIMEOUT_MS, false);
			if (status.activeProfileId !== this.#lastActiveProfileId) {
				this.#lastActiveProfileId = status.activeProfileId;
				this.emit("statusChanged");
			}

			const listed = await this.#send<ProfilesListResult>("profiles.list", {}, FAST_TIMEOUT_MS, false);
			const profiles = Array.isArray(listed?.profiles) ? listed.profiles : [];
			const signature = profiles.map((profile) => `${profile.id}:${profile.name}`).join("|");
			if (signature !== this.#lastProfileSignature) {
				this.#lastProfileSignature = signature;
				this.emit("profilesChanged");
			}
		} catch {
			// Expected whenever PitLaunch is closed. The next press reconnects and relaunches.
		}
	}

	public dispose(): void {
		this.#disposed = true;
		if (this.#watchTimer) clearInterval(this.#watchTimer);
		this.#watchTimer = undefined;
		this.#socket?.destroy();
		this.#socket = undefined;
	}
}
