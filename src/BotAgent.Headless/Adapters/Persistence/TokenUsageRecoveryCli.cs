extern alias StorageModule;

using Microsoft.Data.Sqlite;
using Database = StorageModule::BotAgent.Adapters.Persistence.AppDatabase;

namespace BotAgent.Adapters.Persistence;

internal static class TokenUsageRecoveryCli
{
    internal static int Run(string[] args)
    {
        if (args.Length != 4 || args[0] != "--recover-token-usage" || args[2] != "--to-schema" || args[3] != "14")
        {
            Console.Error.WriteLine("usage: --recover-token-usage <database> --to-schema 14");
            return 2;
        }
        try
        {
            Database.RecoverTokenUsageToSchema14(args[1]);
            Console.WriteLine("{\"status\":\"recovered\",\"fromSchema\":15,\"toSchema\":14}");
            return 0;
        }
        catch (InvalidOperationException error) when (error.Message == "database_in_use")
        {
            Console.Error.WriteLine("database_in_use");
            return 3;
        }
        catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            // SQL and filesystem errors can contain identifiers; never echo them in recovery output.
            Console.Error.WriteLine("token_usage_recovery_refused");
            return 4;
        }
    }
}
