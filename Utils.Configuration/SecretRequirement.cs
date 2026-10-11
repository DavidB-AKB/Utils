namespace Utils.Configuration;

// One secret a deployment has to supply, and what breaks without it.
//
// The `why` is not decoration. The whole problem this solves is a customer being handed a service
// that refuses to start with a message naming a configuration key and nothing else - so the message
// has to say what the value is for, and both of the ways it can be supplied.
public sealed record SecretRequirement
{
    private SecretRequirement(string configurationKey, string why, int minimumLength)
    {
        ConfigurationKey = configurationKey;
        Why = why;
        MinimumLength = minimumLength;
    }

    // Colon-separated, as .NET configuration keys are: "Jwt:Secret", "ConnectionStrings:ServicesDb".
    public string ConfigurationKey { get; }

    public string Why { get; }

    // Zero means "any non-empty value". Used for keys where a short value is not merely weak but
    // actively broken - an HMAC signing key below 32 bytes, for instance.
    public int MinimumLength { get; }

    public static SecretRequirement Required(string configurationKey, string why, int minimumLength = 0) =>
        new(configurationKey, why, minimumLength);

    // The environment variable that supplies this key. .NET maps "__" to ":" in environment
    // variable names, which is why the convention is Jwt__Secret rather than Jwt:Secret - a colon is
    // not a legal environment variable character on every platform.
    public string EnvironmentVariableName => ConfigurationKey.Replace(":", "__");

    // The file that supplies this key under the secrets directory. Same name as the environment
    // variable, by the same "__" convention - see SecretsConfigurationExtensions.
    public string SecretFileName => EnvironmentVariableName;
}
