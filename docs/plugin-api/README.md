# Plugin API boundaries

The MVP defines the interfaces that external plugins will implement in stage 2.
It does **not** load external code yet.

## Stable interfaces

### Provider

```rust
trait Provider: Send + Sync {
    fn id(&self) -> &str;
    fn kind(&self) -> &str;
    fn models(&self) -> Vec<ModelInfo>;
    fn available(&self) -> (bool, String);
    fn begin_turn(&self);
    fn stream_chat(
        &self,
        request: &ChatRequest,
        on_event: &mut dyn FnMut(ChatEvent),
    ) -> Result<ChatOutcome, ProviderError>;
}
```

The agent sees only neutral `ChatRequest`/`ChatEvent`/`ChatOutcome` types.

### Tool

```rust
trait Tool: Send + Sync {
    fn name(&self) -> &'static str;
    fn description(&self) -> &'static str;
    fn parameters(&self) -> serde_json::Value;
    fn policy_request(&self, args: &Value) -> Result<PolicyRequest, ToolError>;
    fn execute(&self, ctx: &ToolContext, args: &Value) -> Result<ToolOutcome, ToolError>;
}
```

Tools must not bypass `PolicyEngine`; the agent evaluates policy and approval
before calling `execute`, and tools re-check the path policy.

### Audit

```rust
trait AuditSink: Send + Sync {
    fn record(&self, record: &AuditRecord) -> Result<(), AuditError>;
}
```

### Policy

Policy lives in the kernel (`kl-policy`), not in a replaceable plugin. External
policy plugins will only supply additional rules through a future sidecar API.

## Plugin types and runtime

| Type | Runtime | Notes |
|---|---|---|
| provider | sidecar or built-in Rust | protocol adapter only |
| tool | sidecar or WASM | file/shell/network capabilities must be granted |
| policy | sidecar rules | cannot disable kernel deny rules |
| audit | sidecar rules | cannot suppress kernel audit records |
| command/renderer | sidecar | CLI-facing extensions |

## Forbidden

- No Rust `dlopen`/`cdylib` plugin ABI. Rust has no stable ABI and hot-swapping
  it is unsafe.
- Plugins do not run inside the trusted core process.
- Plugins do not receive secrets unless explicitly granted.
- Plugin crashes must not terminate `kl-core`.

## Manifest (planned v1)

```yaml
id: provider-example
version: 0.1.0
api: 1
kind: provider
entrypoint: ./bin/provider-example
runtime: sidecar
capabilities: [chat, tools, streaming]
permissions:
  network: [api.example.com]
  env: [EXAMPLE_API_KEY]
config_schema: ./schema.json
```

Stage 2 will add manifest verification, health checks, traffic switching,
draining, capability tokens and transactional configuration rollback.
