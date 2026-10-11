using Microsoft.Extensions.Configuration;

namespace Utils.Configuration;

// How a deployment supplies secrets, without assuming a cloud.
//
// THE PROVIDER-AGNOSTIC MECHANISM IS ALREADY IN THE FRAMEWORK, and that is the point of this file
// being thin. Environment variables work everywhere, and `AddKeyPerFile` reads a directory of
// one-value-per-file secrets - which is exactly how Docker Compose/Swarm secrets, Kubernetes secret
// volumes and Nomad all deliver them. Both normalise "__" to ":", so a single name serves either:
//
//     Jwt__Secret                 as an environment variable
//     /run/secrets/Jwt__Secret    as a file containing only the value
//
// So there was nothing to invent. What was missing is the second half: a customer handed these
// services could not find out what to supply except by starting one, reading the exception, fixing
// it, and repeating - because AddPermissionsAuth throws on the first missing key from deep inside a
// registration call. RequireSecrets reports EVERYTHING missing at once, up front.
//
// FILES ARE PREFERRED OVER ENVIRONMENT VARIABLES for anything genuinely secret, which is why this
// adds the file source LAST (highest precedence). An environment variable is readable from
// /proc/<pid>/environ, is inherited by every child process, and tends to appear in crash dumps and
// process listings; a file can be mounted read-only and owned by one user. Environment variables
// remain supported because they are the only mechanism some hosts offer.
public static class SecretsConfigurationExtensions
{
    // Where Docker and Kubernetes both mount secrets by default.
    public const string DefaultSecretsDirectory = "/run/secrets";

    // Overridable so a deployment that mounts elsewhere needs no code change, and so a developer can
    // point it at a local directory.
    public const string SecretsDirectoryKey = "Secrets:Directory";

    // Adds the file-per-secret source on top of whatever the host already configured.
    //
    // OPTIONAL, deliberately: a developer machine has no /run/secrets, and failing there would make
    // the normal case the broken one. Absence of the directory is not a missing-secret problem -
    // RequireSecrets is what reports that, and it reports it the same way regardless of which source
    // the value was meant to come from.
    public static IConfigurationBuilder AddDeploymentSecrets(this IConfigurationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Read from what is configured so far, so Secrets:Directory can itself come from an
        // environment variable or appsettings.
        var directory = builder.Build()[SecretsDirectoryKey] ?? DefaultSecretsDirectory;

        return builder.AddKeyPerFile(directory, optional: true);
    }

    // Validates every requirement and throws ONCE, listing all of them.
    //
    // Call this from the host before AddXServices, so the report covers the whole combined host
    // rather than stopping at whichever service happened to be registered first.
    public static IConfiguration RequireSecrets(
        this IConfiguration configuration, params SecretRequirement[] requirements)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(requirements);

        var problems = Inspect(configuration, requirements);
        if (problems.Count > 0)
        {
            throw new SecretsMissingException(problems);
        }

        return configuration;
    }

    // The same check without throwing - for a health endpoint or a preflight command that wants to
    // report rather than refuse to start.
    public static IReadOnlyList<SecretProblem> Inspect(
        this IConfiguration configuration, params SecretRequirement[] requirements)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(requirements);

        var problems = new List<SecretProblem>();

        foreach (var requirement in requirements)
        {
            var value = configuration[requirement.ConfigurationKey];

            if (string.IsNullOrWhiteSpace(value))
            {
                problems.Add(new SecretProblem(requirement, SecretProblemKind.Missing, null));
                continue;
            }

            if (requirement.MinimumLength > 0 && value.Length < requirement.MinimumLength)
            {
                problems.Add(new SecretProblem(
                    requirement,
                    SecretProblemKind.TooShort,
                    $"needs at least {requirement.MinimumLength} characters, got {value.Length}"));
                continue;
            }

            // THE FAILURE MOST LIKELY TO ACTUALLY HAPPEN, and the one a length check waves through:
            // appsettings.Development.json ships a Jwt:Secret of
            // "development-change-this-to-a-strong-32-char-secret", which is 48 characters and passes
            // every other test here. A customer copying that file into production gets a service that
            // starts cleanly and signs tokens with a secret published in a public-ish repository.
            if (LooksLikeAPlaceholder(value) is { } marker)
            {
                problems.Add(new SecretProblem(
                    requirement,
                    SecretProblemKind.Placeholder,
                    $"still contains '{marker}', so it is the example value rather than a real one"));
            }
        }

        return problems;
    }

    // Substring matching rather than an exact list of known placeholders: the point is to catch a
    // value nobody replaced, and the phrasings that show up are all some variation of telling the
    // reader to change it. A real generated secret - random bytes, base64 or hex - will not contain
    // any of these, so the false-positive risk is negligible against the failure it prevents.
    private static string? LooksLikeAPlaceholder(string value)
    {
        string[] markers =
        [
            "change-this", "changeme", "change-me", "replace-this", "replaceme",
            "development-", "example.com", "your-secret", "placeholder", "xxxxx",
        ];

        return markers.FirstOrDefault(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }
}

public enum SecretProblemKind
{
    Missing,
    TooShort,
    Placeholder,
}

public sealed record SecretProblem(SecretRequirement Requirement, SecretProblemKind Kind, string? Detail);

// One exception listing every problem, rather than one exception per problem discovered in sequence.
//
// The message is the deliverable here. A customer sees what to supply, why each value matters, and
// both ways to provide it - once - instead of discovering them one restart at a time.
public sealed class SecretsMissingException : Exception
{
    public SecretsMissingException(IReadOnlyList<SecretProblem> problems)
        : base(BuildMessage(problems))
    {
        Problems = problems;
    }

    public IReadOnlyList<SecretProblem> Problems { get; }

    private static string BuildMessage(IReadOnlyList<SecretProblem> problems)
    {
        var lines = new List<string>
        {
            $"{problems.Count} configuration value(s) must be supplied before this service can start:",
            string.Empty,
        };

        foreach (var problem in problems)
        {
            var requirement = problem.Requirement;
            var what = problem.Kind switch
            {
                SecretProblemKind.Missing => "is not set",
                SecretProblemKind.TooShort => $"is too short ({problem.Detail})",
                SecretProblemKind.Placeholder => $"is a placeholder ({problem.Detail})",
                _ => "is invalid",
            };

            lines.Add($"  {requirement.ConfigurationKey} {what}.");
            lines.Add($"      needed for: {requirement.Why}");
            lines.Add($"      supply as:  environment variable {requirement.EnvironmentVariableName}");
            lines.Add($"             or:  a file {requirement.SecretFileName} in the secrets directory "
                      + $"(default {SecretsConfigurationExtensions.DefaultSecretsDirectory}, "
                      + $"override with {SecretsConfigurationExtensions.SecretsDirectoryKey})");
            lines.Add(string.Empty);
        }

        lines.Add("A file is preferred over an environment variable for anything secret: an");
        lines.Add("environment variable is readable from /proc/<pid>/environ, is inherited by child");
        lines.Add("processes, and tends to show up in crash dumps.");

        return string.Join(Environment.NewLine, lines);
    }
}
