using Coppertop.Trader;
using Coppertop.Trader.Account;
using Coppertop.Trader.Api;
using Coppertop.Trader.Kraken;
using Coppertop.Trader.Trading;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<TraderOptions>(builder.Configuration.GetSection(TraderOptions.Section));
builder.Services.Configure<CoppertopApiOptions>(builder.Configuration.GetSection(CoppertopApiOptions.Section));
builder.Services.Configure<KrakenOptions>(builder.Configuration.GetSection(KrakenOptions.Section));

var mode = builder.Configuration[$"{TraderOptions.Section}:Mode"] ?? "Paper";
if (!mode.Equals("Paper", StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException($"Trader mode '{mode}' is not supported yet. Only 'Paper' is enabled.");

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<KrakenPairCache>();
builder.Services.AddSingleton<CandleCache>();
builder.Services.AddSingleton<TradeTape>();
builder.Services.AddHttpClient<KrakenClient>(c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddHttpClient<CoppertopApiClient>(c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddScoped<TraderEngine>();
builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<AccountWorker>();

builder.Build().Run();
