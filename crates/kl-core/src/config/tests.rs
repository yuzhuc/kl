use super::*;

fn three_protocol_config() -> (&'static str, &'static str) {
    (
        r#"
[agent]
provider = "opencode"
model = "gpt-5-responses"

[providers.opencode]
enabled = true
path = "providers/opencode.toml"
"#,
        r#"
api_key_env = "OPENCODE_API_KEY"

[protocols]
openai-chat = "https://gateway.example/chat/complete"
anthropic = "https://gateway.example/messages"
openai-responses = "https://gateway.example/responses"

[[models]]
id = "gpt-5-chat"
protocol = "openai-chat"
context_window = 128000
compact_threshold = 0.8
max_output_tokens = 4096

[[models]]
id = "claude-native"
protocol = "anthropic"
context_window = 200000
compact_threshold = 0.75
max_output_tokens = 8192

[[models]]
id = "gpt-5-responses"
protocol = "openai-responses"
context_window = 256000
compact_threshold = 0.9
max_output_tokens = 16384
url = "https://gateway.example/custom/responses"
"#,
    )
}

#[test]
fn incomplete_urls_are_rejected() {
    assert!(!is_complete_url("http://"));
    assert!(!is_complete_url("http:///v1/chat"));
    assert!(!is_complete_url("gateway.example/v1/chat"));
    assert!(is_complete_url("https://gateway.example/v1/chat"));
}

#[test]
fn invalid_context_and_output_limits_are_rejected() {
    let temp = tempfile::tempdir().unwrap();
    let global = r#"
[providers.test]
enabled = true
path = "test.toml"
"#;
    let provider = r#"
api_key = "inline-key"
[protocols]
openai-chat = "https://example.test/chat"
[[models]]
id = "chat"
protocol = "openai-chat"
context_window = 0
compact_threshold = 0.5
max_output_tokens = 100
"#;
    let global_path = temp.path().join("config.toml");
    std::fs::write(&global_path, global).unwrap();
    let provider_path = temp.path().join("test.toml");
    std::fs::write(&provider_path, provider).unwrap();
    let config = Config::load(&global_path).unwrap();
    let error = ProviderCatalog::load(&config, temp.path(), temp.path()).unwrap_err();
    assert!(matches!(error, ConfigError::InvalidContextWindow { .. }));

    let provider = provider.replace("context_window = 0", "context_window = 1000");
    let provider = provider.replace("max_output_tokens = 100", "max_output_tokens = 0");
    std::fs::write(&provider_path, provider).unwrap();
    let error = ProviderCatalog::load(&config, temp.path(), temp.path()).unwrap_err();
    assert!(matches!(error, ConfigError::InvalidMaxOutput { .. }));
}

#[test]
fn provider_parse_errors_redact_inline_keys() {
    let temp = tempfile::tempdir().unwrap();
    let global = r#"
[providers.test]
enabled = true
path = "test.toml"
"#;
    let provider = r#"
api_key = "super-secret-key"
[[models]
"#;
    let global_path = temp.path().join("config.toml");
    std::fs::write(&global_path, global).unwrap();
    std::fs::write(temp.path().join("test.toml"), provider).unwrap();
    let config = Config::load(&global_path).unwrap();
    let error = ProviderCatalog::load(&config, temp.path(), temp.path())
        .unwrap_err()
        .to_string();
    assert!(!error.contains("super-secret-key"));
    assert!(!redact_api_key("api_key = \"secret\"").contains("secret"));
}

#[test]
fn loads_multiple_providers_independently() {
    let temp = tempfile::tempdir().unwrap();
    std::fs::create_dir_all(temp.path().join("providers")).unwrap();
    std::fs::write(
        temp.path().join("providers/a.toml"),
        r#"
api_key = "a-key"
[protocols]
openai-chat = "https://gateway.example/a/chat"
[[models]]
id = "a-model"
protocol = "openai-chat"
context_window = 1000
compact_threshold = 0.5
max_output_tokens = 100
"#,
    )
    .unwrap();
    std::fs::write(
        temp.path().join("providers/b.toml"),
        r#"
api_key = "b-key"
[protocols]
anthropic = "https://gateway.example/b/messages"
[[models]]
id = "b-model"
protocol = "anthropic"
context_window = 2000
compact_threshold = 0.6
max_output_tokens = 200
"#,
    )
    .unwrap();
    let global_path = temp.path().join("config.toml");
    std::fs::write(
        &global_path,
        r#"
[agent]
provider = "a"
model = "a-model"
[providers.a]
enabled = true
path = "providers/a.toml"
[providers.b]
enabled = true
path = "providers/b.toml"
"#,
    )
    .unwrap();
    let config = Config::load(&global_path).unwrap();
    let catalog = ProviderCatalog::load(&config, temp.path(), temp.path()).unwrap();
    assert_eq!(catalog.providers.len(), 2);
    assert_eq!(catalog.providers[0].models[0].model, "a-model");
    assert_eq!(catalog.providers[1].models[0].model, "b-model");
}

#[test]
fn parses_three_protocol_provider_catalog() {
    let temp = tempfile::tempdir().unwrap();
    let (global, provider) = three_protocol_config();
    std::fs::create_dir_all(temp.path().join("providers")).unwrap();
    std::fs::write(temp.path().join("providers/opencode.toml"), provider).unwrap();
    let global_path = temp.path().join("config.toml");
    std::fs::write(&global_path, global).unwrap();
    let config = Config::load(&global_path).unwrap();
    let catalog = ProviderCatalog::load(&config, temp.path(), temp.path()).unwrap();
    assert_eq!(catalog.providers.len(), 1);
    assert_eq!(catalog.providers[0].models.len(), 3);
    assert_eq!(
        catalog.providers[0].models[2].url,
        "https://gateway.example/custom/responses"
    );
}

#[test]
fn disabled_provider_is_not_loaded() {
    let temp = tempfile::tempdir().unwrap();
    let global = r#"
[agent]
provider = "opencode"
model = "gpt-5-chat"

[providers.opencode]
enabled = false
path = "providers/missing.toml"
"#;
    let global_path = temp.path().join("config.toml");
    std::fs::write(&global_path, global).unwrap();
    let config = Config::load(&global_path).unwrap();
    let catalog = ProviderCatalog::load(&config, temp.path(), temp.path()).unwrap();
    assert!(catalog.providers.is_empty());
    assert!(catalog.disabled.contains("opencode"));
}

#[test]
fn missing_protocol_url_is_rejected() {
    let temp = tempfile::tempdir().unwrap();
    let global = r#"
[providers.test]
enabled = true
path = "test.toml"
"#;
    let provider = r#"
api_key = "inline-key"
[[models]]
id = "chat"
protocol = "openai-chat"
context_window = 1000
compact_threshold = 0.5
max_output_tokens = 100
"#;
    let global_path = temp.path().join("config.toml");
    std::fs::write(&global_path, global).unwrap();
    std::fs::write(temp.path().join("test.toml"), provider).unwrap();
    let config = Config::load(&global_path).unwrap();
    let error = ProviderCatalog::load(&config, temp.path(), temp.path()).unwrap_err();
    assert!(matches!(error, ConfigError::MissingProtocolUrl { .. }));
}

#[test]
fn invalid_threshold_and_duplicate_models_are_rejected() {
    let temp = tempfile::tempdir().unwrap();
    let global = r#"
[providers.test]
enabled = true
path = "test.toml"
"#;
    let provider = r#"
api_key = "inline-key"
[protocols]
openai-chat = "https://example.test/chat"
[[models]]
id = "same"
protocol = "openai-chat"
context_window = 1000
compact_threshold = 1.5
max_output_tokens = 100
[[models]]
id = "same"
protocol = "openai-chat"
context_window = 1000
compact_threshold = 0.5
max_output_tokens = 100
"#;
    let global_path = temp.path().join("config.toml");
    std::fs::write(&global_path, global).unwrap();
    std::fs::write(temp.path().join("test.toml"), provider).unwrap();
    let config = Config::load(&global_path).unwrap();
    let error = ProviderCatalog::load(&config, temp.path(), temp.path()).unwrap_err();
    assert!(matches!(error, ConfigError::DuplicateModel { .. }));
}

#[test]
fn provider_delete_removes_config_provider_file_and_credential() {
    let temp = tempfile::tempdir().unwrap();
    let config_path = temp.path().join("config.toml");
    std::fs::write(
        &config_path,
        r#"
[agent]
provider = "deepseek"
model = "deepseek-chat"

[providers.deepseek]
enabled = true
path = "providers/deepseek.toml"
"#,
    )
    .unwrap();
    std::fs::create_dir_all(temp.path().join("providers")).unwrap();
    std::fs::create_dir_all(temp.path().join("credentials")).unwrap();
    std::fs::write(temp.path().join("providers/deepseek.toml"), "").unwrap();
    std::fs::write(temp.path().join("credentials/deepseek.key"), "sk-test").unwrap();

    let result = provider_delete(&config_path, temp.path(), "deepseek").unwrap();

    assert!(result.deleted);
    let config = Config::load(&config_path).unwrap();
    assert!(!config.providers.contains_key("deepseek"));
    assert!(config.agent.provider.is_empty());
    assert!(config.agent.model.is_empty());
    assert!(!temp.path().join("providers/deepseek.toml").exists());
    assert!(!temp.path().join("credentials/deepseek.key").exists());
    assert!(matches!(
        provider_delete(&config_path, temp.path(), "deepseek"),
        Err(ConfigError::UnknownProvider(_))
    ));
}

#[test]
fn provider_upsert_persists_model_enabled_and_reasoning() {
    let temp = tempfile::tempdir().unwrap();
    let config_path = temp.path().join("config.toml");
    let mut params = upsert_params("local", true);
    params.models[0].enabled = false;
    params.models[0].name = Some("Local Model".to_owned());
    params.models[0].reasoning_effort = ReasoningEffort::High;
    params.models[0].reasoning_efforts = vec![
        ReasoningEffort::Low,
        ReasoningEffort::Medium,
        ReasoningEffort::High,
        ReasoningEffort::Max,
    ];

    provider_upsert(&config_path, &params).unwrap();

    let config = Config::load(&config_path).unwrap();
    let catalog = ProviderCatalog::load(&config, temp.path(), temp.path()).unwrap();
    let model = &catalog.providers[0].models[0];
    assert_eq!(model.name, "Local Model");
    assert!(!model.enabled);
    assert_eq!(model.reasoning_effort.as_deref(), Some("high"));
    assert_eq!(
        model.reasoning_efforts,
        vec![
            "low".to_owned(),
            "medium".to_owned(),
            "high".to_owned(),
            "max".to_owned()
        ]
    );
    assert!(matches!(
        config_set_default_model(
            &config_path,
            temp.path(),
            &SetDefaultModelParams {
                provider: "local".to_owned(),
                model: "gpt-5-responses".to_owned(),
            },
        ),
        Err(ConfigError::ModelDisabled { .. })
    ));
}

#[test]
fn provider_upsert_repairs_a_missing_default_provider() {
    let temp = tempfile::tempdir().unwrap();
    let config_path = temp.path().join("config.toml");
    std::fs::write(
        &config_path,
        r#"
[agent]
provider = "gone"
model = "gone-model"
"#,
    )
    .unwrap();
    let params = ProviderUpsertParams {
        id: "deepseek".to_owned(),
        protocols: kl_protocol::ProviderProtocolUrls {
            openai_chat: Some("https://api.deepseek.com/v1/chat/completions".to_owned()),
            ..Default::default()
        },
        models: vec![kl_protocol::ProviderModelInput {
            id: "deepseek-chat".to_owned(),
            name: None,
            protocol: "openai-chat".to_owned(),
            context_window: 128000,
            compact_threshold: 0.8,
            max_output_tokens: 8192,
            url: None,
            enabled: true,
            reasoning_effort: ReasoningEffort::Default,
            reasoning_efforts: Vec::new(),
        }],
        enabled: true,
        api_key_env: None,
    };

    provider_upsert(&config_path, &params).unwrap();

    let config = Config::load(&config_path).unwrap();
    assert_eq!(config.agent.provider, "deepseek");
    assert_eq!(config.agent.model, "deepseek-chat");
}

#[test]
fn redacted_provider_catalog_does_not_leak_inline_key() {
    let temp = tempfile::tempdir().unwrap();
    let global = r#"
[providers.test]
enabled = true
path = "test.toml"
"#;
    let provider = r#"
api_key = "super-secret-key"
[protocols]
openai-chat = "https://example.test/chat"
[[models]]
id = "chat"
protocol = "openai-chat"
context_window = 1000
compact_threshold = 0.5
max_output_tokens = 100
"#;
    let global_path = temp.path().join("config.toml");
    std::fs::write(&global_path, global).unwrap();
    std::fs::write(temp.path().join("test.toml"), provider).unwrap();
    let config = Config::load(&global_path).unwrap();
    let catalog = ProviderCatalog::load(&config, temp.path(), temp.path()).unwrap();
    let redacted = catalog.redacted().to_string();
    assert!(!redacted.contains("super-secret-key"));
    assert!(redacted.contains("configured"));
}

#[test]
fn provider_upsert_preserves_unknown_fields_and_writes_atomically() {
    let temp = tempfile::tempdir().unwrap();
    let config_path = temp.path().join("config.toml");
    let provider_dir = temp.path().join("providers");
    std::fs::create_dir_all(&provider_dir).unwrap();
    std::fs::write(
        &config_path,
        r#"
[custom]
keep = "yes"

[providers.opencode]
enabled = false
path = "providers/opencode.toml"
note = "keep"
"#,
    )
    .unwrap();
    std::fs::write(
        provider_dir.join("opencode.toml"),
        r#"
note = "keep"
api_key_env = "OPENCODE_API_KEY"

[protocols]
openai-chat = "https://old.example/chat"

[[models]]
id = "gpt-5-responses"
protocol = "openai-responses"
context_window = 1000
compact_threshold = 0.5
max_output_tokens = 100
note = "keep-model"
"#,
    )
    .unwrap();

    let result = provider_upsert(&config_path, &upsert_params("opencode", true)).unwrap();
    assert_eq!(result.models, 1);
    assert!(result.enabled);

    let config_text = std::fs::read_to_string(&config_path).unwrap();
    assert!(config_text.contains("keep = \"yes\""));
    assert!(config_text.contains("note = \"keep\""));
    assert!(config_text.contains("enabled = true"));

    let provider_text = std::fs::read_to_string(provider_dir.join("opencode.toml")).unwrap();
    assert!(provider_text.contains("note = \"keep\""));
    assert!(provider_text.contains("note = \"keep-model\""));
    assert!(provider_text.contains("openai-responses = \"https://gateway.example/responses\""));
    assert!(!provider_text.contains("api_key ="));

    provider_upsert(&config_path, &upsert_params("opencode", false)).unwrap();
    let config_text = std::fs::read_to_string(&config_path).unwrap();
    assert!(config_text.contains("enabled = false"));
    assert!(config_text.contains("keep = \"yes\""));
    let provider_text = std::fs::read_to_string(provider_dir.join("opencode.toml")).unwrap();
    assert!(provider_text.contains("note = \"keep-model\""));
    for directory in [temp.path(), provider_dir.as_path()] {
        for entry in std::fs::read_dir(directory).unwrap() {
            let name = entry.unwrap().file_name().to_string_lossy().into_owned();
            assert!(!name.contains(".tmp"), "temporary file left behind: {name}");
        }
    }
}

#[test]
fn provider_model_endpoints_cover_models_and_modul() {
    let temp = tempfile::tempdir().unwrap();
    let config_path = temp.path().join("config.toml");
    std::fs::create_dir_all(temp.path().join("providers")).unwrap();
    std::fs::create_dir_all(temp.path().join("credentials")).unwrap();
    std::fs::write(
        &config_path,
        r#"
[providers.deepseek]
enabled = false
path = "providers/deepseek.toml"
"#,
    )
    .unwrap();
    std::fs::write(
        temp.path().join("providers/deepseek.toml"),
        r#"
[protocols]
openai-chat = "https://api.deepseek.com/v1/chat/completions"
anthropic = "https://api.deepseek.com/v1/messages"
"#,
    )
    .unwrap();
    std::fs::write(temp.path().join("credentials/deepseek.key"), "sk-test").unwrap();

    let endpoints = provider_model_endpoints(&config_path, temp.path(), "deepseek").unwrap();

    assert_eq!(endpoints.len(), 2);
    assert_eq!(endpoints[0].protocol, Protocol::OpenAiChat);
    assert_eq!(
        endpoints[0].models_urls,
        vec![
            "https://api.deepseek.com/v1/models".to_owned(),
            "https://api.deepseek.com/v1/modul".to_owned()
        ]
    );
    assert_eq!(endpoints[0].api_key.as_deref(), Some("sk-test"));
    assert_eq!(endpoints[1].protocol, Protocol::Anthropic);
}

#[test]
fn provider_upsert_allows_a_provider_without_models() {
    let temp = tempfile::tempdir().unwrap();
    let config_path = temp.path().join("config.toml");
    let mut params = upsert_params("empty", true);
    params.models.clear();

    let result = provider_upsert(&config_path, &params).unwrap();
    assert_eq!(result.models, 0);

    let config = Config::load(&config_path).unwrap();
    let catalog = ProviderCatalog::load(&config, temp.path(), temp.path()).unwrap();
    assert_eq!(catalog.providers.len(), 1);
    assert!(catalog.providers[0].models.is_empty());
}

#[test]
fn api_key_is_written_outside_provider_file_and_redacted() {
    let temp = tempfile::tempdir().unwrap();
    let config_path = temp.path().join("config.toml");
    provider_upsert(&config_path, &upsert_params("opencode", true)).unwrap();
    provider_set_api_key(
        &config_path,
        temp.path(),
        &ProviderSetApiKeyParams {
            id: "opencode".to_owned(),
            key: "super-secret-key".to_owned(),
        },
    )
    .unwrap();

    let key_path = temp.path().join("credentials/opencode.key");
    assert_eq!(
        std::fs::read_to_string(&key_path).unwrap(),
        "super-secret-key"
    );
    let provider_text =
        std::fs::read_to_string(temp.path().join("providers/opencode.toml")).unwrap();
    assert!(!provider_text.contains("super-secret-key"));

    let config = Config::load(&config_path).unwrap();
    let catalog = ProviderCatalog::load(&config, temp.path(), temp.path()).unwrap();
    assert_eq!(
        catalog.providers[0].api_key.as_deref(),
        Some("super-secret-key")
    );
    let redacted = catalog.redacted().to_string();
    assert!(!redacted.contains("super-secret-key"));
    let redacted_value: Value = catalog.redacted();
    assert_eq!(redacted_value["providers"][0]["key_configured"], true);
}

#[test]
fn default_model_and_enabled_validation_use_saved_config() {
    let temp = tempfile::tempdir().unwrap();
    let config_path = temp.path().join("config.toml");
    provider_upsert(&config_path, &upsert_params("opencode", true)).unwrap();
    provider_set_api_key(
        &config_path,
        temp.path(),
        &ProviderSetApiKeyParams {
            id: "opencode".to_owned(),
            key: "test-key".to_owned(),
        },
    )
    .unwrap();

    let result = config_set_default_model(
        &config_path,
        temp.path(),
        &SetDefaultModelParams {
            provider: "opencode".to_owned(),
            model: "gpt-5-responses".to_owned(),
        },
    )
    .unwrap();
    assert_eq!(result.provider, "opencode");
    assert_eq!(result.model, "gpt-5-responses");
    let config = Config::load(&config_path).unwrap();
    assert_eq!(config.agent.provider, "opencode");
    assert_eq!(config.agent.model, "gpt-5-responses");

    provider_set_enabled(
        &config_path,
        &ProviderSetEnabledParams {
            id: "opencode".to_owned(),
            enabled: false,
        },
    )
    .unwrap();
    let error = config_set_default_model(
        &config_path,
        temp.path(),
        &SetDefaultModelParams {
            provider: "opencode".to_owned(),
            model: "gpt-5-responses".to_owned(),
        },
    )
    .unwrap_err();
    assert!(matches!(error, ConfigError::ProviderDisabled(_)));
    let config = Config::load(&config_path).unwrap();
    let catalog = ProviderCatalog::load(&config, temp.path(), temp.path()).unwrap();
    assert!(catalog.providers.is_empty());
    assert!(catalog.disabled.contains("opencode"));
}

#[test]
fn invalid_provider_ids_are_rejected() {
    let temp = tempfile::tempdir().unwrap();
    let config_path = temp.path().join("config.toml");
    let error = provider_upsert(&config_path, &upsert_params("../escape", true)).unwrap_err();
    assert!(matches!(error, ConfigError::InvalidProviderId(_)));
}

#[test]
fn api_key_env_overrides_credentials_file() {
    let temp = tempfile::tempdir().unwrap();
    let config_path = temp.path().join("config.toml");
    let env_name = format!("KL_TEST_PROVIDER_KEY_{}", std::process::id());
    unsafe {
        std::env::set_var(&env_name, "env-secret");
    }
    let mut params = upsert_params("local", true);
    params.api_key_env = Some(env_name.clone());
    provider_upsert(&config_path, &params).unwrap();
    provider_set_api_key(
        &config_path,
        temp.path(),
        &ProviderSetApiKeyParams {
            id: "local".to_owned(),
            key: "file-secret".to_owned(),
        },
    )
    .unwrap();
    let config = Config::load(&config_path).unwrap();
    let catalog = ProviderCatalog::load(&config, temp.path(), temp.path()).unwrap();
    assert_eq!(catalog.providers[0].api_key.as_deref(), Some("env-secret"));
    unsafe {
        std::env::remove_var(&env_name);
    }
}
#[test]
fn agent_presets_parse_and_map_tool_access() {
    assert_eq!(AgentPreset::parse("build"), Some(AgentPreset::Build));
    assert_eq!(AgentPreset::parse("PLAN"), Some(AgentPreset::Plan));
    assert_eq!(AgentPreset::parse("explore"), Some(AgentPreset::Explore));
    assert_eq!(AgentPreset::parse("review"), Some(AgentPreset::Review));
    assert_eq!(AgentPreset::parse("verify"), Some(AgentPreset::Verify));
    assert_eq!(AgentPreset::parse("deep"), Some(AgentPreset::Deep));
    assert_eq!(AgentPreset::parse("unknown"), None);
    assert_eq!(AgentPreset::Build.tool_access(), ToolAccess::Full);
    assert_eq!(AgentPreset::Plan.tool_access(), ToolAccess::ReadOnly);
    assert_eq!(AgentPreset::Explore.tool_access(), ToolAccess::ReadOnly);
    assert_eq!(AgentPreset::Review.tool_access(), ToolAccess::ReadOnly);
    assert_eq!(AgentPreset::Verify.tool_access(), ToolAccess::VerifyOnly);
    assert_eq!(AgentPreset::Deep.tool_access(), ToolAccess::Full);
    assert!(AgentPreset::Build.prompt_prefix().is_empty());
    assert!(AgentPreset::Deep.prompt_prefix().is_empty());
    assert!(!AgentPreset::Plan.prompt_prefix().is_empty());
    assert!(!AgentPreset::Verify.prompt_prefix().is_empty());
    for preset in [
        AgentPreset::Build,
        AgentPreset::Plan,
        AgentPreset::Explore,
        AgentPreset::Review,
        AgentPreset::Verify,
        AgentPreset::Deep,
    ] {
        assert_eq!(AgentPreset::parse(preset.as_str()), Some(preset));
    }
}

fn upsert_params(id: &str, enabled: bool) -> ProviderUpsertParams {
    ProviderUpsertParams {
        id: id.to_owned(),
        protocols: kl_protocol::ProviderProtocolUrls {
            openai_chat: Some("https://gateway.example/chat".to_owned()),
            anthropic: Some("https://gateway.example/messages".to_owned()),
            openai_responses: Some("https://gateway.example/responses".to_owned()),
        },
        models: vec![kl_protocol::ProviderModelInput {
            id: "gpt-5-responses".to_owned(),
            name: None,
            protocol: "openai-responses".to_owned(),
            context_window: 1000,
            compact_threshold: 0.5,
            max_output_tokens: 100,
            url: None,
            enabled: true,
            reasoning_effort: ReasoningEffort::Default,
            reasoning_efforts: Vec::new(),
        }],
        enabled,
        api_key_env: None,
    }
}
