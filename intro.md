# PricePirate Price Data

The [PricePirate Price API](https://pricepirate.com/en/price-api/) returns live marketplace prices for EANs, GTINs and marketplace product IDs. This connector looks up every offer for a product, with shop, price, shipping and availability, or product details with the lowest, average and highest price. You can look up 1 to 1,000 products in one action.

Supported marketplaces and price comparison sites: Idealo, Amazon, Google Shopping, Bol, Klarna and PriceRunner. The available countries depend on the marketplace.

## Publisher: UCX Media

## Prerequisites

You need a PricePirate account and an API key. [Sign up](https://dashboard.pricepirate.com) to get 100 free credits. Each query costs one credit, and failed queries are refunded.

## How to get credentials

1. Sign in to the [PricePirate dashboard](https://dashboard.pricepirate.com).
2. Open **API Keys** and select **Create API Key**.
3. Paste the key into the **API Key** field when you create the connection.

## Get started with your connector

1. Add the **Get offers by EAN or product ID** action to a flow.
2. Choose a **Marketplace** and a **Country**, and add one or more EANs to **Queries**.
3. Use **Apply to each** on **Offers** to work with each offer, for example to write it to Excel or SharePoint.

Price jobs usually take 1 to 2 minutes and can take up to about 15 minutes. The action waits until the job is finished, so the next step gets the complete results.

## Operations

- **Get offers by EAN or product ID**: Returns one item per offer, plus one item for each query without offers, so no row disappears silently. Turn on **Skip queries without offers** to leave those out.
- **Get product details by EAN or product ID**: Returns one item per query with product details and the lowest, average and highest price.
- **Start price job**: Starts a job and returns its job ID right away, for flows that read the results later.
- **Get job results**: Reads a job as offers, product details or the raw job JSON. It waits for the job to finish unless you turn off **Wait until finished**.
- **Get account**: Returns your credit balance, limits and available marketplaces.

## Known issues and limitations

- Results are kept for about 1 hour after a job finishes. After that, **Get job results** returns a "not found" error.
- Klarna and PriceRunner support lookups by EAN only.
- The options **Include product details** and **Delivery postal code** apply to Amazon only.
- If PricePirate reports a server error while a job is being started, the action fails without retrying, because the job may already have been created and charged. Check the Jobs page in the dashboard before you run it again.
- A job takes at most 1,000 queries. Split longer lists into several jobs.

## Frequently asked questions

**What does a lookup cost?** One credit per query. Queries that find nothing still cost a credit. Queries that fail with an error are refunded automatically.

**Where can I find the full API documentation?** In the [Price API documentation](https://pricepirate.com/docs/en/api/price-data/overview).

## Support

Write to [info@gaponik.com](mailto:info@gaponik.com) or visit the [support page](https://pricepirate.com/docs/en/guide/help/support).
