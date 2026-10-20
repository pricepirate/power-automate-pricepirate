using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using System.Web;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// PricePirate Price Data connector.
//
// The Price API runs lookups as asynchronous jobs: POST /api/v2/jobs answers 202 with a
// Location to poll, and GET /api/v2/jobs/{id} answers 202 while the job is pending and
// 200 once it is completed or failed. This script
//   - turns the connector's actions into that job API (one backend path, several actions),
//   - rewrites Location to an absolute URL that carries the output view, so the platform's
//     asynchronous polling comes back to the PollJob operation,
//   - flattens terminal results into one item per offer or one item per query,
//   - and never lets the platform retry a job creation that may already have been charged.
public class Script : ScriptBase
{
    private const string ApiBase = "https://api-v2.pricepirate.com";
    private const string IntegrationHeader = "x-pricepirate-integration";
    private const string IntegrationName = "power-automate";
    private const int MaxQueries = 1000;

    private static readonly Dictionary<string, string[]> Countries = new Dictionary<string, string[]>
    {
        ["idealo"] = new[] { "de", "at", "es", "fr", "it", "uk" },
        ["amazon"] = new[] { "de", "at", "ae", "au", "be", "br", "ca", "eg", "es", "fr", "ie", "in", "it", "jp", "mx", "nl", "pl", "sa", "se", "sg", "tr", "uk", "us", "za" },
        ["google-shopping"] = new[] { "de", "at", "au", "ca", "ch", "es", "fr", "in", "it", "jp", "nl", "pl", "ro", "uk", "us" },
        ["bol"] = new[] { "nl", "be" },
        ["klarna"] = new[] { "de", "at", "dk", "es", "fi", "fr", "ie", "it", "nl", "no", "se", "uk", "us" },
        ["pricerunner"] = new[] { "uk", "se", "dk" },
    };

    private static readonly Dictionary<string, string> CountryNames = new Dictionary<string, string>
    {
        ["ae"] = "United Arab Emirates", ["at"] = "Austria", ["au"] = "Australia", ["be"] = "Belgium",
        ["br"] = "Brazil", ["ca"] = "Canada", ["ch"] = "Switzerland", ["de"] = "Germany",
        ["dk"] = "Denmark", ["eg"] = "Egypt", ["es"] = "Spain", ["fi"] = "Finland",
        ["fr"] = "France", ["ie"] = "Ireland", ["in"] = "India", ["it"] = "Italy",
        ["jp"] = "Japan", ["mx"] = "Mexico", ["nl"] = "Netherlands", ["no"] = "Norway",
        ["pl"] = "Poland", ["ro"] = "Romania", ["sa"] = "Saudi Arabia", ["se"] = "Sweden",
        ["sg"] = "Singapore", ["tr"] = "Turkey", ["uk"] = "United Kingdom", ["us"] = "United States",
        ["za"] = "South Africa",
    };

    // Marketplaces whose job API only supports lookups by EAN/GTIN.
    private static readonly HashSet<string> EanOnly = new HashSet<string> { "klarna", "pricerunner" };

    private static readonly string[] OfferFields =
    {
        "shop_name", "shop_url", "price", "shipping", "total", "currency", "availability_code",
        "availability_text", "condition", "position", "marketplace_name", "shop_review_rating",
        "shop_review_count", "free_return", "voucher", "direct_sale", "title", "sellerId",
    };

    private static readonly HashSet<string> NumberFields = new HashSet<string>
    {
        "price", "shipping", "total", "shop_review_rating", "price_min", "price_avg", "price_max", "review_rating",
    };

    private static readonly HashSet<string> IntegerFields = new HashSet<string>
    {
        "shop_review_count", "offers_count", "review_count",
    };

    private static readonly HashSet<string> BooleanFields = new HashSet<string>
    {
        "free_return", "voucher", "direct_sale",
    };

    public override async Task<HttpResponseMessage> ExecuteAsync()
    {
        switch (OperationId())
        {
            case "GetOffers":
                return await CreateJob("offers", true).ConfigureAwait(false);
            case "GetProductDetails":
                return await CreateJob("details", true).ConfigureAwait(false);
            case "StartPriceJob":
                return await CreateJob("raw", false).ConfigureAwait(false);
            case "PollJob":
                return await ReadJob(true).ConfigureAwait(false);
            case "GetJobResults":
                return await ReadJob(QueryFlag("wait", true)).ConfigureAwait(false);
            case "ListCountries":
                return ListCountries();
            default:
                return await Context.SendAsync(Context.Request, CancellationToken).ConfigureAwait(false);
        }
    }

    // In some regions the operation ID arrives base64 encoded.
    private string OperationId()
    {
        var id = Context.OperationId;
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(id));
            if (decoded.All(c => char.IsLetterOrDigit(c))) return decoded;
        }
        catch (FormatException)
        {
        }
        return id;
    }

    private async Task<HttpResponseMessage> CreateJob(string view, bool wait)
    {
        var marketplace = (Query("marketplace") ?? "").Trim().ToLowerInvariant();
        var country = (Query("country") ?? "").Trim().ToLowerInvariant();
        var searchBy = (Query("search_by") ?? "ean").Trim().ToLowerInvariant();

        if (!Countries.ContainsKey(marketplace))
            return Error(HttpStatusCode.BadRequest, "Choose a marketplace: " + string.Join(", ", Countries.Keys) + ".");
        if (!Countries[marketplace].Contains(country))
            return Error(HttpStatusCode.BadRequest, "The country '" + country + "' isn't available for " + marketplace + ". Choose one of: " + string.Join(", ", Countries[marketplace]) + ".");
        if (searchBy != "ean" && searchBy != "product_id")
            return Error(HttpStatusCode.BadRequest, "Search by must be 'ean' or 'product_id'.");
        if (searchBy == "product_id" && EanOnly.Contains(marketplace))
            return Error(HttpStatusCode.BadRequest, marketplace + " only supports lookups by EAN.");

        var input = await ReadBody().ConfigureAwait(false);
        var queries = (input["queries"] as JArray ?? new JArray())
            .Select(q => q.Type == JTokenType.Null ? "" : q.ToString().Trim())
            .Where(q => q.Length > 0)
            .ToList();
        if (queries.Count == 0)
            return Error(HttpStatusCode.BadRequest, "Add at least one EAN or product ID to Queries.");
        if (queries.Count > MaxQueries)
            return Error(HttpStatusCode.BadRequest, "A job takes at most " + MaxQueries + " queries. Split the list into several jobs.");

        var body = new JObject
        {
            ["source"] = marketplace,
            ["operation"] = searchBy == "ean" ? "search-by-gtin" : "search-by-id",
            ["country"] = country,
            ["queries"] = new JArray(queries),
        };
        if (marketplace == "amazon")
        {
            var options = new JObject { ["includeDetail"] = QueryFlag("include_details", true) };
            var zipcode = (Query("zipcode") ?? "").Trim();
            if (zipcode.Length > 0) options["zipcode"] = zipcode;
            body["options"] = options;
        }

        var request = BackendRequest(HttpMethod.Post, "/api/v2/jobs");
        request.Content = CreateJsonContent(body.ToString(Formatting.None));
        var response = await Context.SendAsync(request, CancellationToken).ConfigureAwait(false);
        var status = (int)response.StatusCode;

        // The job may have been created and charged even though the request failed. The
        // platform retries 5xx by default, which could start and charge the same job twice.
        if (status == 500 || status == 502 || status == 504)
            return Error((HttpStatusCode)424, "PricePirate returned HTTP " + status + " while starting the job. The job may still have been created and charged, so check the Jobs page in the PricePirate dashboard before you run this action again.");
        if (status != 202 && status != 200)
            return response;

        var created = JObject.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        var jobId = (string)created["job_id"];

        if (!wait)
        {
            var started = new JObject
            {
                ["job_id"] = jobId,
                ["status"] = "pending",
                ["credits_remaining"] = HeaderInt(response, "X-Credits-Remaining"),
            };
            return Json(HttpStatusCode.OK, started);
        }

        return Pending(jobId, view, QueryFlag("skip_empty", false), RetryAfter(response));
    }

    private async Task<HttpResponseMessage> ReadJob(bool wait)
    {
        var jobId = JobIdFromPath();
        if (string.IsNullOrEmpty(jobId))
            return Error(HttpStatusCode.BadRequest, "Enter a job ID.");
        var view = (Query("view") ?? Query("output") ?? "offers").Trim().ToLowerInvariant();
        if (view != "offers" && view != "details" && view != "raw")
            return Error(HttpStatusCode.BadRequest, "Output must be 'offers', 'details' or 'raw'.");
        var skipEmpty = QueryFlag("skip_empty", false);

        var request = BackendRequest(HttpMethod.Get, "/api/v2/jobs/" + Uri.EscapeDataString(jobId) + "?omit_partials=true");
        var response = await Context.SendAsync(request, CancellationToken).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        if (status != 200 && status != 202)
        {
            if (status == 404)
                return Error(HttpStatusCode.NotFound, "Job " + jobId + " wasn't found. Results are kept for about 1 hour after a job finishes, and only API keys of the account that created the job can read it.");
            return response;
        }

        var job = JObject.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        var jobStatus = (string)job["status"];

        if (status == 202 || jobStatus == "pending")
        {
            if (wait) return Pending(jobId, view, skipEmpty, RetryAfter(response));
            return Json(HttpStatusCode.OK, Shape(job, view, skipEmpty));
        }

        // This terminal poll has settled the job, so error refunds are already booked.
        if (jobStatus == "failed")
            return Error(HttpStatusCode.BadRequest, "Price job " + jobId + " failed. Its credits were refunded. You can start the job again.");

        return Json(HttpStatusCode.OK, Shape(job, view, skipEmpty));
    }

    private HttpResponseMessage ListCountries()
    {
        var marketplace = (Query("marketplace") ?? "").Trim().ToLowerInvariant();
        string[] codes;
        if (!Countries.TryGetValue(marketplace, out codes)) codes = new string[0];
        var list = new JArray(codes.Select(code => new JObject
        {
            ["code"] = code,
            ["name"] = CountryNames[code] + " (" + code + ")",
        }));
        return Json(HttpStatusCode.OK, new JObject { ["countries"] = list });
    }

    // ---- Output shapes -------------------------------------------------------------------

    private static JObject Shape(JObject job, string view, bool skipEmpty)
    {
        var output = new JObject
        {
            ["job_id"] = job["job_id"],
            ["status"] = job["status"],
            ["source"] = job["source"],
            ["operation"] = job["operation"],
            ["country"] = job["country"],
            ["created_at"] = job["created_at"],
            ["done"] = job["done"],
            ["total"] = job["total"],
            ["summary"] = new JObject
            {
                ["found"] = job["summary"]?["found"] ?? 0,
                ["not_found"] = job["summary"]?["not_found"] ?? 0,
                ["error"] = job["summary"]?["error"] ?? 0,
            },
            ["credits"] = new JObject
            {
                ["billed"] = job["credits"]?["billed"],
                ["refunded"] = job["credits"]?["refunded"],
            },
        };

        var results = job["results"] as JArray ?? new JArray();
        if (view == "offers") output["offers"] = Offers(job, results, skipEmpty);
        else if (view == "details") output["products"] = Products(results, skipEmpty);
        else output["job"] = job;
        return output;
    }

    private static JArray Offers(JObject job, JArray results, bool skipEmpty)
    {
        var items = new JArray();
        foreach (var entry in results.OfType<JObject>())
        {
            var product = entry["result"] as JObject;
            var offers = product?["offers"] as JArray;
            var context = new JObject
            {
                ["query"] = Str(entry["query"]),
                ["status"] = Str(entry["status"]),
                ["error"] = Str(entry["error"]),
                ["errorCode"] = Str(entry["errorCode"]),
                ["source"] = Str(product?["source"] ?? job["source"]),
                ["country"] = Str(product?["country"] ?? job["country"]),
                ["job_id"] = Str(job["job_id"]),
                ["product_id"] = Str(product?["id"]),
                ["product_name"] = Str(product?["name"]),
                ["product_url"] = Str(product?["url"]),
                ["ean"] = Str(product?["ean"]),
                ["brand"] = Str(product?["brand"]),
                ["price_min"] = Typed("price_min", product?["price_min"]),
                ["offers_count"] = Typed("offers_count", product?["offers_count"]),
                ["fetched_at"] = Str(product?["fetched_at"]),
            };

            if (offers == null || offers.Count == 0)
            {
                if (skipEmpty) continue;
                foreach (var field in OfferFields) context[field] = null;
                items.Add(context);
                continue;
            }

            foreach (var offer in offers.OfType<JObject>())
            {
                var item = (JObject)context.DeepClone();
                foreach (var field in OfferFields) item[field] = Typed(field, offer[field]);
                items.Add(item);
            }
        }
        return items;
    }

    private static JArray Products(JArray results, bool skipEmpty)
    {
        var items = new JArray();
        foreach (var entry in results.OfType<JObject>())
        {
            var product = entry["result"] as JObject;
            if (product == null && skipEmpty) continue;
            items.Add(new JObject
            {
                ["query"] = Str(entry["query"]),
                ["status"] = Str(entry["status"]),
                ["error"] = Str(entry["error"]),
                ["errorCode"] = Str(entry["errorCode"]),
                ["id"] = Str(product?["id"]),
                ["name"] = Str(product?["name"]),
                ["url"] = Str(product?["url"]),
                ["ean"] = Str(product?["ean"]),
                ["brand"] = Str(product?["brand"]),
                ["description"] = Str(product?["description"]),
                ["image_urls"] = Strings(product?["image_urls"]),
                ["categories"] = Strings(product?["categories"]),
                ["review_rating"] = Typed("review_rating", product?["review_rating"]),
                ["review_count"] = Typed("review_count", product?["review_count"]),
                ["price_min"] = Typed("price_min", product?["price_min"]),
                ["price_avg"] = Typed("price_avg", product?["price_avg"]),
                ["price_max"] = Typed("price_max", product?["price_max"]),
                ["offers_count"] = Typed("offers_count", product?["offers_count"]),
                ["platform_data"] = product?["platform_data"] as JObject ?? new JObject(),
            });
        }
        return items;
    }

    // Values keep the API's field names. Types are normalised to the connector schema so a
    // marketplace that sends "4.5" instead of 4.5 can't break the flow's schema validation.
    private static JToken Typed(string field, JToken value)
    {
        if (value == null || value.Type == JTokenType.Null || value.Type == JTokenType.Undefined) return JValue.CreateNull();
        if (NumberFields.Contains(field))
        {
            double number;
            if (value.Type == JTokenType.Float || value.Type == JTokenType.Integer) return new JValue((double)value);
            if (double.TryParse(value.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out number)) return new JValue(number);
            return JValue.CreateNull();
        }
        if (IntegerFields.Contains(field))
        {
            double number;
            if (value.Type == JTokenType.Integer) return new JValue((long)value);
            if (double.TryParse(value.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out number)) return new JValue((long)Math.Round(number));
            return JValue.CreateNull();
        }
        if (BooleanFields.Contains(field))
        {
            if (value.Type == JTokenType.Boolean) return value;
            var text = value.ToString().Trim().ToLowerInvariant();
            if (text == "true" || text == "1" || text == "yes") return new JValue(true);
            if (text == "false" || text == "0" || text == "no") return new JValue(false);
            return JValue.CreateNull();
        }
        return Str(value);
    }

    private static JToken Str(JToken value)
    {
        if (value == null || value.Type == JTokenType.Null || value.Type == JTokenType.Undefined) return JValue.CreateNull();
        if (value.Type == JTokenType.Object || value.Type == JTokenType.Array) return new JValue(value.ToString(Formatting.None));
        if (value.Type == JTokenType.Date) return new JValue(((DateTime)value).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
        return new JValue(value.ToString());
    }

    private static JArray Strings(JToken value)
    {
        if (value == null || value.Type == JTokenType.Null) return new JArray();
        var array = value as JArray;
        if (array != null) return new JArray(array.Where(v => v.Type != JTokenType.Null).Select(v => (JToken)new JValue(v.ToString())));
        return new JArray(new JValue(value.ToString()));
    }

    // ---- HTTP helpers --------------------------------------------------------------------

    private HttpRequestMessage BackendRequest(HttpMethod method, string pathAndQuery)
    {
        var request = new HttpRequestMessage(method, ApiBase + pathAndQuery);
        IEnumerable<string> key;
        if (Context.Request.Headers.TryGetValues("x-api-key", out key))
            request.Headers.TryAddWithoutValidation("x-api-key", key);
        request.Headers.TryAddWithoutValidation(IntegrationHeader, IntegrationName);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    // An absolute Location that names the output view, so the platform's next poll reaches
    // PollJob with everything it needs to shape the terminal response.
    private static HttpResponseMessage Pending(string jobId, string view, bool skipEmpty, int retryAfter)
    {
        var response = Json(HttpStatusCode.Accepted, new JObject { ["job_id"] = jobId, ["status"] = "pending" });
        response.Headers.Location = new Uri(ApiBase + "/api/v2/jobs/" + Uri.EscapeDataString(jobId)
            + "?view=" + view + "&skip_empty=" + (skipEmpty ? "true" : "false"));
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(retryAfter));
        return response;
    }

    private static int RetryAfter(HttpResponseMessage response)
    {
        var delta = response.Headers.RetryAfter?.Delta;
        if (delta.HasValue && delta.Value.TotalSeconds >= 1) return (int)Math.Min(delta.Value.TotalSeconds, 60);
        return 5;
    }

    private static JToken HeaderInt(HttpResponseMessage response, string name)
    {
        IEnumerable<string> values;
        long number;
        if (response.Headers.TryGetValues(name, out values) && long.TryParse(values.FirstOrDefault(), out number)) return new JValue(number);
        return JValue.CreateNull();
    }

    private string JobIdFromPath()
    {
        var segments = Context.Request.RequestUri.AbsolutePath.Split('/').Where(s => s.Length > 0).ToList();
        var index = segments.LastIndexOf("jobs");
        return index >= 0 && index + 1 < segments.Count ? Uri.UnescapeDataString(segments[index + 1]) : null;
    }

    private string Query(string name)
    {
        return HttpUtility.ParseQueryString(Context.Request.RequestUri.Query).Get(name);
    }

    private bool QueryFlag(string name, bool fallback)
    {
        var value = Query(name);
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        return value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<JObject> ReadBody()
    {
        if (Context.Request.Content == null) return new JObject();
        var text = await Context.Request.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text)) return new JObject();
        var token = JToken.Parse(text);
        var list = token as JArray;
        if (list != null) return new JObject { ["queries"] = list };
        return token as JObject ?? new JObject();
    }

    private static HttpResponseMessage Json(HttpStatusCode status, JObject body)
    {
        return new HttpResponseMessage(status) { Content = CreateJsonContent(body.ToString(Formatting.None)) };
    }

    private static HttpResponseMessage Error(HttpStatusCode status, string message)
    {
        return Json(status, new JObject { ["error"] = true, ["message"] = message });
    }
}
