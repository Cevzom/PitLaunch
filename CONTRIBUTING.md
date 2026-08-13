# Contributing to PitLaunch

Thanks for helping make PitLaunch safer on more Windows, GPU, and monitor combinations.

## Report a bug

Use the [bug report form](https://github.com/Cevzom/PitLaunch/issues/new?template=bug_report.yml). Include the exact switch you attempted, your Windows version, GPU, monitor layout, and a short log excerpt. You can create a privacy-sanitized ZIP from **Settings → Support → Export support bundle**.

Do not post `profiles.json`, tokens, email addresses, or other private data in a public issue.

## Send a change

1. Open an issue first for behavior or UI changes so the direction is clear.
2. Create a focused branch and keep unrelated formatting out of the diff.
3. Run `dotnet build -c Release` and `dotnet run -c Release -- --self-test`.
4. For Stream Deck changes, run `npm test` and `npm run validate` in `integrations/stream-deck`.
5. Open a pull request explaining the user-visible change and how you tested it.

By contributing, you agree that your work is provided under the [MIT License](LICENSE).
