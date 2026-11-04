using Npgsql;

namespace Quicker.Persistence;

/// <summary>Builds the two data sources the system uses: the application role and the schema owner.</summary>
public static class DataSources
{
    public static NpgsqlDataSource ForApp(DbOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Build(options.AppConnection, "quicker-app");
    }

    public static NpgsqlDataSource ForOwner(DbOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Build(options.OwnerConnection, "quicker-owner");
    }

    private static NpgsqlDataSource Build(string connectionString, string applicationName)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.ConnectionStringBuilder.ApplicationName ??= applicationName;
        builder.ConnectionStringBuilder.IncludeErrorDetail = true;
        return builder.Build();
    }
}
