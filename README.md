# PricePirate Price Data connector for Power Automate

A certified-connector candidate for Power Automate, Power Apps, Logic Apps and Copilot Studio. It wraps the [PricePirate Price API](https://pricepirate.com/en/price-api/), which returns live marketplace prices by EAN, GTIN or marketplace product ID.

User documentation is in [intro.md](intro.md). The connector files follow the layout of [microsoft/PowerPlatformConnectors](https://github.com/microsoft/PowerPlatformConnectors) (`certified-connectors/<Name>/`).

| File | Purpose |
| --- | --- |
| `apiDefinition.swagger.json` | Swagger 2.0 definition of the actions |
| `apiProperties.json` | Connection parameter, brand colour, policy and custom code operations |
| `script.csx` | Custom code: maps actions to the job API and shapes results |
| `icon.png` | 230 × 230 icon on the brand colour `#0B0B0B` |
| `intro.md` | Documentation published with the connector |
| `tests/` | Local tests for `script.csx` |

## How it works

The Price API runs lookups as asynchronous jobs. `POST /api/v2/jobs` answers `202 Accepted` with a `Location` header, and `GET /api/v2/jobs/{id}` answers `202` while the job is pending and `200` once it is finished. Power Automate follows that pattern on its own: it polls `Location` every `Retry-After` seconds until it gets a `200`.

`script.csx` does the following:

- Maps **Get offers**, **Get product details** and **Start price job** onto `POST /api/v2/jobs`.
- Rewrites `Location` to an absolute URL that names the output view, so every poll reaches the internal `PollJob` operation.
- Flattens a finished job into one item per offer or one item per query. Queries without offers stay in the output with their status unless the user turns on **Skip queries without offers**.
- Raises an error for a failed job after the terminal poll, which settles the refund.
- Turns a `500`, `502` or `504` on job creation into a non-retryable `424`. Power Automate retries `5xx` by default, and a retry could start and charge the same job twice.
- Answers the country dropdown from a static catalogue, without calling the API.

Every request sends `x-pricepirate-integration: power-automate`, so the gateway can attribute traffic. Billing and limits still follow the API key.

## Tests

```sh
dotnet run --project tests          # offline tests, no credits
PRICEPIRATE_API_KEY=… dotnet run --project tests -- live   # one real job, 1 credit
```

The offline tests use a recorded Idealo job in `tests/fixtures/` and a fake backend. They also flatten a synthetic 1,000-query job, which takes a few seconds, well under the platform's 2-minute limit for custom code.

## Licence

MIT, © UCX Media – Sergei Gaponik. Support: info@gaponik.com.
