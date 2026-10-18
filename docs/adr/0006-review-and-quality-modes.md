# ADR 0006: automated review and quality modes

- Status: accepted
- Date: 2026-10-18

## Context

Stage 3 needs a code-review command that is independent from the runtime audit
log, plus a deeper execution mode that improves quality without introducing a
second agent runtime or a provider-specific protocol.

## Decision

1. Keep `kl audit` unchanged. Add `review.run` and `kl review` for diff review.
   The pipeline combines deterministic local rules, secret/dependency checks,
   cross-file checks, runtime audit denies/sandbox violations, and an optional
   structured LLM review. Quick/standard/strict modes control the confidence
   threshold; strict retains and marks low-confidence findings.
2. Add an optional `quality` value to `turn.start`: `fast`, `balanced`, `deep`.
   `balanced` preserves the existing single-turn behavior.
3. Implement `deep` inside the core worker as five ordinary `Agent::run_turn`
   calls: explorer, planner, implementer, reviewer, verifier. They share session
   history and the existing `ProviderRouter`. No new provider protocol or
   cross-protocol conversion is introduced.
4. Deep mode has hard limits: stage count, total tool calls, provider-reported
   token usage, wall-clock timeout, per-stage iterations, and cancellation.
   Early-stage failures are recorded and allow later stages to continue;
   implementer failure stops the turn. Reviewer/verifier prompts require
   file:line evidence and the verifier is not allowed to expand the change.
5. Role-specific provider/model overrides are optional config entries. An
   unavailable or invalid override falls back to the default route.

## Consequences

- The review fixture is deterministic and does not depend on model output.
- Deep mode is bounded and auditable, but its stage separation is orchestration
  rather than a separate multi-agent service.
- Reviewer/verifier read-only behavior is prompt-enforced and backed by a
  workspace fingerprint check that fails the deep turn on changes; policy
  approvals remain the hard security boundary.
- Repository symbol indexing is intentionally out of scope for this stage.
