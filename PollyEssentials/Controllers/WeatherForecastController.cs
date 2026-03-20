using Microsoft.AspNetCore.Mvc;

using Polly;
using Polly.Timeout;

using System.Net.Http;

/*

Polly acts across requests and not just within the same requests. 
So the circuit breaker, effectively count 5 different API requests and if all the 5 fails, then all the further requests, for the next 30 seconds will not be entertained. But when an API request lands, and it tries to make an http call, and that fails, it will wait for 2 seconds and retry. 
This happens 3 times, all in the same request. If all the 4 [original + 3] fail, then the circuit breaker counts it as 1 failure [1 Request failed].


Time ──────────────────────────────────────────────────────────────→

Request 1 ──► Retry(4 attempts) ──► all fail ──► Circuit Breaker: failure #1 ──► Fallback → message
Request 2 ──► Retry(4 attempts) ──► all fail ──► Circuit Breaker: failure #2 ──► Fallback → message
Request 3 ──► Retry(4 attempts) ──► all fail ──► Circuit Breaker: failure #3 ──► Fallback → message
Request 4 ──► Retry(4 attempts) ──► all fail ──► Circuit Breaker: failure #4 ──► Fallback → message
Request 5 ──► Retry(4 attempts) ──► all fail ──► Circuit Breaker: failure #5 ──► ⚠️ CIRCUIT OPENS! ⚠️
                                                                                      │
                                                                                      │ 30 seconds
                                                                                      ▼
Request 6 ──► Circuit Breaker OPEN ──► throws BrokenCircuitException ──► Fallback → message (no HTTP call!)
Request 7 ──► Circuit Breaker OPEN ──► throws BrokenCircuitException ──► Fallback → message
Request 8 ──► Circuit Breaker OPEN ──► throws BrokenCircuitException ──► Fallback → message

After 30 seconds ──► Circuit HALF-OPEN

Request 9 ──► Circuit HALF-OPEN ──► allows ONE trial HTTP call (with retries)
    ├─► If SUCCEEDS ──► Circuit CLOSES (back to normal)
    └─► If FAILS ──► Circuit OPENS again for another 30 seconds
 */


namespace PollyEssentials.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class WeatherForecastController : ControllerBase
    {
        IHttpClientFactory _httpClientFactory;

        private static readonly string[] Summaries = new[]
        {
            "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
        };

        private readonly ILogger<WeatherForecastController> _logger;


        /*✅ What This Code Does (Step‑by‑Step Execution)
                Circuit Breaker checks its state.

                If open: throws BrokenCircuitException immediately → goes to fallback (outer policy) → returns fallback message.

                If closed or half‑open: proceeds to fallback.

                Fallback enters a try block around the inner delegate (retry + HTTP call).

                If inner delegate succeeds → returns actual JSON.

                If inner delegate throws HttpRequestException (after retries) → fallback catches it, logs, and returns the fallback string.

                Retry (inside fallback) attempts the HTTP call up to 3 times with 2‑second delays.

                If a transient failure occurs, it retries.

                If all retries fail, it re‑throws the final HttpRequestException to the fallback.

                HTTP Call is the innermost delegate.
        */


        public WeatherForecastController(ILogger<WeatherForecastController> logger, IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            
        }
        [HttpGet(Name = "GetWeatherForecast")]
        public async Task<IActionResult> GetWeatherForecast()
        {
            // Create an HttpClient instance from the factory for the named client "PollyClient"
            // This client should have been configured with base address etc. in Program.cs
            var httpClient = _httpClientFactory.CreateClient("PollyClient");

            // --- Section 1: Define a retry policy [For external http resource, per request] ---

            var retry = Policy
                        .Handle<HttpRequestException>() // External api isn't reachable. Handles HttpRequestException (network errors, HTTP 5xx, etc.)
                        .Or<TimeoutRejectedException>() //This is required to handle the timeoutPerCall that comes in Section 3
                        .WaitAndRetryAsync(3, i => TimeSpan.FromSeconds(2)); //Wait for 2 seconds, before trying. Try 3 times

            // --- Section 2: Define a circuit breaker policy ---

            var circuitBreaker = Policy
                        .Handle<HttpRequestException>() // Handle the HttpRequestException propagated from retry
                        .Or<TimeoutRejectedException>() //This is required to handle the timeoutPerCall that comes in Section 3, propagated from retry
                        .CircuitBreakerAsync(5, TimeSpan.FromSeconds(30)); // After 5 consecutive failures, the circuit will break (open) for 30 seconds
            // During that time, further attempts will fail immediately without calling the delegate

            // --- Section 3: Define time out for a http call
            var timeoutPerCall = Policy.TimeoutAsync(3); // each HTTP call must complete in 3 seconds

            // --- Section 4: Define a fallback policy ---
            // This policy returns a string value when the inner operation throws HttpRequestException
            // The fallback value is a user-friendly message
            // onFallbackAsync logs the original exception (in a real app you'd log to a service)
            var fallback = Policy<string>
                            .Handle<HttpRequestException>()
                            .Or<TimeoutRejectedException>()   // also handle timeout!
                            .FallbackAsync(
                    fallbackValue: "Service unavailable. Showing cached/default data.",
                    onFallbackAsync: async (ex, ctx) =>
                    {
                        Console.WriteLine($"Fallback triggered: {ex.Exception.Message}");
                    });



            // --- Section 5: Prepare the policy ---
            // --- Compose policies ---
            // First, wrap fallback around retry: fallback is outer, retry is inner
            // Then wrap that composite around circuitBreaker: circuitBreaker becomes the outermost
            // Execution order when calling ExecuteAsync:
            // 1. circuitBreaker (checks if circuit is open; if open, throws BrokenCircuitException)
            // 2. fallback (if inner throws, returns fallback value)
            // 3. retry (retries up to 3 times on HttpRequestException)
            // So just before the innermost, is the actual HTTP call


            // Fallback wraps circuitBreaker wraps retry wraps timeout
            var policy = fallback.WrapAsync(circuitBreaker.WrapAsync(retry.WrapAsync(timeoutPerCall)));


            // --- Section 6: Execute the call --- 
            // Execute the HTTP call inside the final policyWrap
            // The delegate returns a string (the JSON from the API)
            var result = await policy.ExecuteAsync(
                async (ct) =>   //this Cancellation token is a must here for the timeoutPerCall policy to cancel the httpRequest.
                await httpClient.GetStringAsync("https://cataas.com/api/cats?tags=cute",ct), //Without the Cancellation token ct, the timoutPerCall policy cannot "Cancel" the httpRequest
                HttpContext.RequestAborted);    

            // Return the result (either the actual JSON or the fallback message)
            return Ok(result);
        }
    }
}
