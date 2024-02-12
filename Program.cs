using System.Text;
using Yarp.ReverseProxy.Transforms;
using WebMarkupMin.AspNetCore8;
using EasyCaching.LiteDB;
using EasyCaching.Core;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.Extensions;

using Serilog;
using Microsoft.AspNetCore.HttpLogging;

class Replacement(string pattern, string replacement)
{
	readonly public string pattern = pattern;
	readonly public string replacement = replacement;
}

class CacheFile(byte[] content, string contentType)
{
	readonly public byte[] content = content;
	readonly public string contentType = contentType;
}

partial class Program
{
	[GeneratedRegex(@"\.(png|jpg|ico|jpeg|woff|woff2|ttf|otf|cpp|js|css|svg|txt)\??(.*)$")]
	private static partial Regex CachesuffixRegex();

	readonly static string HOST = Environment.GetEnvironmentVariable("HOST") ?? "vjmirror.link";
	readonly static string BETTER_STACK_TOKEN = Environment.GetEnvironmentVariable("BETTER_STACK_TOKEN") ?? "";

	readonly static Regex CACHE_SUFFIX = CachesuffixRegex();
	readonly static IEnumerable<Replacement> REPLACEMENTS = [
		new("<title>Virtual Judge</title>", "<title>Virtual Judge (Unoffical Mirror)</title>"),
		new("vjudge.net", HOST),
		new("vjudge.csgrandeur.cn", HOST),
		// new("vj.csgrandeur.cn", HOST),
		new("vjudge.net.cn", HOST),
		new("Server Time: <span class=\"currentTimeTZ\"></span>", "Server Time: <span class=\"currentTimeTZ\"></span><br>Unoffical Mirror; Powered by .NET 8.0 & YARP<br>Feedback (for mirror issues): me[at]imken.moe"),
	];

	private static void Main(string[] args)
	{
		dotenv.net.DotEnv.Load();

		Log.Logger = new LoggerConfiguration()
			.WriteTo.BetterStack(sourceToken: BETTER_STACK_TOKEN)
			.MinimumLevel.Information()
			.CreateLogger();

		var builder = WebApplication.CreateBuilder(args);

		builder.Services.AddControllers();
		builder.Services
			.AddReverseProxy()
			.LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
			.AddTransforms(builderContext =>
			{
				var cachingProvider = builderContext.Services.GetRequiredService<IEasyCachingProvider>()
					?? throw new Exception("Cache is broken.");

				builderContext
					.AddRequestTransform(RequestTransformer)
					.AddResponseTransform(responseContext => ResponseTransformer(responseContext, cachingProvider));
			});

		builder.Services
			.AddWebMarkupMin(options =>
			{
				options.AllowMinificationInDevelopmentEnvironment = true;
				options.DisablePoweredByHttpHeaders = true;
			})
			.AddHtmlMinification(options =>
			{
				options.SupportedHttpStatusCodes.Add(403);
				options.SupportedHttpStatusCodes.Add(404);
			});

		builder.Services.AddMvc();
		builder.Services
			.AddEasyCaching(option =>
			{
				option.UseLiteDB(config =>
				{
					config.DBConfig = new LiteDBDBOptions { FileName = "_cache.db" };
				});
			});

		builder.Services
			.AddHttpLogging(logging =>
			{
				logging.LoggingFields = HttpLoggingFields.All;
				logging.CombineLogs = true;
			});

		builder.Host.UseSerilog();

		var app = builder.Build();

		app.MapReverseProxy(proxyPipeline =>
		{
			proxyPipeline
				.Use(CacheResponseMiddleware)
				.Use(PoweredByMiddleware);
		});

		app.UseWebMarkupMin();

		try
		{
			app.Run();
		}
		catch (Exception ex)
		{
			Log.Fatal("Application terminated unexpectedly\n" + ex.ToString());
		}
		finally
		{
			Log.CloseAndFlush();
		}
	}

	private static async Task PoweredByMiddleware(HttpContext context, Func<Task> next)
	{
		if (!context.Response.HasStarted)
			context.Response.Headers.Append("X-Powered-By", ".NET 8.0 & YARP");
		await next();
	}

	private static async Task CacheResponseMiddleware(HttpContext context, Func<Task> next)
	{
		var cachingProvider = context.RequestServices.GetRequiredService<IEasyCachingProvider>()
			?? throw new Exception("Cache is broken.");
		var key = context.Request.GetEncodedUrl();

		if (!context.Response.HasStarted && cachingProvider.Exists(key))
		{
			var cacheValue = cachingProvider.Get<CacheFile>(key);
			if (cacheValue != null)
			{
				context.Response.Headers.Remove("Content-Type");
				context.Response.Headers.Append("Content-Type", cacheValue.Value.contentType);
				context.Response.Headers.CacheControl = "public, max-age=2592000";
				context.Response.Headers.Append("X-Imken-Cache", "HIT");
				await context.Response.BodyWriter.WriteAsync(cacheValue.Value.content);
				return;
			}
		}
		await next();
	}

	private static async ValueTask ResponseTransformer(ResponseTransformContext responseContext, IEasyCachingProvider cachingProvider)
	{
		if (responseContext.ProxyResponse == null)
			throw new Exception("Remote host returns nothing. Please try again.");

		if (responseContext.ProxyResponse.Headers.TryGetValues("Set-Cookie", out var setCookieValues))
		{
			responseContext.HttpContext.Response.Headers.Remove("Set-Cookie");
			responseContext.HttpContext.Response.Headers.Append(
				"Set-Cookie",
				setCookieValues
					.Select(x => x.Replace("Domain=vjudge.net;", ""))
					.ToArray()
			);
		}

		responseContext.ProxyResponse.Content.Headers.Remove("Set-Cookie");

		if (responseContext.ProxyResponse.StatusCode != System.Net.HttpStatusCode.OK)
			return;

		var cachePath = responseContext.HttpContext.Request.Path;
		var cacheKey = responseContext.HttpContext.Request.GetEncodedUrl();
		var cacheValue = cachingProvider.Get<CacheFile>(cachePath);

		var stream = await responseContext.ProxyResponse.Content.ReadAsStreamAsync();
		var memoryStream = new MemoryStream();
		stream.CopyTo(memoryStream);
		using var reader = new BinaryReader(stream, encoding: Encoding.UTF8);

		byte[] bodyBytes = memoryStream.ToArray();
		var body = Encoding.UTF8.GetString(bodyBytes);
		string contentType = "";


		responseContext.SuppressResponseBody = true;

		if (responseContext.ProxyResponse.Content.Headers.TryGetValues("Content-Type", out var rawContentType))
		{
			contentType = rawContentType.First();
			var shouldRewrite = false
				|| contentType.Contains("text/")
				|| contentType.Contains("javascript")
				|| contentType.Contains("json")
			;

			if (!string.IsNullOrEmpty(body) && shouldRewrite)
			{
				foreach (var replace in REPLACEMENTS)
					body = body.Replace(replace.pattern, replace.replacement);

				bodyBytes = Encoding.UTF8.GetBytes(body);
			}
		}


		var shouldCache = false
			|| cachePath.StartsWithSegments("/static")
			|| cachePath.StartsWithSegments("/problem/description")
			|| cachePath.StartsWithSegments("/solution/snapshot")
			|| CACHE_SUFFIX.IsMatch(cachePath);

		if (responseContext.ProxyResponse.IsSuccessStatusCode && shouldCache)
		{
			await cachingProvider.SetAsync(
				cacheKey,
				new CacheFile(bodyBytes, contentType),
				TimeSpan.FromDays(30)
			);
			responseContext.HttpContext.Response.Headers.Append("X-Imken-Cache", "LOST");
		}
		else
		{
			responseContext.HttpContext.Response.Headers.Append("X-Imken-Cache", "BYPASS");
		}

		responseContext.HttpContext.Response.ContentLength = bodyBytes.Length;
		await responseContext.HttpContext.Response.Body.WriteAsync(bodyBytes);
	}

	private static ValueTask RequestTransformer(RequestTransformContext requestContext)
	{
		requestContext.ProxyRequest.Headers.Remove("Accept-Encoding");
		requestContext.ProxyRequest.Headers.Remove("Origin");
		requestContext.ProxyRequest.Headers.Add("Origin", "https://vjudge.net");
		return new ValueTask();
	}
}
