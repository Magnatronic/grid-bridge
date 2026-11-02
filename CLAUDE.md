# Grid Bridge

The spec is the source of truth: https://claude.ai/artifact/FeVuEiPemRL3xESZMPto8o
Current phase: 0 (Grid Pad test app). Pass criteria: `docs/phases.md`

## Scope
- Only build what the current phase needs. No hooks or abstractions for later phases.
- New ideas go in one line in `docs/later.md`, not in the code.
- If the spec seems wrong, stop and ask. Don't quietly work around it.
- A phase ends when its criteria in `docs/phases.md` pass. Write the next phase's criteria before starting it.

## Writing
- Replies: lead with the outcome. No recaps or option surveys.
- Docs: no new files unless asked. Keep READMEs under 20 lines.
- Code: comment only on why something non-obvious is done.

## Done means
- The automated tests pass, and new logic has a test that fails without it.
- Hardware behaviour has a dated result in `docs/hardware-tests/`.
- Report what was run and what wasn't. "Should work" is not done.

## Testing
- Protocol parsing: the C#, firmware and web code all pass one shared file of test cases.
- Keep logic testable off the device: hotkeys and serial behind small interfaces in C#, parsing and state in plain C++ in firmware.
- Grid 3, USB and radio behaviour: tested on real devices, using a checklist in `docs/hardware-tests/`.
- The spec's reliability rules come first, especially that nothing stays pressed when the link drops.
