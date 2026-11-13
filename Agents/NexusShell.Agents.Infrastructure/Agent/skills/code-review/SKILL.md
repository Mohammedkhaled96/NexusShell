---
name: code-review
description: Structured checklist for reviewing code changes for correctness, security, reliability and maintainability, with severity levels for findings.
---
# Code review checklist

Report findings most severe first. Severity levels:
- **Critical**: data loss, security vulnerability, crash on a common path.
- **Major**: wrong result in a realistic case, resource leak, race condition.
- **Minor**: edge case, unclear error message, missing test.

## Correctness
- Null / empty / boundary inputs, off-by-one errors, integer overflow.
- Error paths: are exceptions caught at the right level and not swallowed silently?
- Async code: missing `await`, blocking on `.Result`, missing cancellation, unobserved tasks.
- Concurrency: shared mutable state without synchronization.

## Security
- Injection: SQL, shell command, path traversal, untrusted input in file paths or URLs.
- Secrets: hard-coded keys or tokens, secrets written to logs or exceptions.
- Deserialization of untrusted data, overly broad permissions, missing input validation.

## Reliability
- Disposal of streams, processes, HTTP responses (`using` / `await using`).
- Timeouts and retries for network calls; idempotency of retried operations.

## Maintainability
- Names that say what things do, small functions, no duplicated logic.
- Tests for the changed behavior, including one failure case.
