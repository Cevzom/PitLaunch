import streamDeck, { action, KeyDownEvent, SingletonAction } from "@elgato/streamdeck";

import { PitLaunchClient } from "../pitlaunch/client";

/**
 * The emergency button. Its whole job is to work when the screen is unreadable, so it takes no
 * configuration: no setup to pick, nothing to get wrong beforehand.
 */
@action({ UUID: "com.cevzom.pitlaunch.restore-displays" })
export class RestoreDisplaysAction extends SingletonAction {
	readonly #client: PitLaunchClient;

	public constructor(client: PitLaunchClient) {
		super();
		this.#client = client;
	}

	public override async onKeyDown(ev: KeyDownEvent): Promise<void> {
		try {
			const result = await this.#client.restoreDisplays();
			if (result?.restored === false) {
				streamDeck.logger.warn(`Restore reported a problem: ${result.message ?? "unknown"}`);
				await ev.action.showAlert();
				return;
			}
			await ev.action.showOk();
		} catch (error) {
			streamDeck.logger.error(`Restore displays failed: ${error instanceof Error ? error.message : String(error)}`);
			await ev.action.showAlert();
		}
	}
}
