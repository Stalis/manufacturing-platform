namespace Tests.Shared;

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute(params string[] requiredVariables)
    {
        foreach (var variable in requiredVariables)
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(variable)))
            {
                Skip = $"Set {variable} to disposable test infrastructure (PostgreSQL requires CREATEDB permission).";
                break;
            }
    }
}
