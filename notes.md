## GOAL
Create an automated system to trade Crypto on Kraken in C# that uses plain-code math for the trading decisions and a low-cost LLM only where language is needed (news, summaries). The system should be able to analyze past market data and make informed trading decisions based on that analysis. It should be able to look at past research and use that as a guide about what to do, and learn from the experience.

Trades should be done automatically, and with the goal of making money consistently while managing risk effectively.

### Success Criteria
We don't want to hold on to the assets for too long; the system should aim to make timely trades based on market conditions, selling when there has been enough profit or when market indicators suggest a downturn. Must factor in the cost of trading fees and other associated expenses.

We need a FrontEnd to see the current status including current portfolio holdings, outstanding orders, recent trades, and overall performance metrics.

We want a single service for the trading, and a single service for the WFE.

UPDATED: Three isolated backend services (Api, Trader, Research) + the FrontEnd. No shared libraries; each service owns its own DTOs/clients. Api is the only SQLite writer. Research publishes "opportunities" (what + limits); Trader decides when, with zero LLM calls. First strategy: buy-the-dip (SMA − k·σ), TP +1.5%, SL −2%. See README.md.

### PAST Problems
- The system has been successful, but the cost of the LLM was more than the profits generated from trading.
- Keeping track of trades, outstanding orders, and portfolio performance has been challenging.

## Current Plan
- UPDATED: TypeSafe AI removed from the plan. The rule-based screener (Research) makes the numeric decisions for free.
- Use Sonar from Perplexity to get market news.
- Use the Kraken API to execute trades and retrieve market data.
- Create Skills for the traditional LLM to handle tasks that are better suited for it, such as natural language understanding and complex reasoning.
- Store memory about each crypto into a memory file in blob storage.
- When selecting an LLM, we can use meta/muse-spark-1.3-contributor which is $0.10 / $0.20 per 1M tokens. If that produces errors, we can use openai/gpt-6-luna which is $0.10 / $0.50 per 1M. Even better we can start with thinkingmachines/inkling:free which is free, and then fall back on muse, and then luna.
- I have some credits on Azure Foundry, so we may be able to use that for running the LLMs more cost-effectively.
- I want to focus on 10-15 most traded cryptocurrencies.

### Anticipated Flow for buying Crypto
1) Retrieve the latest market data from the Kraken API.
2) Screen the market data with plain-code rules and a strategy replay to identify potential trading opportunities.
3) Use the traditional LLM to interpret market news and complex reasoning tasks.
4) Make trading decisions based on the screener, with the traditional LLM able to veto on news. NOTE: It's very possible that no buy order is reccomended.
5) Execute trades through the Kraken API.
6) Update the memory file in blob storage with the latest trade and market information.
7) Update the FrontEnd with the current portfolio holdings, outstanding orders, recent trades, and overall performance metrics.

### Anticipated Flow for selling Crypto
1) Retrieve the latest market data from the Kraken API.
2) Check prices against the take-profit and stop-loss in plain code (the Trader).
3) Use the traditional LLM to interpret market news and complex reasoning tasks.
4) Make selling decisions in plain code; news from the traditional LLM can trigger an early exit. NOTE: It's very possible that no sell order is recommended.
5) Execute sell orders through the Kraken API.
6) Update the memory file in blob storage with the latest trade and market information.
7) Update the FrontEnd with the current portfolio holdings, outstanding orders, recent trades, and overall performance metrics.

### Outstanding Questions
- What part of the process needs a traditional LLM? UPDATED: news vetoes (Sonar), and later summaries/memory. Numeric decisions stay in code.
- How often do we run the process to check for prices, and make trades? More frequent use may lead to better responsiveness to market changes but could increase LLM costs.
- Where do we store data? Can we use SQLite?
- Rather than deal with making API calls, can we just use the API instead? Along with MCP? https://docs.kraken.com/home/cli - UPDATED: NO. This is not the plan, use the API as much as we can.
- There is an SDK, but it's in rust (https://docs.kraken.com/home/sdks/rust) is this something we want to consider using, or should we stick with the API? I'd prefer the API using standard HTTP requests.

### General Rules
- Trades should be in the $7 to $15 range.
- Always consider the overall portfolio balance and risk exposure before executing a trade.
- Don't overload one particular asset or trade type; maintain a diversified portfolio to manage risk effectively.
- Ensure that all trades are logged and memory files are updated promptly to maintain accurate records.

## Next Steps
- Create a system that is able to use the CLI or the MCP so we're able to get current balances and execute trades efficiently. NOTE: We need to verify the data that is returned so we know what to write. UPDATED: USE API for retrieving balances and executing trades.
- Create a way to write this information into the database.
- Create a system that will do research, and provide actionable insights for trading decisions written into the memory file in blob storage.
- Create a system that will automatically execute trades based on the insights and decisions recorded in the memory file and written into the database.
- Create a system that will monitor the performance of executed trades and update the memory file and database accordingly.
- Create a data validation system to ensure the accuracy and integrity of the information being written into the database and memory file.