using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace PawPrints.Api;

internal static class DatabaseMigrationDiagnostics
{
    /// <summary>
    /// Logs the full exception chain and SQL Client–specific details for production debugging (compact JSON friendly).
    /// </summary>
    public static void LogMigrationFailure(ILogger logger, Exception exception)
    {
        logger.LogError(
            exception,
            "Database migration failed; API requests will return 503 until this is resolved. Root type: {RootType}, message: {RootMessage}",
            exception.GetType().FullName,
            exception.Message
        );

        var depth = 0;
        for (var ex = exception; ex != null; ex = ex.InnerException, depth++)
        {
            if (depth > 0)
            {
                logger.LogError(
                    "Migration failure inner chain depth {Depth}: {Type} — {Message}",
                    depth,
                    ex.GetType().FullName,
                    ex.Message
                );
            }

            switch (ex)
            {
                case SqlException sqlEx:
                    LogSqlException(logger, sqlEx);
                    break;
                case IOException ioEx when ioEx.InnerException is SocketException sockEx:
                    logger.LogError(
                        "Underlying socket error during SQL connection: {SocketErrorCode} ({SocketError}) — {Message}",
                        sockEx.SocketErrorCode,
                        (int)sockEx.SocketErrorCode,
                        sockEx.Message
                    );
                    break;
            }
        }
    }

    private static void LogSqlException(ILogger logger, SqlException sqlEx)
    {
        logger.LogError(
            "SqlException summary: Number={Number}, State={State}, Class={Class}, Server={Server}, ClientConnectionId={ClientConnectionId}",
            sqlEx.Number,
            sqlEx.State,
            sqlEx.Class,
            sqlEx.Server,
            sqlEx.ClientConnectionId
        );

        for (var i = 0; i < sqlEx.Errors.Count; i++)
        {
            var err = sqlEx.Errors[i];
            logger.LogError(
                "SqlError[{Index}] Number={Number} State={State} Class={Class} Procedure={Procedure} LineNumber={LineNumber}: {Message}",
                i,
                err.Number,
                err.State,
                err.Class,
                err.Procedure ?? "",
                err.LineNumber,
                err.Message
            );
        }
    }
}
