import commonjs from "@rollup/plugin-commonjs";
import nodeResolve from "@rollup/plugin-node-resolve";
import typescript from "@rollup/plugin-typescript";

const sdPlugin = "com.cevzom.pitlaunch.sdPlugin";

const shared = () => [
	typescript({ tsconfig: "./tsconfig.json" }),
	nodeResolve({ browser: false, exportConditions: ["node"], preferBuiltins: true }),
	commonjs()
];

/**
 * Two bundles from one source tree:
 *
 * 1. The plugin itself, as a single file inside the .sdPlugin folder. Stream Deck spawns this
 *    with its own Node runtime, so nothing from node_modules ships in the package.
 * 2. The PitLaunch layer on its own, so the tests can drive the pipe client without a Stream
 *    Deck host to connect to.
 */
export default [
	{
		input: "src/plugin.ts",
		output: {
			file: `${sdPlugin}/bin/plugin.js`,
			format: "es",
			sourcemap: true
		},
		plugins: shared(),
		external: ["ws"]
	},
	{
		input: "src/pitlaunch/index.ts",
		output: {
			file: "tests/dist/pitlaunch.mjs",
			format: "es",
			sourcemap: true
		},
		plugins: shared()
	}
];
