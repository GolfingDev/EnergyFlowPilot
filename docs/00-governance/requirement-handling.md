# Requirement Handling

## Purpose

This document defines how project requirements are captured and used.

## Process

- New user requirements are added to the most fitting document in `docs/`.
- If no fitting document exists, create a new focused document instead of expanding an unrelated one.
- Requirements should be written as clear rules, not as chat summaries.
- Implementation decisions should reference the relevant document when useful.
- Conflicts between documents must be resolved before code is changed.

## Priority

When requirements conflict, use this order:

1. Explicit user instruction in the current task.
2. Current documents in `docs/`.
3. Existing code conventions.
4. Previous chat context.

## Change Impact

Changing a document in `docs/` may require code changes. The expected workflow is:

1. Update the relevant documentation.
2. Add or adjust tests that describe the changed behavior.
3. Update implementation.
4. Run verification.
