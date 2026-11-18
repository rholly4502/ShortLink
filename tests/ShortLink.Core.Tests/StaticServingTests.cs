using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ShortLink.Core.Abstractions;
using ShortLink.Infrastructure.Caching;

namespace ShortLink.Core.Tests;

/// <summary>
/// Integration test atas pipeline penuh (WebApplicationFactory) utk verifikasi
/// integrasi static-serving produksi (P6.5): menyajikan SPA, asset, fallback,
/// dan TIDAK menelan rute API.
/// </summary>
public sealed class StaticServingTests
{
    [Fact]
    public async Task Production_ServesIndexHtmlAssetsAndSpaFallback()
    {
        var webRoot = Path.Combine(Path.GetTempPath(), "shortlink-webroot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(webRoot);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(webRoot, "index.html"),
                "<!DOCTYPE html><html><head><title>ShortLink</title></head><body><div id=\"root\"></div></body></html>");
            await File.WriteAllTextAsync(Path.Combine(webRoot, "style.css"), "body { margin: 0; }");

            await using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.UseEnvironment("Production");
                    builder.UseWebRoot(webRoot);
                    builder.ConfigureTestServices(services =>
                    {
                        services.RemoveAll<IRedisStringStore>();
                        services.AddSingleton<IRedisStringStore>(new FakeRedisStringStore());
                    });
                });

            using var client = factory.CreateClient();

            // Index di akar subpath.
            var index = await client.GetAsync("/");
            Assert.Equal(HttpStatusCode.OK, index.StatusCode);
            Assert.Contains("ShortLink", await index.Content.ReadAsStringAsync());

            // Asset statis nyata.
            var css = await client.GetAsync("/style.css");
            Assert.Equal(HttpStatusCode.OK, css.StatusCode);

            // SPA fallback utk rute client-side.
            var fallback = await client.GetAsync("/dashboard/some/deep/route");
            Assert.Equal(HttpStatusCode.OK, fallback.StatusCode);
            Assert.Contains("ShortLink", await fallback.Content.ReadAsStringAsync());

            // Rute API tidak tertelan fallback (belum auth -> 401, bukan index.html).
            var api = await client.GetAsync("/api/links");
            Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);
        }
        finally
        {
            Directory.Delete(webRoot, recursive: true);
        }
    }
}
