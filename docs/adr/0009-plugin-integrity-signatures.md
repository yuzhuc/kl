# ADR 0009: Plugin integrity and Ed25519 signatures

- Status: accepted
- Date: 2026-10-18

## Context

Sidecar and WASM plugins execute code that the host trusts. Manifest v1
currently controls permissions, but nothing binds the manifest to the plugin
binary, and a tampered installed plugin would run unnoticed. A full trust store
or marketplace is out of scope for this stage, but the host needs a local,
fail-closed check that works for both runtimes and preserves the active version
when an upgrade fails.

## Decision

- Manifest v1 adds `integrity.sha256`, the SHA-256 of the entrypoint file, and
  an optional `signature` object with `algorithm = "ed25519"` and a
  standard-base64 signature value.
- The signature covers the compact JSON produced by
  `PluginManifest::signing_payload()`: the manifest with `signature` removed,
  defaults materialized, and the current struct field order. This binds the
  manifest fields, permissions and entrypoint digest to one Ed25519 signature.
- The public key comes from `security.json` in the plugin host root (the
  directory containing `installed/`) or from `KL_PLUGIN_PUBLIC_KEY`, with the
  config file taking precedence. Keys are raw 32-byte Ed25519 public keys
  encoded with standard base64. Private keys are never read, stored, logged or
  included in errors.
- `require_signature` defaults to false so unsigned development plugins keep
  working. When true, `integrity`, `signature` and a configured public key are
  mandatory. Any integrity or signature field that is present is verified even
  in development mode.
- A single `PluginVerifier` gate runs in `PluginHost::start_instance` before
  spawning a sidecar or instantiating a WASM component. Install and reload
  verify the candidate, start it, and only then replace the active instance;
  failure leaves the old instance running.
- Reload re-reads the manifest from disk so on-disk tampering is detected
  instead of reusing an already-loaded manifest.

## Consequences

- Tampered entrypoints, wrong keys and missing signatures fail before new code
  runs, and failed upgrades retain the previous version.
- The hash covers only the entrypoint. Mutable configuration and ancillary
  files are intentionally excluded; a future design may add a signed file
  list.
- The signing payload is coupled to the manifest field set. Adding fields
  requires a versioned payload or re-signing all plugins.
- There is no key rotation, revocation or marketplace trust chain yet. Those
  are follow-up work.
- Verification and process start are separate operations. The installed
  plugin directory must be host-owned and not writable by untrusted users to
  avoid a verify-then-execute race.
