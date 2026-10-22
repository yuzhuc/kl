use crate::{PolicyRequest, Tool, ToolContext, ToolError, ToolOutcome, string_arg};
use kl_policy::Action;
use serde_json::{Value, json};
use std::io::Read;
use std::net::{IpAddr, Ipv4Addr, Ipv6Addr, ToSocketAddrs};
use std::time::Duration;
use url::Url;

const MAX_BYTES: u64 = 1024 * 1024;
const MAX_REDIRECTS: usize = 5;
const TIMEOUT: Duration = Duration::from_secs(15);

pub struct WebFetchTool;

impl Tool for WebFetchTool {
    fn name(&self) -> &str {
        "web_fetch"
    }

    fn description(&self) -> &str {
        "Fetch a public http(s) URL after approval; private addresses and unlisted hosts are rejected."
    }

    fn parameters(&self) -> Value {
        json!({
            "type": "object",
            "properties": {
                "url": {
                    "type": "string",
                    "description": "http(s) URL to fetch"
                }
            },
            "required": ["url"],
            "additionalProperties": false
        })
    }

    fn validate(&self, args: &Value) -> Result<(), ToolError> {
        let url = Url::parse(string_arg(args, "url")?)
            .map_err(|error| ToolError::InvalidArgs(format!("invalid URL: {error}")))?;
        if !matches!(url.scheme(), "http" | "https") {
            return Err(ToolError::InvalidArgs(
                "web_fetch only supports http and https".to_owned(),
            ));
        }
        Ok(())
    }

    fn policy_request(&self, args: &Value) -> Result<PolicyRequest, ToolError> {
        let url = string_arg(args, "url")?;
        Ok(PolicyRequest {
            action: Action::Shell,
            resource: format!("web_fetch {url}"),
        })
    }

    fn always_ask(&self) -> bool {
        true
    }

    fn max_result_chars(&self) -> usize {
        MAX_BYTES as usize
    }

    fn execute(&self, context: &ToolContext<'_>, args: &Value) -> Result<ToolOutcome, ToolError> {
        let allowlist = &context.policy.config().network_allowlist;
        if allowlist.is_empty() {
            return Err(ToolError::Policy(
                "web_fetch requires a non-empty policy.network_allowlist".to_owned(),
            ));
        }
        let url = Url::parse(string_arg(args, "url")?)
            .map_err(|error| ToolError::InvalidArgs(format!("invalid URL: {error}")))?;
        let agent = ureq::AgentBuilder::new()
            .redirects(0)
            .timeout(TIMEOUT)
            .build();
        let (final_url, status, body, truncated) = fetch(&agent, url, allowlist)?;
        let summary = format!(
            "fetched {final_url} ({status}{})",
            if truncated { ", truncated" } else { "" }
        );
        Ok(ToolOutcome::ok(summary, body))
    }
}

fn fetch(
    agent: &ureq::Agent,
    mut url: Url,
    allowlist: &[String],
) -> Result<(Url, u16, String, bool), ToolError> {
    for _ in 0..=MAX_REDIRECTS {
        validate_url(&url, allowlist)?;
        let response = agent
            .get(url.as_str())
            .call()
            .map_err(|error| ToolError::Execution(format!("web_fetch failed: {error}")))?;
        let status = response.status();
        if (300..400).contains(&status) {
            let location = response
                .header("location")
                .ok_or_else(|| ToolError::Execution("redirect without location".to_owned()))?;
            url = url
                .join(location)
                .map_err(|error| ToolError::Execution(format!("invalid redirect: {error}")))?;
            continue;
        }
        if status >= 400 {
            return Err(ToolError::Execution(format!(
                "web_fetch returned HTTP {status}"
            )));
        }
        let mut reader = response.into_reader().take(MAX_BYTES + 1);
        let mut bytes = Vec::new();
        reader
            .read_to_end(&mut bytes)
            .map_err(|error| ToolError::Execution(format!("web_fetch read failed: {error}")))?;
        let truncated = bytes.len() as u64 > MAX_BYTES;
        bytes.truncate(MAX_BYTES as usize);
        let body = String::from_utf8_lossy(&bytes).into_owned();
        return Ok((url, status, body, truncated));
    }
    Err(ToolError::Execution("too many redirects".to_owned()))
}

fn validate_url(url: &Url, allowlist: &[String]) -> Result<(), ToolError> {
    if !matches!(url.scheme(), "http" | "https") {
        return Err(ToolError::Policy("only http/https is allowed".to_owned()));
    }
    if !url.username().is_empty() || url.password().is_some() {
        return Err(ToolError::Policy("URL userinfo is not allowed".to_owned()));
    }
    if let Some(port) = url.port()
        && port != 80
        && port != 443
    {
        return Err(ToolError::Policy(
            "only ports 80 and 443 are allowed".to_owned(),
        ));
    }
    let host = url
        .host_str()
        .ok_or_else(|| ToolError::Policy("URL has no host".to_owned()))?;
    let lower = host.to_ascii_lowercase();
    if lower == "localhost" || lower.ends_with(".localhost") || !host_allowed(allowlist, &lower) {
        return Err(ToolError::Policy(format!(
            "host is not allowlisted: {lower}"
        )));
    }
    let addresses = (lower.as_str(), url.port_or_known_default().unwrap_or(443))
        .to_socket_addrs()
        .map_err(|error| ToolError::Policy(format!("cannot resolve host {lower}: {error}")))?;
    let mut resolved = false;
    for address in addresses {
        resolved = true;
        if blocked_ip(address.ip()) {
            return Err(ToolError::Policy(format!(
                "private or special address rejected: {}",
                address.ip()
            )));
        }
    }
    if !resolved {
        return Err(ToolError::Policy(format!(
            "host resolved to no addresses: {lower}"
        )));
    }
    Ok(())
}

fn host_allowed(allowed: &[String], host: &str) -> bool {
    allowed.iter().any(|entry| {
        let entry = entry.trim().to_ascii_lowercase();
        if let Some(suffix) = entry.strip_prefix("*.") {
            host == suffix || host.ends_with(&format!(".{suffix}"))
        } else if let Some(suffix) = entry.strip_prefix('.') {
            host == suffix || host.ends_with(&format!(".{suffix}"))
        } else {
            host == entry
        }
    })
}

fn blocked_ip(ip: IpAddr) -> bool {
    match ip {
        IpAddr::V4(ip) => blocked_v4(ip),
        IpAddr::V6(ip) => blocked_v6(ip),
    }
}

fn blocked_v4(ip: Ipv4Addr) -> bool {
    let value = u32::from(ip);
    ip.is_private()
        || ip.is_loopback()
        || ip.is_link_local()
        || ip.is_unspecified()
        || ip.is_multicast()
        || ip.is_broadcast()
        || (value & 0xffff_ff00) == 0x6440_0000 // 100.64.0.0/10
}

fn blocked_v6(ip: Ipv6Addr) -> bool {
    ip.is_loopback()
        || ip.is_unspecified()
        || ip.is_unique_local()
        || ip.is_unicast_link_local()
        || ip.is_multicast()
        || ip.to_ipv4_mapped().is_some_and(blocked_v4)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn blocked(url: &str) -> bool {
        let Ok(url) = Url::parse(url) else {
            return true;
        };
        validate_url(&url, &["example.com".to_owned()]).is_err()
    }

    #[test]
    fn rejects_ten_ssrf_shapes() {
        for url in [
            "http://127.0.0.1/",
            "http://localhost/",
            "http://169.254.169.254/",
            "http://10.0.0.1/",
            "http://192.168.1.1/",
            "http://[::1]/",
            "http://[fc00::1]/",
            "http://[fe80::1]/",
            "http://user:pass@example.com/",
            "https://example.com:8443/",
        ] {
            assert!(blocked(url), "should reject {url}");
        }
    }

    #[test]
    fn requires_an_allowlisted_host() {
        let url = Url::parse("https://example.com/").unwrap();
        assert!(validate_url(&url, &["example.com".to_owned()]).is_ok());
        assert!(validate_url(&url, &[]).is_err());
    }
}
