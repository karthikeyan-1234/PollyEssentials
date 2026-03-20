# Polly Resilience Patterns in .NET 9 Web API

A practical example demonstrating advanced Polly resilience policies in an ASP.NET Core 9 endpoint. This code showcases:

- **Retry** with fixed delay (handles HttpRequestException and TimeoutRejectedException)
- **Circuit Breaker** (opens after 5 consecutive failures for 30 seconds)
- **Timeout** per HTTP call (3 seconds, with cancellation token support)
- **Fallback** returning a user-friendly message when all else fails

Policies are composed in the correct order: Fallback → Circuit Breaker → Retry → Timeout → HTTP call. The example uses the public [CATAAS API](https://cataas.com/) (cats with cute tag) to test resilience in real time.

Perfect for learning Polly, understanding policy composition, and building resilient HTTP clients in .NET.
