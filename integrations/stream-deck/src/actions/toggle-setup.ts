import streamDeck, { action, type KeyAction, type KeyDownEvent, SingletonAction, type WillAppearEvent } from "@elgato/streamdeck";

import { PitLaunchClient } from "../pitlaunch/client";

/**
 * Zero-configuration Desk <-> Rig button. PitLaunch owns the selection rules so the tray,
 * command line and every control surface all choose the same opposite setup.
 */
@action({ UUID: "com.cevzom.pitlaunch.toggle-setup" })
export class ToggleSetupAction extends SingletonAction {
	readonly #client: PitLaunchClient;

	public constructor(client: PitLaunchClient) {
		super();
		this.#client = client;
		this.#client.on("statusChanged", () => void this.#refreshAll());
	}

	public override async onWillAppear(ev: WillAppearEvent): Promise<void> {
		if (ev.action.isKey()) await this.#render(ev.action);
	}

	public override async onKeyDown(ev: KeyDownEvent): Promise<void> {
		try {
			const result = await this.#client.toggleProfile();
			await ev.action.setTitle(wrapTitle(result.profileName ?? "Toggle"));
			if (result.complete === false) {
				streamDeck.logger.warn(`Partial toggle: ${result.message ?? "some devices were skipped"}`);
				await ev.action.showAlert();
			} else {
				await ev.action.showOk();
			}
		} catch (error) {
			streamDeck.logger.error(`Toggle setup failed: ${error instanceof Error ? error.message : String(error)}`);
			await ev.action.showAlert();
		} finally {
			await this.#refreshAll();
		}
	}

	async #refreshAll(): Promise<void> {
		for (const visible of this.actions) {
			if (visible.isKey()) await this.#render(visible);
		}
	}

	async #render(target: KeyAction): Promise<void> {
		try {
			const [profiles, status] = await Promise.all([this.#client.listProfiles(), this.#client.status()]);
			const active = profiles.find((profile) => equalsId(profile.id, status.activeProfileId));
			await target.setTitle(active ? wrapTitle(active.name) : "Desk ↔\nRig");
		} catch {
			await target.setTitle("Desk ↔\nRig");
		}
	}
}

function equalsId(left: string | null | undefined, right: string | null | undefined): boolean {
	return Boolean(left && right && left.toLowerCase() === right.toLowerCase());
}

function wrapTitle(name: string): string {
	if (name.length <= 9) return name;
	const middle = name.lastIndexOf(" ", Math.ceil(name.length / 2) + 3);
	if (middle <= 0) return name.slice(0, 9);
	return `${name.slice(0, middle)}\n${name.slice(middle + 1)}`;
}
