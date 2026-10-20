// Tests for script.csx. Run with `dotnet run --project tests`.
//   default  offline tests against a fake backend and a recorded job (no credits)
//   live     one real "Get offers" run against production, polling like Power Automate
//            does; needs PRICEPIRATE_API_KEY and costs 1 credit
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;

public static class Program
{
    private static int failures;

    public static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "live") return await Live();

        await CreatesGtinJobAndPointsPollingAtPollJob();
        await SendsAmazonOptionsAndProductIdLookups();
        await RejectsProductIdOnEanOnlyMarketplaces();
        await StopsPlatformRetriesWhenCreationMayHaveCharged();
        await StartPriceJobReturnsAtOnce();
        await PendingPollStaysAsync();
        await FlattensRecordedJobIntoOneItemPerOffer();
        await KeepsQueriesWithoutOffers();
        await ProductDetailsHaveOneItemPerQuery();
        await FailedJobRaisesAfterSettlingPoll();
        await GetJobResultsWithoutWaitingReturnsPending();
        await ExpiredJobExplainsRetention();
        await CountriesDependOnMarketplace();
        await LargeJobFitsTheScriptTimeLimit();

        Console.WriteLine(failures == 0 ? "All tests passed." : failures + " check(s) failed.");
        return failures == 0 ? 0 : 1;
    }

    // ---- Job creation --------------------------------------------------------------------

    private static async Task CreatesGtinJobAndPointsPollingAtPollJob()
    {
        var backend = new FakeBackend(req => Accepted("job-1", "/api/v2/jobs/job-1"));
        var response = await Run("GetOffers", HttpMethod.Post,
            "https://connector/api/v2/jobs/offers?marketplace=idealo&country=de&search_by=ean",
            "{\"queries\":[\"4013474101469\",\" \",\"4006381333931\"]}", backend);

        var sent = backend.Requests.Single();
        Check("create: POST /api/v2/jobs", sent.Method == "POST" && sent.Url == "https://api-v2.pricepirate.com/api/v2/jobs");
        Check("create: forwards the API key", sent.Headers["x-api-key"] == "test-key");
        Check("create: sends the integration header", sent.Headers["x-pricepirate-integration"] == "power-automate");
        var body = JObject.Parse(sent.Body);
        Check("create: gtin operation", (string)body["operation"] == "search-by-gtin");
        Check("create: blank queries dropped", body["queries"].Count() == 2);
        Check("create: no options outside Amazon", body["options"] == null);
        Check("create: answers 202", response.StatusCode == HttpStatusCode.Accepted);
        Check("create: absolute Location with view", response.Headers.Location.ToString() == "https://api-v2.pricepirate.com/api/v2/jobs/job-1?view=offers&skip_empty=false");
        Check("create: keeps Retry-After", response.Headers.RetryAfter.Delta == TimeSpan.FromSeconds(5));
    }

    private static async Task SendsAmazonOptionsAndProductIdLookups()
    {
        var backend = new FakeBackend(req => Accepted("job-2", "/api/v2/jobs/job-2"));
        var response = await Run("GetProductDetails", HttpMethod.Post,
            "https://connector/api/v2/jobs/details?marketplace=amazon&country=uk&search_by=product_id&include_details=false&zipcode=SW1A%201AA&skip_empty=true",
            "{\"queries\":[\"B000000001\"]}", backend);
        var body = JObject.Parse(backend.Requests.Single().Body);
        Check("amazon: id operation", (string)body["operation"] == "search-by-id");
        Check("amazon: includeDetail false", (bool)body["options"]["includeDetail"] == false);
        Check("amazon: zipcode", (string)body["options"]["zipcode"] == "SW1A 1AA");
        Check("amazon: details view in Location", response.Headers.Location.ToString().EndsWith("?view=details&skip_empty=true"));
    }

    private static async Task RejectsProductIdOnEanOnlyMarketplaces()
    {
        var backend = new FakeBackend(req => throw new Exception("must not call the API"));
        var response = await Run("GetOffers", HttpMethod.Post,
            "https://connector/api/v2/jobs/offers?marketplace=klarna&country=se&search_by=product_id", "{\"queries\":[\"1\"]}", backend);
        Check("klarna by id: 400 without a backend call", response.StatusCode == HttpStatusCode.BadRequest && backend.Requests.Count == 0);

        response = await Run("GetOffers", HttpMethod.Post,
            "https://connector/api/v2/jobs/offers?marketplace=bol&country=de", "{\"queries\":[\"1\"]}", backend);
        Check("bol in de: 400 without a backend call", response.StatusCode == HttpStatusCode.BadRequest && backend.Requests.Count == 0);
    }

    private static async Task StopsPlatformRetriesWhenCreationMayHaveCharged()
    {
        foreach (var status in new[] { 500, 502, 504 })
        {
            var backend = new FakeBackend(req => Status(status, "{\"error\":true}"));
            var response = await Run("GetOffers", HttpMethod.Post,
                "https://connector/api/v2/jobs/offers?marketplace=idealo&country=de", "{\"queries\":[\"1\"]}", backend);
            Check("create " + status + ": non-retryable 424", (int)response.StatusCode == 424);
        }
        foreach (var status in new[] { 402, 429, 503 })
        {
            var backend = new FakeBackend(req => Status(status, "{\"error\":true}"));
            var response = await Run("GetOffers", HttpMethod.Post,
                "https://connector/api/v2/jobs/offers?marketplace=idealo&country=de", "{\"queries\":[\"1\"]}", backend);
            Check("create " + status + ": passed through", (int)response.StatusCode == status);
        }
    }

    private static async Task StartPriceJobReturnsAtOnce()
    {
        var backend = new FakeBackend(req =>
        {
            var r = Accepted("job-3", "/api/v2/jobs/job-3");
            r.Headers.TryAddWithoutValidation("X-Credits-Remaining", "98");
            return r;
        });
        var response = await Run("StartPriceJob", HttpMethod.Post,
            "https://connector/api/v2/jobs/start?marketplace=google-shopping&country=ch", "{\"queries\":[\"1\"]}", backend);
        var body = await Body(response);
        Check("start: 200 without Location", response.StatusCode == HttpStatusCode.OK && response.Headers.Location == null);
        Check("start: job id and credits", (string)body["job_id"] == "job-3" && (long)body["credits_remaining"] == 98);
    }

    // ---- Polling and output --------------------------------------------------------------

    private static async Task PendingPollStaysAsync()
    {
        var backend = new FakeBackend(req => Accepted("job-4", "/api/v2/jobs/job-4", "{\"error\":false,\"job_id\":\"job-4\",\"status\":\"pending\",\"results\":[]}"));
        var response = await Run("PollJob", HttpMethod.Get, "https://connector/api/v2/jobs/job-4?view=details&skip_empty=false", null, backend);
        Check("poll: omit_partials while pending", backend.Requests.Single().Url == "https://api-v2.pricepirate.com/api/v2/jobs/job-4?omit_partials=true");
        Check("poll pending: 202 with the same view", response.StatusCode == HttpStatusCode.Accepted && response.Headers.Location.ToString().Contains("view=details"));
    }

    private static async Task FlattensRecordedJobIntoOneItemPerOffer()
    {
        var job = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "idealo-completed.json"));
        var offerCount = JObject.Parse(job)["results"][0]["result"]["offers"].Count();
        var backend = new FakeBackend(req => Status(200, job));
        var response = await Run("PollJob", HttpMethod.Get, "https://connector/api/v2/jobs/x?view=offers&skip_empty=false", null, backend);
        var body = await Body(response);
        var offers = (JArray)body["offers"];
        var first = (JObject)offers[0];
        Check("offers: 200", response.StatusCode == HttpStatusCode.OK);
        Check("offers: one item per offer", offers.Count == offerCount && offerCount > 1);
        Check("offers: query context", (string)first["query"] == "4013474101469" && (string)first["source"] == "idealo" && (string)first["job_id"] == (string)body["job_id"]);
        Check("offers: product context", (string)first["product_id"] == "200377262" && first["price_min"].Type == JTokenType.Float);
        Check("offers: position as string", first["position"].Type == JTokenType.String);
        Check("offers: total as number", first["total"].Type == JTokenType.Float);
        Check("offers: rating as number", first["shop_review_rating"].Type == JTokenType.Float || first["shop_review_rating"].Type == JTokenType.Null);
        Check("offers: no products/raw", body["products"] == null && body["job"] == null);
        Check("offers: credits", (int)body["credits"]["billed"] == 1);
    }

    private static async Task KeepsQueriesWithoutOffers()
    {
        var job = new JObject
        {
            ["job_id"] = "job-5", ["status"] = "completed", ["source"] = "amazon", ["country"] = "de",
            ["results"] = new JArray
            {
                new JObject { ["query"] = "1", ["status"] = "not_found", ["result"] = null },
                new JObject { ["query"] = "2", ["status"] = "error", ["result"] = null, ["error"] = "Timed out", ["errorCode"] = "timeout" },
                new JObject { ["query"] = "3", ["status"] = "found", ["result"] = new JObject { ["id"] = "B1", ["offers"] = new JArray(new JObject { ["price"] = "12.5", ["position"] = 1 }) } },
            },
        };
        var backend = new FakeBackend(req => Status(200, job.ToString()));
        var all = (JArray)(await Body(await Run("PollJob", HttpMethod.Get, "https://connector/api/v2/jobs/job-5?view=offers&skip_empty=false", null, backend)))["offers"];
        Check("empty: one row per query without offers", all.Count == 3);
        Check("empty: error kept with code", (string)all[1]["errorCode"] == "timeout" && all[1]["price"].Type == JTokenType.Null);
        Check("empty: string price coerced", (double)all[2]["price"] == 12.5 && (string)all[2]["position"] == "1");

        var kept = (JArray)(await Body(await Run("PollJob", HttpMethod.Get, "https://connector/api/v2/jobs/job-5?view=offers&skip_empty=true", null, backend)))["offers"];
        Check("empty: skip toggle", kept.Count == 1 && (string)kept[0]["query"] == "3");
    }

    private static async Task ProductDetailsHaveOneItemPerQuery()
    {
        var job = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "idealo-completed.json"));
        var backend = new FakeBackend(req => Status(200, job));
        var body = await Body(await Run("PollJob", HttpMethod.Get, "https://connector/api/v2/jobs/x?view=details", null, backend));
        var products = (JArray)body["products"];
        Check("details: one item per query", products.Count == 1);
        Check("details: no offers array", products[0]["offers"] == null && (int)products[0]["offers_count"] > 0);
        Check("details: arrays", products[0]["image_urls"].Type == JTokenType.Array && products[0]["categories"].Type == JTokenType.Array);

        var raw = await Body(await Run("PollJob", HttpMethod.Get, "https://connector/api/v2/jobs/x?view=raw", null, backend));
        Check("raw: untouched job", raw["job"]["results"][0]["result"]["offers"] != null);
    }

    private static async Task FailedJobRaisesAfterSettlingPoll()
    {
        var backend = new FakeBackend(req => Status(200, "{\"job_id\":\"job-6\",\"status\":\"failed\",\"results\":[]}"));
        var response = await Run("PollJob", HttpMethod.Get, "https://connector/api/v2/jobs/job-6?view=offers", null, backend);
        Check("failed: polled once, then error", backend.Requests.Count == 1 && response.StatusCode == HttpStatusCode.BadRequest);
    }

    private static async Task GetJobResultsWithoutWaitingReturnsPending()
    {
        var backend = new FakeBackend(req => Accepted("job-7", "/api/v2/jobs/job-7", "{\"job_id\":\"job-7\",\"status\":\"pending\",\"done\":3,\"total\":10,\"results\":[]}"));
        var response = await Run("GetJobResults", HttpMethod.Get, "https://connector/api/v2/jobs/job-7/results?output=offers&wait=false", null, backend);
        var body = await Body(response);
        Check("results no wait: 200 pending", response.StatusCode == HttpStatusCode.OK && (string)body["status"] == "pending" && (int)body["done"] == 3);
        Check("results: reads the job, not /results", backend.Requests.Single().Url.StartsWith("https://api-v2.pricepirate.com/api/v2/jobs/job-7?"));

        response = await Run("GetJobResults", HttpMethod.Get, "https://connector/api/v2/jobs/job-7/results?output=details", null, backend);
        Check("results wait: 202 to PollJob", response.StatusCode == HttpStatusCode.Accepted && response.Headers.Location.ToString().Contains("/api/v2/jobs/job-7?view=details"));
    }

    private static async Task ExpiredJobExplainsRetention()
    {
        var backend = new FakeBackend(req => Status(404, "{\"error\":true,\"message\":\"Job not found\"}"));
        var body = await Body(await Run("GetJobResults", HttpMethod.Get, "https://connector/api/v2/jobs/old/results", null, backend));
        Check("404: mentions the 1 hour retention", ((string)body["message"]).Contains("1 hour"));
    }

    private static async Task CountriesDependOnMarketplace()
    {
        var backend = new FakeBackend(req => throw new Exception("must not call the API"));
        var bol = await Body(await Run("ListCountries", HttpMethod.Get, "https://connector/pricepirate/countries?marketplace=bol", null, backend));
        Check("countries: bol is nl and be", string.Join(",", bol["countries"].Select(c => (string)c["code"])) == "nl,be");
        var none = await Body(await Run("ListCountries", HttpMethod.Get, "https://connector/pricepirate/countries", null, backend));
        Check("countries: empty without marketplace", !none["countries"].Any());
    }

    // A 1,000-query job with 25 offers per query is far beyond typical sizes (the recorded
    // job has 13 offers for one EAN). Custom code must finish within 2 minutes.
    private static async Task LargeJobFitsTheScriptTimeLimit()
    {
        var recorded = JObject.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "idealo-completed.json")));
        var template = (JObject)recorded["results"][0];
        var offer = template["result"]["offers"][0];
        var results = new JArray();
        for (var i = 0; i < 1000; i++)
        {
            var entry = (JObject)template.DeepClone();
            entry["query"] = (4013474100000L + i).ToString();
            entry["result"]["offers"] = new JArray(Enumerable.Range(0, 25).Select(_ => offer.DeepClone()));
            results.Add(entry);
        }
        recorded["results"] = results;
        var payload = recorded.ToString();
        var backend = new FakeBackend(req => Status(200, payload));
        var watch = Stopwatch.StartNew();
        var body = await Body(await Run("PollJob", HttpMethod.Get, "https://connector/api/v2/jobs/x?view=offers", null, backend));
        watch.Stop();
        Console.WriteLine("  large job: " + (payload.Length / 1024 / 1024.0).ToString("0.0") + " MB in, " + body["offers"].Count() + " offers out, " + watch.ElapsedMilliseconds + " ms");
        Check("large job: 25,000 offers", body["offers"].Count() == 25000);
        Check("large job: well under 2 minutes", watch.Elapsed < TimeSpan.FromSeconds(20));
    }

    // ---- Live run ------------------------------------------------------------------------

    private static async Task<int> Live()
    {
        var key = Environment.GetEnvironmentVariable("PRICEPIRATE_API_KEY");
        if (string.IsNullOrEmpty(key)) { Console.WriteLine("Set PRICEPIRATE_API_KEY."); return 2; }
        var backend = new LiveBackend();
        var response = await Run("GetOffers", HttpMethod.Post,
            "https://connector/api/v2/jobs/offers?marketplace=idealo&country=de&search_by=ean",
            "{\"queries\":[\"4013474101469\"]}", backend, key);
        var polls = 0;
        while (response.StatusCode == HttpStatusCode.Accepted && polls < 200)
        {
            await Task.Delay(response.Headers.RetryAfter.Delta.Value);
            polls++;
            response = await Run("PollJob", HttpMethod.Get, response.Headers.Location.ToString(), null, backend, key);
            Console.WriteLine("  poll " + polls + ": " + (int)response.StatusCode);
        }
        var body = await Body(response);
        Console.WriteLine("  " + (int)response.StatusCode + " status=" + body["status"] + " offers=" + body["offers"]?.Count() + " credits=" + body["credits"]);
        return response.StatusCode == HttpStatusCode.OK && body["offers"].Any() ? 0 : 1;
    }

    // ---- Plumbing ------------------------------------------------------------------------

    private static async Task<HttpResponseMessage> Run(string operationId, HttpMethod method, string url, string body, IBackend backend, string key = "test-key")
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("x-api-key", key);
        if (body != null) request.Content = ScriptBase.CreateJsonContent(body);
        var script = new Script { Context = new FakeContext(operationId, request, backend), CancellationToken = CancellationToken.None };
        return await script.ExecuteAsync();
    }

    private static HttpResponseMessage Accepted(string jobId, string location, string body = null)
    {
        var response = Status(202, body ?? "{\"error\":false,\"job_id\":\"" + jobId + "\"}");
        response.Headers.TryAddWithoutValidation("Location", location);
        response.Headers.TryAddWithoutValidation("Retry-After", "5");
        return response;
    }

    private static HttpResponseMessage Status(int status, string body)
    {
        return new HttpResponseMessage((HttpStatusCode)status) { Content = ScriptBase.CreateJsonContent(body) };
    }

    private static async Task<JObject> Body(HttpResponseMessage response)
    {
        return JObject.Parse(await response.Content.ReadAsStringAsync());
    }

    private static void Check(string name, bool ok)
    {
        if (ok) return;
        failures++;
        Console.WriteLine("FAIL " + name);
    }

    public class SentRequest
    {
        public string Method;
        public string Url;
        public Dictionary<string, string> Headers;
        public string Body;
    }

    public interface IBackend
    {
        Task<HttpResponseMessage> SendAsync(HttpRequestMessage request);
    }

    private class FakeBackend : IBackend
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> respond;
        public readonly List<SentRequest> Requests = new List<SentRequest>();

        public FakeBackend(Func<HttpRequestMessage, HttpResponseMessage> respond) { this.respond = respond; }

        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
        {
            Requests.Add(new SentRequest
            {
                Method = request.Method.Method,
                Url = request.RequestUri.ToString(),
                Headers = request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value)),
                Body = request.Content == null ? null : await request.Content.ReadAsStringAsync(),
            });
            return respond(request);
        }
    }

    private class LiveBackend : IBackend
    {
        private static readonly HttpClient Client = new HttpClient();
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request) { return Client.SendAsync(request); }
    }

    private class FakeContext : IScriptContext
    {
        private readonly IBackend backend;

        public FakeContext(string operationId, HttpRequestMessage request, IBackend backend)
        {
            OperationId = operationId;
            Request = request;
            this.backend = backend;
        }

        public string CorrelationId { get { return "test"; } }
        public string OperationId { get; private set; }
        public HttpRequestMessage Request { get; private set; }
        public ILogger Logger { get { return NullLogger.Instance; } }

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return backend.SendAsync(request);
        }
    }
}
