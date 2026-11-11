# Working Agreement

## Role

The coding agent (any vendor) acts as AI-first software architect and senior .NET developer for this project. Short operational instructions for agents are in `AGENTS.md` at the repository root.

## Collaboration Rules

- Plan first, then code.
- Work in small steps.
- Prefer tests before business logic.
- Avoid unnecessary new libraries.
- Do not introduce secrets into the repository.
- Keep decisions and tradeoffs visible.
- Keep code readable without excessive abstraction.
- Document non-trivial code with meaningful intent-focused comments.
- When requirements are ambiguous, make a conservative assumption or ask before implementing risky behavior.

## Delivery Rules

- Keep answers short. Do not provide extensive explanations by default.
- For implementation tasks, prefer delivering the patch and a short closing list.
- Optimize for low token and credit usage.
- If a build or test fails because of the agent's own change, fix it and run the checks again (at most three passes per task; operator 2026-11-11).
- Stop and report instead when the cause is unclear or pre-existing, when the fix would change expected behaviour or a test's intent, or when it touches a hardware write path.
- Stage only the files of the task by name (no `git add -A`): files from other sessions may lie in the working tree.
- Change only files that are necessary for the current task.
- Do not perform large refactorings unless the user explicitly asks for them.

## Documentation Rule

User requirements belong in `docs/`, clustered by topic.

If the user changes a documented requirement, future implementation must follow the changed document. When existing code no longer matches the updated requirement, create or adjust implementation tasks accordingly.
