# Coding Standards

## General Rules

- Keep code readable and understandable.
- Avoid spaghetti code.
- Avoid excessive abstraction layers.
- Introduce abstractions only when they make the code easier to test, replace, or understand.
- Prefer clear names and small methods over clever implementations.
- Use descriptive variable, method, class and interface names.
- Avoid cryptic abbreviations unless they are established domain terms.
- Write out `Battery Decision Engine` or `Decision Engine` in project documentation, XML comments and code comments.
- Source code, variable names, type names and method names may be written in English.
- Error messages intended for logs, exceptions, API responses or UI display must be written in clear, understandable German.

## Business Logic

- No business logic in controllers.
- Controllers and endpoints are responsible for HTTP concerns only:
  - routing
  - request/response mapping
  - status codes
  - calling application/business services
- Business decisions belong in the `Business` project.

## Interfaces

- Work mostly with interfaces at architectural boundaries.
- Use interfaces for:
  - hardware access
  - external APIs
  - persistence contracts
  - time access
  - application services where substitution or testing is useful
- Do not create interfaces for every small class by default.
- Avoid abstraction chains that do not add testability, replaceability or clarity.

## Frontend Contracts

- Every exchange with the frontend uses DTOs.
- API DTOs are contract types and must not expose persistence entities directly.
- Domain models should not be used as frontend response objects when a dedicated DTO makes the contract clearer.
- Mapping between DTOs and business/domain types should happen at the API or application boundary.

## Comments And Documentation In Code

- Non-trivial code blocks must be documented with meaningful comments when the intent is not immediately obvious.
- Lines that do more than a plain assignment should be written clearly and documented when a reader would otherwise need domain or implementation context.
- Comments must explain intent, assumptions, business meaning or edge cases.
- Avoid empty comments that only repeat the code.
- Prefer extracting a well-named method over adding many comments to complex inline code.

## Complexity

- Keep complexity low enough that the code remains readable.
- Prefer a few clear layers over many thin pass-through layers.
- A class should have a clear responsibility.
- A method should do one understandable thing.
- If a method needs many comments to be understandable, consider splitting it.

## Constructors And Parameter Lists

- Avoid constructors and methods that require more than two to three parameters.
- If more values are needed, prefer passing one clearly named parameter object.
- For configuration-style objects, prefer readable property assignments or options objects over long positional constructor calls.
- Existing long constructors should be refactored when the touched code would otherwise become harder to read.
- Exceptions are allowed only when the local pattern is already established and readability remains clearly better than an additional object.
