import streamDeck, {
	action,
	type DidReceiveSettingsEvent,
	type KeyAction,
	type KeyDownEvent,
	type SendToPluginEvent,
	SingletonAction,
	type WillAppearEvent
} from "@elgato/streamdeck";
import type { JsonValue } from "@elgato/utils";

import { PitLaunchClient, PitLaunchRequestError } from "../pitlaunch/client";
import { ProfileSummary } from "../pitlaunch/protocol";

/** State indices must match the order of "States" in manifest.json. */
const STATE_IDLE = 0;
const STATE_ACTIVE = 1;

export interface SwitchSetupSettings {
	/** PitLaunch's permanent Guid. The only thing we key on. */
	profileId?: string;
	/** Cached purely so a button can label itself before the pipe answers. Never used to activate. */
	profileName?: string;
	[key: string]: JsonValue | undefined;
}

@action({ UUID: "com.cevzom.pitlaunch.switch-setup" })
export class SwitchSetupAction extends SingletonAction<SwitchSetupSettings> {
	readonly #client: PitLaunchClient;

	public constructor(client: PitLaunchClient) {
		super();
		this.#client = client;
		// One subscription for every button of this type; each refresh walks the visible actions.
		this.#client.on("statusChanged", () => void this.#refreshAll());
		this.#client.on("profilesChanged", () => void this.#refreshAll());
	}

	public override async onWillAppear(ev: WillAppearEvent<SwitchSetupSettings>): Promise<void> {
		// Only keypads are declared in the manifest, but the payload type still allows a dial.
		if (ev.action.isKey()) await this.#render(ev.action, ev.payload.settings);
	}

	public override async onDidReceiveSettings(ev: DidReceiveSettingsEvent<SwitchSetupSettings>): Promise<void> {
		if (ev.action.isKey()) await this.#render(ev.action, ev.payload.settings);
	}

	public override async onKeyDown(ev: KeyDownEvent<SwitchSetupSettings>): Promise<void> {
		const { profileId } = ev.payload.settings;
		if (!profileId) {
			await ev.action.showAlert();
			await ev.action.setTitle("Pick a\nsetup");
			return;
		}

		try {
			const result = await this.#client.activate(profileId);
			if (result?.complete === false) {
				// PitLaunch applied what it could. That is not a success worth a tick.
				streamDeck.logger.warn(`Partial switch: ${result.message ?? "some devices were skipped"}`);
				await ev.action.showAlert();
			} else {
				await ev.action.showOk();
			}
		} catch (error) {
			await this.#reportFailure(ev, error);
		} finally {
			await this.#refreshAll();
		}
	}

	async #reportFailure(ev: KeyDownEvent<SwitchSetupSettings>, error: unknown): Promise<void> {
		await ev.action.showAlert();
		if (error instanceof PitLaunchRequestError && error.code === "PROFILE_NOT_FOUND") {
			// The setup was deleted in PitLaunch. Say so rather than silently doing nothing.
			await ev.action.setTitle("Setup\nmissing");
			streamDeck.logger.warn(`Setup ${ev.payload.settings.profileId} no longer exists.`);
			return;
		}
		streamDeck.logger.error(`Switch failed: ${error instanceof Error ? error.message : String(error)}`);
	}

	/** Repaints every visible button of this type. */
	async #refreshAll(): Promise<void> {
		for (const visible of this.actions) {
			if (!visible.isKey()) continue;
			const settings = await visible.getSettings();
			await this.#render(visible, settings);
		}
	}

	async #render(target: KeyAction<SwitchSetupSettings>, settings: SwitchSetupSettings): Promise<void> {
		if (!settings.profileId) {
			await target.setState(STATE_IDLE);
			await target.setTitle("Pick a\nsetup");
			return;
		}

		try {
			const [profiles, status] = await Promise.all([this.#client.listProfiles(), this.#client.status()]);
			const match = profiles.find((profile) => equalsId(profile.id, settings.profileId));

			if (!match) {
				// Deleted while the button still points at it.
				await target.setState(STATE_IDLE);
				await target.setTitle("Setup\nmissing");
				return;
			}

			// Renamed setups follow their id, which is why we never match on name.
			await target.setTitle(wrapTitle(match.name));
			await target.setState(equalsId(status.activeProfileId, settings.profileId) ? STATE_ACTIVE : STATE_IDLE);
		} catch {
			// PitLaunch closed. Keep the last known name so the deck still reads sensibly.
			await target.setState(STATE_IDLE);
			await target.setTitle(wrapTitle(settings.profileName ?? "PitLaunch"));
		}
	}

	/** Answers the property inspector's request for the setup list. */
	public override async onSendToPlugin(ev: SendToPluginEvent<JsonValue, SwitchSetupSettings>): Promise<void> {
		const payload = ev.payload as { event?: string } | undefined;
		if (payload?.event !== "getProfiles") return;

		try {
			const profiles = await this.#client.listProfiles();
			await streamDeck.ui.sendToPropertyInspector({
				event: "profiles",
				profiles: profiles.map((profile: ProfileSummary) => ({ id: profile.id, name: profile.name, kind: profile.kind }))
			});
		} catch (error) {
			await streamDeck.ui.sendToPropertyInspector({
				event: "profilesError",
				message: error instanceof Error ? error.message : String(error)
			});
		}
	}
}

/** Guids differ in case between .NET and JSON round trips often enough to matter. */
function equalsId(left: string | null | undefined, right: string | null | undefined): boolean {
	if (!left || !right) return false;
	return left.toLowerCase() === right.toLowerCase();
}

/** Stream Deck keys are small; two short lines read better than one clipped one. */
function wrapTitle(name: string): string {
	if (name.length <= 9) return name;
	const middle = name.lastIndexOf(" ", Math.ceil(name.length / 2) + 3);
	if (middle <= 0) return name.slice(0, 9);
	return `${name.slice(0, middle)}\n${name.slice(middle + 1)}`;
}
