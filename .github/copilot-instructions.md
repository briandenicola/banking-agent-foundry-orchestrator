# Copilot instructions for this repository

## Repository status

This repository currently does not contain application source code, package manifests, or project tooling files. The only top-level content present is the .github directory.

## Build, test, and lint commands

No build, test, or lint commands are currently defined for this repository.

- There is no package.json, pyproject.toml, requirements.txt, Makefile, justfile, tox.ini, pytest.ini, Cargo.toml, go.mod, or similar project manifest to inspect.
- Do not assume a language runtime or test runner exists until a project manifest is added.

## High-level architecture

The repository is effectively a blank slate at the moment. There is no existing application architecture to reason about, so changes should stay minimal and avoid introducing framework or build-system conventions unless they are explicitly requested.

When new project files are added later, prefer keeping the structure straightforward and documented in the repository root so future sessions can discover it quickly.

## Key conventions

- Keep changes scoped to the repository's current contents; there is no established codebase layout to preserve yet.
- If you add new tooling or project files, document the relevant commands in the repository root documentation so future sessions can follow them.
- Use .github/copilot-instructions.md as the place for repository-specific guidance that would otherwise be easy to miss.

## Notes for future sessions

- There is no README.md, CONTRIBUTING.md, or other existing instruction file to incorporate from at this time.
- Avoid adding framework-specific conventions or build pipelines until the repository actually contains code that needs them.
