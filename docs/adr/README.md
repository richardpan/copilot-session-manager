# Architectural Decision Records

This directory contains [Architectural Decision Records](https://adr.github.io/)
(ADRs) for the Copilot Session Manager.

## What is an ADR?

An ADR captures a single significant architectural decision, the context that
forced the decision, and the consequences. It's a lightweight alternative to a
heavyweight design doc and gives future contributors the *why* behind the *what*.

## When to write one

Write an ADR when you make a decision that:

- Is hard or expensive to reverse
- Affects how multiple components fit together
- Picks one option from several valid alternatives
- Establishes a pattern other code will follow
- Adds, removes, or significantly changes a major dependency

You **don't** need an ADR for routine changes (bug fixes, refactors,
straightforward features that fit existing patterns).

## How to write one

1. Copy [`template.md`](template.md) to `NNNN-short-title.md` where `NNNN` is
   the next available number (zero-padded to 4 digits)
2. Fill in each section
3. Set status to **Proposed** while in PR review
4. Update to **Accepted**, **Rejected**, **Superseded by ADR-XXXX**, or
   **Deprecated** as appropriate
5. Reference related ADRs in a "Related" section
6. Link the ADR from the relevant code with a comment:
   `// See docs/adr/0001-conpty-for-embedded-terminal.md`

## Index

| #    | Title                                                  | Status   |
|------|--------------------------------------------------------|----------|
| 0001 | [Use ConPTY for the embedded terminal](0001-conpty-for-embedded-terminal.md) | Proposed |
| 0002 | [Read Copilot CLI's session storage directly](0002-read-copilot-cli-storage-directly.md) | Accepted |
| 0003 | [Versioned adapter layer for Copilot CLI compatibility](0003-versioned-cli-adapter.md) | Accepted |
| 0004 | [Encrypt app DB at rest with DPAPI](0004-encrypt-app-db-with-dpapi.md) | Accepted |
| 0005 | [Settings schema versioning and migration](0005-settings-schema-migration.md) | Accepted |
| 0006 | [Hand-rolled VT parser for embedded terminal](0006-vt-parser-choice.md) | Accepted |
| 0007 | [WPF terminal rendering strategy](0007-wpf-terminal-rendering.md) | Accepted |
| 0008 | [Tabbed multi-session terminal view](0008-tabbed-terminal-view.md) | Accepted |
