using Microsoft.Extensions.Configuration;
using Utils.Configuration;

namespace Utils.Tests;

// No database and no container - this is configuration, so the tests are fast and the only real I/O
// is a temporary directory standing in for /run/secrets.
public class SecretsConfigurationTests : IDisposable
{
    private readonly string _secretsDirectory =
        Path.Combine(Path.GetTempPath(), $"utils-secrets-{Guid.NewGuid():N}");

    public SecretsConfigurationTests() => Directory.CreateDirectory(_secretsDirectory);

    public void Dispose()
    {
        if (Directory.Exists(_secretsDirectory))
        {
            Directory.Delete(_secretsDirectory, recursive: true);
        }
    }

    private void WriteSecretFile(string name, string value) =>
        File.WriteAllText(Path.Combine(_secretsDirectory, name), value);

    private IConfiguration BuildWithSecretsDirectory(params (string Key, string Value)[] inMemory) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                inMemory
                    .Select(pair => new KeyValuePair<string, string?>(pair.Key, pair.Value))
                    .Append(new KeyValuePair<string, string?>(
                        SecretsConfigurationExtensions.SecretsDirectoryKey, _secretsDirectory)))
            .AddDeploymentSecrets()
            .Build();

    private static SecretRequirement Jwt() =>
        SecretRequirement.Required("Jwt:Secret", "signing and validating bearer tokens", minimumLength: 32);

    private static SecretRequirement Db() =>
        SecretRequirement.Required("ConnectionStrings:ServicesDb", "the shared services database");

    // ---- Where a secret can come from ----

    // The whole provider-agnostic claim: one name, two mechanisms, no cloud. "__" maps to ":" in
    // both, which is why the file is named Jwt__Secret rather than Jwt:Secret.
    [Fact]
    public void ASecretCanComeFromAFileInTheSecretsDirectory()
    {
        WriteSecretFile("Jwt__Secret", "a-genuinely-long-enough-signing-key-value");

        var configuration = BuildWithSecretsDirectory();

        Assert.Equal("a-genuinely-long-enough-signing-key-value", configuration["Jwt:Secret"]);
    }

    [Fact]
    public void AConnectionStringCanComeFromAFile()
    {
        WriteSecretFile("ConnectionStrings__ServicesDb", "Host=db;Database=ServicesDb");

        var configuration = BuildWithSecretsDirectory();

        Assert.Equal("Host=db;Database=ServicesDb", configuration.GetConnectionString("ServicesDb"));
    }

    // The file source is added LAST, so it wins. Deliberate: a file can be mounted read-only and
    // owned by one user, where an environment variable is readable from /proc and inherited by every
    // child process.
    [Fact]
    public void AFileSecretOverridesAnEarlierSource()
    {
        WriteSecretFile("Jwt__Secret", "from-the-file-which-is-long-enough-here");

        var configuration = BuildWithSecretsDirectory(("Jwt:Secret", "from-appsettings"));

        Assert.Equal("from-the-file-which-is-long-enough-here", configuration["Jwt:Secret"]);
    }

    // A developer machine has no /run/secrets, and failing there would make the normal case the
    // broken one.
    [Fact]
    public void AMissingSecretsDirectoryIsNotAnError()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([
                new KeyValuePair<string, string?>(
                    SecretsConfigurationExtensions.SecretsDirectoryKey,
                    Path.Combine(Path.GetTempPath(), $"definitely-absent-{Guid.NewGuid():N}")),
            ])
            .AddDeploymentSecrets()
            .Build();

        Assert.Null(configuration["Jwt:Secret"]);
    }

    // ---- Reporting what is missing ----

    // THE POINT OF THE WHOLE EXERCISE. Before this, a customer found out what to supply by starting a
    // service, reading the exception AddPermissionsAuth threw on the first missing key, fixing it, and
    // repeating - because validation lived inside a registration call rather than up front.
    [Fact]
    public void EveryMissingSecretIsReportedAtOnce()
    {
        var configuration = BuildWithSecretsDirectory();

        var exception = Assert.Throws<SecretsMissingException>(
            () => configuration.RequireSecrets(Jwt(), Db()));

        Assert.Equal(2, exception.Problems.Count);
        Assert.Contains("Jwt:Secret", exception.Message);
        Assert.Contains("ConnectionStrings:ServicesDb", exception.Message);
    }

    // The message has to say what the value is for and both ways to supply it, or it is just a key
    // name the reader has to go and look up.
    [Fact]
    public void TheMessageNamesTheReasonAndBothSupplyMechanisms()
    {
        var configuration = BuildWithSecretsDirectory();

        var exception = Assert.Throws<SecretsMissingException>(() => configuration.RequireSecrets(Jwt()));

        Assert.Contains("signing and validating bearer tokens", exception.Message);
        Assert.Contains("Jwt__Secret", exception.Message);
        Assert.Contains("/run/secrets", exception.Message);
    }

    [Fact]
    public void NothingIsThrownWhenEverythingIsSupplied()
    {
        WriteSecretFile("Jwt__Secret", "a-genuinely-long-enough-signing-key-value");
        WriteSecretFile("ConnectionStrings__ServicesDb", "Host=db");

        var configuration = BuildWithSecretsDirectory();

        configuration.RequireSecrets(Jwt(), Db());
    }

    [Fact]
    public void AWhitespaceOnlyValueCountsAsMissing()
    {
        var configuration = BuildWithSecretsDirectory(("Jwt:Secret", "   "));

        var problem = Assert.Single(configuration.Inspect(Jwt()));
        Assert.Equal(SecretProblemKind.Missing, problem.Kind);
    }

    // A signing key below 32 bytes is not merely weak, it is below what HMAC-SHA256 needs - so this
    // is a correctness check as much as a strength one.
    [Fact]
    public void AShortSigningKeyIsReportedWithItsActualLength()
    {
        var configuration = BuildWithSecretsDirectory(("Jwt:Secret", "too-short"));

        var problem = Assert.Single(configuration.Inspect(Jwt()));
        Assert.Equal(SecretProblemKind.TooShort, problem.Kind);
        Assert.Contains("got 9", problem.Detail);
    }

    // THE FAILURE MOST LIKELY TO ACTUALLY HAPPEN, and the one every other check here waves through.
    //
    // appsettings.Development.json ships this exact value. It is 48 characters, so the length check
    // passes; it is non-empty, so the presence check passes. A customer copying that file into
    // production would get a service that starts cleanly and signs tokens with a secret anybody who
    // has seen the repository knows.
    [Fact]
    public void TheShippedDevelopmentSecretIsRejectedAsAPlaceholder()
    {
        var configuration = BuildWithSecretsDirectory(
            ("Jwt:Secret", "development-change-this-to-a-strong-32-char-secret"));

        var problem = Assert.Single(configuration.Inspect(Jwt()));
        Assert.Equal(SecretProblemKind.Placeholder, problem.Kind);
    }

    [Theory]
    [InlineData("please-change-this-before-deploying-anything")]
    [InlineData("CHANGEME-CHANGEME-CHANGEME-CHANGEME")]
    [InlineData("your-secret-goes-right-here-okay-then")]
    public void OtherUnreplacedPlaceholdersAreRejectedToo(string value)
    {
        var configuration = BuildWithSecretsDirectory(("Jwt:Secret", value));

        Assert.Equal(SecretProblemKind.Placeholder, Assert.Single(configuration.Inspect(Jwt())).Kind);
    }

    // A connection string gets the placeholder check too, and is checked against a requirement with
    // no length minimum - which is the realistic shape, since "long enough" means nothing for a
    // connection string.
    [Fact]
    public void APlaceholderConnectionStringIsRejected()
    {
        var configuration = BuildWithSecretsDirectory(
            ("ConnectionStrings:ServicesDb", "Host=db;Password=placeholder"));

        Assert.Equal(SecretProblemKind.Placeholder, Assert.Single(configuration.Inspect(Db())).Kind);
    }

    // Checks short-circuit in order, so a value that is both too short AND a placeholder is reported
    // as too short. Stated as a test because it looks like a gap and is not: either report leads the
    // reader to replace the value, which fixes both.
    [Fact]
    public void AShortPlaceholderIsReportedAsTooShortRatherThanBoth()
    {
        var configuration = BuildWithSecretsDirectory(("Jwt:Secret", "changeme"));

        Assert.Equal(SecretProblemKind.TooShort, Assert.Single(configuration.Inspect(Jwt())).Kind);
    }

    // The matcher is substring-based, so it has to not fire on a real generated secret. Random hex
    // and base64 are what a customer following the runbook will actually produce.
    [Theory]
    [InlineData("9f2b7c41a8e05d36b1fa4e8c27d905613ab7ee42c81d59f0a3b6c7d8e9f01234")]
    [InlineData("Zm9vYmFyYmF6cXV1eA09PT1hYmNkZWZnaGlqa2xtbm9wcXJzdHV2d3h5eg==")]
    [InlineData("Host=prod-db.internal;Database=ServicesDb;Username=app_runtime;Password=t7Qm2vXz")]
    public void ARealGeneratedSecretIsAccepted(string value)
    {
        var configuration = BuildWithSecretsDirectory(("Jwt:Secret", value));

        Assert.Empty(configuration.Inspect(Jwt()));
    }

    // Inspect is the non-throwing form, for a preflight command or a health endpoint that wants to
    // report rather than refuse to start.
    [Fact]
    public void InspectReportsWithoutThrowing()
    {
        var configuration = BuildWithSecretsDirectory();

        var problems = configuration.Inspect(Jwt(), Db());

        Assert.Equal(2, problems.Count);
        Assert.All(problems, problem => Assert.Equal(SecretProblemKind.Missing, problem.Kind));
    }

    [Fact]
    public void ARequirementDerivesBothItsSupplyNamesFromItsKey()
    {
        var requirement = SecretRequirement.Required("Leads:Categorizer:Claude:ApiKey", "LLM categorisation");

        Assert.Equal("Leads__Categorizer__Claude__ApiKey", requirement.EnvironmentVariableName);
        Assert.Equal("Leads__Categorizer__Claude__ApiKey", requirement.SecretFileName);
    }
}
