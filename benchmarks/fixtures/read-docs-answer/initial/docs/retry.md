# Retry Policy

Requests that fail with a retryable error are retried up to 5 times.

The delay between attempts uses exponential backoff with a base delay of 200 ms,
doubling on every attempt.
