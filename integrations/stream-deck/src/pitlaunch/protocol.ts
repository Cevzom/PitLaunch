/**
 * The wire contract for the PitLaunch integration pipe.
 *
 * Both sides must agree on every name in this file. It is deliberately small: a Stream Deck
 * button only ever needs to list setups, activate or toggle one, ask what is active, and pull
 * the displays back from a bad state.
 *
 * Transport: a Windows named pipe, duplex, message framing is newline-delimited JSON
 * (one compact JSON object per line, UTF-8, no BOM). A trailing "\n" terminates every frame.
 */

export const PIPE_NAME = "PitLaunch.Integration.v1";

/** The full path Node needs; Windows named pipes live under this prefix. */
export const PIPE_PATH = `\\\\.\\pipe\\${PIPE_NAME}`;

/** Bumped only for breaking changes. A server seeing an unknown version should refuse politely. */
export const PROTOCOL_VERSION = 1;

export const PROTOCOL_NAME = "PitLaunch.Integration.v1";

export type Method =
	| "profiles.list"
	| "profile.activate"
	| "profile.toggle"
	| "status.get"
	| "displays.restore";

export interface Request<TParams = unknown> {
	protocol: typeof PROTOCOL_NAME;
	version: number;
	/** Unique per request. The reply carries the same id; replies may arrive out of order. */
	id: string;
	method: Method;
	params?: TParams;
}

export interface SuccessResponse<TResult = unknown> {
	protocol: typeof PROTOCOL_NAME;
	version: number;
	id: string;
	ok: true;
	result: TResult;
}

export interface ErrorResponse {
	protocol: typeof PROTOCOL_NAME;
	version: number;
	id: string;
	ok: false;
	error: { code: ErrorCode; message: string };
}

export type Response<TResult = unknown> = SuccessResponse<TResult> | ErrorResponse;

export type ErrorCode =
	| "INVALID_REQUEST"
	| "UNSUPPORTED_VERSION"
	| "UNSUPPORTED_METHOD"
	| "PROFILE_NOT_FOUND"
	| "SWITCH_FAILED"
	| "BUSY"
	| "INTERNAL_ERROR";

/**
 * Unsolicited server -> client notifications. Optional: the plugin also polls, so a server
 * that never emits these still works, it just refreshes a little later.
 */
export interface Notification {
	protocol: typeof PROTOCOL_NAME;
	version: number;
	event: "profiles.changed" | "status.changed";
	data?: unknown;
}

/** A setup, as the Stream Deck needs to see it. Ids are permanent; names are not. */
export interface ProfileSummary {
	/** PitLaunch's Guid, canonical lowercase hyphenated form. Survives renames. */
	id: string;
	name: string;
	/** "Desk" | "SimRacing" | "Auto" - free text, shown only as a hint. */
	kind?: string;
	isActive?: boolean;
}

export interface ProfilesListResult {
	profiles: ProfileSummary[];
}

export interface ActivateParams {
	profileId: string;
}

export interface ActivateResult {
	profileId: string;
	/** Current display name, useful for a no-configuration toggle button. */
	profileName?: string;
	/** False when PitLaunch applied the setup but some part of it failed (a missing monitor). */
	complete?: boolean;
	message?: string;
}

export interface StatusResult {
	/** Null when no setup is currently active. */
	activeProfileId: string | null;
	/** PitLaunch's own version string, for diagnostics only. */
	appVersion?: string;
	busy?: boolean;
}

export interface RestoreResult {
	restored: boolean;
	message?: string;
}

export function isResponse(value: unknown): value is Response {
	if (typeof value !== "object" || value === null) return false;
	const candidate = value as Partial<Response>;
	return candidate.protocol === PROTOCOL_NAME && typeof candidate.id === "string" && typeof candidate.ok === "boolean";
}

export function isNotification(value: unknown): value is Notification {
	if (typeof value !== "object" || value === null) return false;
	const candidate = value as Partial<Notification>;
	return candidate.protocol === PROTOCOL_NAME && typeof candidate.event === "string";
}

export function createRequest<TParams>(id: string, method: Method, params?: TParams): Request<TParams> {
	return { protocol: PROTOCOL_NAME, version: PROTOCOL_VERSION, id, method, params };
}
