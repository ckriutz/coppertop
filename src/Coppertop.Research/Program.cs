using Coppertop.Research;
using Coppertop.Research.Api;
using Coppertop.Research.Kraken;
using Coppertop.Research.News;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<ResearchOptions>(builder.Configuration.GetSection(ResearchOptions.Section));
builder.Services.Configure<OpenRouterOptions>(builder.Configuration.GetSection(OpenRouterOptions.Section));
builder.Services.Configure<CoppertopApiOptions>(builder.Configuration.GetSection(CoppertopApiOptions.Section));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient<KrakenPublicClient>(c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddHttpClient<CoppertopApiClient>(c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddHttpClient<SonarClient>(c => c.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddSingleton<NewsGate>();
builder.Services.AddHostedService<Worker>();

builder.Build().Run();
