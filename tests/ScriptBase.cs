// Local stand-ins for the custom-code runtime types, as published in
// https://learn.microsoft.com/en-us/connectors/custom-connectors/write-code
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

public abstract class ScriptBase
{
    public IScriptContext Context { get; set; }

    public CancellationToken CancellationToken { get; set; }

    public static StringContent CreateJsonContent(string serializedJson)
    {
        return new StringContent(serializedJson, Encoding.UTF8, "application/json");
    }

    public abstract Task<HttpResponseMessage> ExecuteAsync();
}

public interface IScriptContext
{
    string CorrelationId { get; }

    string OperationId { get; }

    HttpRequestMessage Request { get; }

    ILogger Logger { get; }

    Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken);
}
