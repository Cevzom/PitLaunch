import streamDeck from "@elgato/streamdeck";

import { RestoreDisplaysAction } from "./actions/restore-displays";
import { SwitchSetupAction } from "./actions/switch-setup";
import { ToggleSetupAction } from "./actions/toggle-setup";
import { PitLaunchClient } from "./pitlaunch/client";

// One connection for the whole plugin. Every button shares it, and it reconnects (and relaunches
// PitLaunch) on demand rather than at startup, so a deck plugged in before login still works.
const client = new PitLaunchClient();

client.on("connected", () => streamDeck.logger.info("Connected to PitLaunch."));
client.on("disconnected", () => streamDeck.logger.info("Disconnected from PitLaunch."));
client.on("protocolError", (message: string) => streamDeck.logger.warn(message));

streamDeck.actions.registerAction(new SwitchSetupAction(client));
streamDeck.actions.registerAction(new ToggleSetupAction(client));
streamDeck.actions.registerAction(new RestoreDisplaysAction(client));

streamDeck.connect();

// Polling keeps the green indicator honest even if PitLaunch never pushes a notification.
client.startWatching();
