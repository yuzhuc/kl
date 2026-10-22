//! Built-in model metadata. Prices are USD per one million tokens.
//! OpenAI GPT-5 values checked against official API pricing on 2026-10-21:
//! https://platform.openai.com/docs/pricing and https://platform.openai.com/docs/models/gpt-5

use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Copy, PartialEq, Serialize, Deserialize)]
pub struct ModelCatalogEntry {
    pub context_window: u32,
    pub max_output_tokens: u32,
    pub input_price_per_million: f64,
    pub cached_input_price_per_million: Option<f64>,
    pub output_price_per_million: f64,
    pub supports_tools: bool,
    pub supports_cache: bool,
    pub supports_vision: bool,
}

const GPT_5: ModelCatalogEntry = ModelCatalogEntry {
    context_window: 400_000,
    max_output_tokens: 128_000,
    input_price_per_million: 1.25,
    cached_input_price_per_million: Some(0.125),
    output_price_per_million: 10.0,
    supports_tools: true,
    supports_cache: true,
    supports_vision: true,
};

/// Look up built-in metadata. Unknown models and unpriced providers return
/// None so callers can omit cost instead of inventing it.
pub fn lookup(provider: &str, model: &str) -> Option<ModelCatalogEntry> {
    let provider = provider.to_ascii_lowercase();
    let model = model.to_ascii_lowercase();
    let base = model
        .strip_suffix("-responses")
        .or_else(|| model.strip_suffix("-chat"))
        .unwrap_or(&model);

    if provider.contains("anthropic") {
        return None;
    }

    match base {
        // Project examples use -chat and -responses as routing aliases.
        "gpt-5" => Some(GPT_5),
        _ => None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn known_model_and_aliases_resolve() {
        assert_eq!(lookup("openai", "gpt-5"), Some(GPT_5));
        assert_eq!(lookup("opencode", "gpt-5-chat"), Some(GPT_5));
        assert_eq!(lookup("gateway", "gpt-5-responses"), Some(GPT_5));
    }

    #[test]
    fn unknown_and_anthropic_models_return_none() {
        assert_eq!(lookup("openai", "gpt-unknown"), None);
        assert_eq!(lookup("anthropic", "gpt-5"), None);
    }

    #[test]
    fn price_is_not_fabricated_for_unknown_models() {
        assert!(lookup("custom", "private-model").is_none());
    }
}
