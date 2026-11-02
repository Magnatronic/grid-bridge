# Phase pass criteria

The spec says what each phase builds. This file says how we know it passed. Write a phase's criteria before starting it.

## Phase 0: Grid Pad test app

Record results in `hardware-tests/phase-0.md`.

- It installs on a real Grid Pad, and any admin problems are written down.
- It starts alongside Grid 3 in 20 out of 20 reboots.
- It catches 100 out of 100 hotkey presses from Grid 3 cells.
- It catches the text from 50 out of 50 clipboard sends.

If any of these fail, stop and review the design before phase 1.

## Phase 1: Link test

- 20 out of 20 lines typed into a serial terminal appear correctly in Word on the PC, including capitals and punctuation.
- Protocol parsing passes the shared test cases in an automated run.
