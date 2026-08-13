import { spawn } from "node:child_process";
import { existsSync } from "node:fs";
import path from "node:path";

/**
 * Where an installed PitLaunch might be.
 *
 * The brief names %LOCALAPPDATA%\PitLaunch\PitLaunch.exe, but Velopack keeps the application
 * body in a versioned "current" folder beside a launcher stub, and which of the two is the real
 * executable depends on how the installer was built. Probing costs nothing and a wrong guess
 * costs the user a button that silently does nothing, so try each in turn.
 */
export function candidateExecutablePaths(env: NodeJS.ProcessEnv = process.env): string[] {
	const localAppData = env.LOCALAPPDATA;
	if (!localAppData) return [];
	const root = path.join(localAppData, "PitLaunch");
	return [
		path.join(root, "PitLaunch.exe"),
		path.join(root, "current", "PitLaunch.exe")
	];
}

/** The first candidate that exists, or undefined when PitLaunch is not installed at all. */
export function findExecutable(env: NodeJS.ProcessEnv = process.env): string | undefined {
	return candidateExecutablePaths(env).find((candidate) => existsSync(candidate));
}

export interface LaunchOutcome {
	started: boolean;
	/** Populated when the executable could not be found, for a useful log line. */
	reason?: string;
}

/**
 * Starts PitLaunch without stealing focus. "--background" leaves it in the tray, which matters
 * because pressing a Stream Deck button should switch a setup, not throw a window over a game.
 */
export function launchPitLaunch(env: NodeJS.ProcessEnv = process.env): LaunchOutcome {
	const executable = findExecutable(env);
	if (!executable) {
		return { started: false, reason: `PitLaunch was not found in ${candidateExecutablePaths(env).join(" or ")}` };
	}

	try {
		const child = spawn(executable, ["--background"], {
			detached: true,
			stdio: "ignore",
			windowsHide: true
		});
		// Let it outlive the plugin process; Stream Deck may restart us at any time.
		child.unref();
		return { started: true };
	} catch (error) {
		return { started: false, reason: error instanceof Error ? error.message : String(error) };
	}
}
