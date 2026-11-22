using System.Reflection;
using System.Xml.Linq;
using Microsoft.AspNetCore.Authorization;
using ShortLink.Api.Controllers;
using ShortLink.Core.Abstractions;
using ShortLink.Infrastructure.Caching;
using ShortLink.Infrastructure.Persistence;
using ShortLink.Worker;

namespace ShortLink.Core.Tests;

/// <summary>Memvalidasi boundary Clean Architecture, hot-path redirect, otorisasi, dan konfigurasi analyzer.</summary>
public sealed class ArchitectureTests
{
    private static readonly string[] ForbiddenCorePrefixes =
    [
        "ShortLink.Infrastructure",
        "ShortLink.Api",
        "ShortLink.Worker",
        "Microsoft.EntityFrameworkCore",
        "Npgsql",
        "StackExchange.Redis",
        "RabbitMQ.Client",
        "Microsoft.AspNetCore",
        "Microsoft.Extensions.Hosting",
        "Microsoft.Extensions.DependencyInjection",
        "BCrypt",
        "System.IdentityModel",
        "Microsoft.IdentityModel"
    ];

    [Fact]
    public void CoreAssembly_DoesNotReferenceOuterLayersOrInfrastructurePackages()
    {
        var referencedNames = typeof(ShortLinkService).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .ToList();

        var violations = referencedNames
            .Where(name => ForbiddenCorePrefixes.Any(prefix =>
                string.Equals(name, prefix, StringComparison.OrdinalIgnoreCase)
                || name.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.Empty(violations);
    }

    [Fact]
    public void CoreProjectFile_HasNoProjectOrPackageReferences()
    {
        var repoRoot = FindRepositoryRoot();
        var doc = XDocument.Load(Path.Combine(repoRoot, "src/ShortLink.Core/ShortLink.Core.csproj"));

        var projectRefs = doc.Descendants().Where(e => e.Name.LocalName == "ProjectReference").ToList();
        var packageRefs = doc.Descendants().Where(e => e.Name.LocalName == "PackageReference").ToList();

        Assert.Empty(projectRefs);
        Assert.Empty(packageRefs);
    }

    [Fact]
    public void LayerProjectReferences_FollowCleanArchitectureDirection()
    {
        var repoRoot = FindRepositoryRoot();

        var infraRefs = GetProjectReferences(repoRoot, "src/ShortLink.Infrastructure/ShortLink.Infrastructure.csproj");
        Assert.Single(infraRefs);
        Assert.Contains("ShortLink.Core", infraRefs);

        var apiRefs = GetProjectReferences(repoRoot, "src/ShortLink.Api/ShortLink.Api.csproj");
        Assert.Contains("ShortLink.Core", apiRefs);
        Assert.Contains("ShortLink.Infrastructure", apiRefs);
        Assert.DoesNotContain("ShortLink.Worker", apiRefs);

        var workerRefs = GetProjectReferences(repoRoot, "src/ShortLink.Worker/ShortLink.Worker.csproj");
        Assert.Contains("ShortLink.Core", workerRefs);
        Assert.Contains("ShortLink.Infrastructure", workerRefs);
        Assert.DoesNotContain("ShortLink.Api", workerRefs);
    }

    [Fact]
    public void OuterLayerAssemblies_DoNotViolateLayerBoundaries()
    {
        var infraRefs = GetReferencedAssemblyNames(typeof(AppDbContext).Assembly);
        Assert.Contains("ShortLink.Core", infraRefs);
        Assert.DoesNotContain("ShortLink.Api", infraRefs);
        Assert.DoesNotContain("ShortLink.Worker", infraRefs);
        Assert.DoesNotContain(infraRefs, name =>
            string.Equals(name, "Microsoft.AspNetCore", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Microsoft.AspNetCore.", StringComparison.OrdinalIgnoreCase));

        var apiRefs = GetReferencedAssemblyNames(typeof(ShortLinksController).Assembly);
        Assert.DoesNotContain("ShortLink.Worker", apiRefs);

        var workerRefs = GetReferencedAssemblyNames(typeof(ClickEventConsumer).Assembly);
        Assert.DoesNotContain("ShortLink.Api", workerRefs);
    }

    [Fact]
    public void CorePublicTypes_StayUnderCoreNamespaceAndExcludeTemplateLeftovers()
    {
        var coreAssembly = typeof(ShortLinkService).Assembly;

        var exportedTypes = coreAssembly.GetExportedTypes();
        Assert.NotEmpty(exportedTypes);
        Assert.All(exportedTypes, type =>
        {
            Assert.NotNull(type.Namespace);
            Assert.True(
                string.Equals(type.Namespace, "ShortLink.Core", StringComparison.Ordinal)
                || type.Namespace!.StartsWith("ShortLink.Core.", StringComparison.Ordinal),
                $"Exported type '{type.FullName}' must reside under ShortLink.Core.");
        });

        var allTypes = coreAssembly.GetTypes();
        Assert.DoesNotContain(allTypes, type => string.Equals(type.Name, "Class1", StringComparison.Ordinal));
    }

    [Fact]
    public void CoreAbstractions_ArePublicInterfacesAndAsyncMethodsAcceptCancellationToken()
    {
        var coreAssembly = typeof(ShortLinkService).Assembly;
        var abstractionTypes = coreAssembly
            .GetExportedTypes()
            .Where(t => string.Equals(t.Namespace, "ShortLink.Core.Abstractions", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(abstractionTypes);
        Assert.All(abstractionTypes, t => Assert.True(t.IsInterface && t.IsPublic, $"'{t.FullName}' must be a public interface."));

        var targetTypes = abstractionTypes
            .Concat([
                typeof(ShortLinkService),
                typeof(AuthService),
                typeof(ClickIngestionService),
                typeof(ClickAggregationService)
            ]);

        foreach (var type in targetTypes)
        {
            var asyncMethods = type
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialName && m.Name.EndsWith("Async", StringComparison.Ordinal));

            foreach (var method in asyncMethods)
            {
                var parameters = method.GetParameters();
                Assert.True(
                    parameters.Length > 0 && parameters[^1].ParameterType == typeof(CancellationToken),
                    $"Method '{type.Name}.{method.Name}' must accept CancellationToken as its final parameter.");
            }
        }
    }

    [Fact]
    public void RedirectController_HotPath_UsesCachedSlugResolverAndAsyncPublisherWithoutDirectDbDependency()
    {
        var constructors = typeof(RedirectController).GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        var ctor = Assert.Single(constructors);
        var parameterTypes = ctor.GetParameters().Select(p => p.ParameterType).ToHashSet();

        Assert.Contains(typeof(CachedSlugResolver), parameterTypes);
        Assert.Contains(typeof(IClickEventPublisher), parameterTypes);
        Assert.DoesNotContain(typeof(AppDbContext), parameterTypes);
        Assert.DoesNotContain(typeof(IShortLinkRepository), parameterTypes);
        Assert.DoesNotContain(typeof(IShortLinkClickCounter), parameterTypes);
        Assert.DoesNotContain(typeof(ShortLinkService), parameterTypes);
    }

    [Fact]
    public void ShortLinksController_RequiresAuthorizationWithoutAnonymousBypass()
    {
        var controllerType = typeof(ShortLinksController);
        Assert.NotNull(controllerType.GetCustomAttribute<AuthorizeAttribute>());

        var declaredMethods = controllerType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        Assert.All(declaredMethods, method =>
            Assert.Null(method.GetCustomAttribute<AllowAnonymousAttribute>()));
    }

    [Fact]
    public void DirectoryBuildProps_EnablesStandardAnalyzersAndTreatWarningsAsErrorsWithoutNoWarn()
    {
        var repoRoot = FindRepositoryRoot();
        var propsPath = Path.Combine(repoRoot, "Directory.Build.props");
        Assert.True(File.Exists(propsPath), "Directory.Build.props must exist at repository root.");

        var doc = XDocument.Load(propsPath);
        Assert.Equal("true", GetSinglePropertyValue(doc, "EnableNETAnalyzers"));
        Assert.Equal("10.0", GetSinglePropertyValue(doc, "AnalysisLevel"));
        Assert.Equal("true", GetSinglePropertyValue(doc, "EnforceCodeStyleInBuild"));
        Assert.Equal("true", GetSinglePropertyValue(doc, "TreatWarningsAsErrors"));
        Assert.Equal("enable", GetSinglePropertyValue(doc, "Nullable"));
        Assert.DoesNotContain(doc.Descendants(), e => e.Name.LocalName == "NoWarn");
    }

    private static string? GetSinglePropertyValue(XDocument doc, string propertyName)
        => doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == propertyName)?
            .Value
            .Trim();

    private static IReadOnlySet<string> GetProjectReferences(string repoRoot, string relativeCsprojPath)
    {
        var doc = XDocument.Load(Path.Combine(repoRoot, relativeCsprojPath));
        return doc.Descendants()
            .Where(e => e.Name.LocalName == "ProjectReference")
            .Select(e => e.Attribute("Include")?.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => Path.GetFileNameWithoutExtension(v!.Replace('\\', '/')))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlySet<string> GetReferencedAssemblyNames(Assembly assembly)
        => assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ShortLink.slnx")))
                return dir.FullName;

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root containing ShortLink.slnx.");
    }
}
