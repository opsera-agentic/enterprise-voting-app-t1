using System;
using System.Data.Common;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Npgsql;
using StackExchange.Redis;
using Amazon.RDS.Util;

namespace Worker
{
    public class Program
    {
        public static int Main(string[] args)
        {
            try
            {
                // Configuration from environment variables
                var dbHost = Environment.GetEnvironmentVariable("DATABASE_HOST") ?? "db";
                var dbPort = int.Parse(Environment.GetEnvironmentVariable("DATABASE_PORT") ?? "5432");
                var dbUser = Environment.GetEnvironmentVariable("DATABASE_USER") ?? "postgres";
                var dbName = Environment.GetEnvironmentVariable("DATABASE_NAME") ?? "votes";
                var useIamAuth = Environment.GetEnvironmentVariable("DATABASE_USE_IAM_AUTH") == "true";
                var awsRegion = Environment.GetEnvironmentVariable("AWS_REGION") ?? "us-west-2";

                var redisHost = Environment.GetEnvironmentVariable("REDIS_HOST") ?? "redis";
                var redisPort = int.Parse(Environment.GetEnvironmentVariable("REDIS_PORT") ?? "6379");

                // Build connection string
                string dbConnectionString;
                if (useIamAuth)
                {
                    // Generate IAM auth token
                    var authToken = RDSAuthTokenGenerator.GenerateAuthToken(
                        Amazon.RegionEndpoint.GetBySystemName(awsRegion),
                        dbHost,
                        dbPort,
                        dbUser
                    );
                    Console.WriteLine($"Using IAM authentication for RDS: {dbHost}:{dbPort}");
                    dbConnectionString = $"Server={dbHost};Port={dbPort};Username={dbUser};Password={authToken};Database={dbName};SSL Mode=Require;Trust Server Certificate=true";
                }
                else
                {
                    var dbPassword = Environment.GetEnvironmentVariable("DATABASE_PASSWORD") ?? "postgres";
                    Console.WriteLine("Using password authentication for database");
                    dbConnectionString = $"Server={dbHost};Port={dbPort};Username={dbUser};Password={dbPassword};Database={dbName}";
                }

                var pgsql = OpenDbConnection(dbConnectionString);
                var redisConn = OpenRedisConnection(redisHost, redisPort);
                var redis = redisConn.GetDatabase();

                // Keep alive is not implemented in Npgsql yet
                var keepAliveCommand = pgsql.CreateCommand();
                keepAliveCommand.CommandText = "SELECT 1";

                var definition = new { vote = "", voter_id = "" };

                // Track when we need to refresh the IAM token (every 10 minutes)
                var lastTokenRefresh = DateTime.UtcNow;
                var tokenRefreshInterval = TimeSpan.FromMinutes(10);

                while (true)
                {
                    Thread.Sleep(100);

                    // Reconnect redis if down
                    if (redisConn == null || !redisConn.IsConnected)
                    {
                        Console.WriteLine("Reconnecting Redis");
                        redisConn = OpenRedisConnection(redisHost, redisPort);
                        redis = redisConn.GetDatabase();
                    }

                    string json = redis.ListLeftPopAsync("votes").Result;
                    if (json != null)
                    {
                        var vote = JsonConvert.DeserializeAnonymousType(json, definition);
                        Console.WriteLine($"Processing vote for '{vote.vote}' by '{vote.voter_id}'");

                        // Check if we need to refresh the DB connection (for IAM auth token refresh)
                        if (useIamAuth && DateTime.UtcNow - lastTokenRefresh > tokenRefreshInterval)
                        {
                            Console.WriteLine("Refreshing IAM auth token...");
                            pgsql.Close();
                            var newToken = RDSAuthTokenGenerator.GenerateAuthToken(
                                Amazon.RegionEndpoint.GetBySystemName(awsRegion),
                                dbHost,
                                dbPort,
                                dbUser
                            );
                            dbConnectionString = $"Server={dbHost};Port={dbPort};Username={dbUser};Password={newToken};Database={dbName};SSL Mode=Require;Trust Server Certificate=true";
                            pgsql = OpenDbConnection(dbConnectionString);
                            keepAliveCommand = pgsql.CreateCommand();
                            keepAliveCommand.CommandText = "SELECT 1";
                            lastTokenRefresh = DateTime.UtcNow;
                        }

                        // Reconnect DB if down
                        if (!pgsql.State.Equals(System.Data.ConnectionState.Open))
                        {
                            Console.WriteLine("Reconnecting DB");
                            pgsql = OpenDbConnection(dbConnectionString);
                        }
                        else
                        {
                            UpdateVote(pgsql, vote.voter_id, vote.vote);
                        }
                    }
                    else
                    {
                        keepAliveCommand.ExecuteNonQuery();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.ToString());
                return 1;
            }
        }

        private static NpgsqlConnection OpenDbConnection(string connectionString)
        {
            NpgsqlConnection connection;

            while (true)
            {
                try
                {
                    connection = new NpgsqlConnection(connectionString);
                    connection.Open();
                    break;
                }
                catch (SocketException)
                {
                    Console.Error.WriteLine("Waiting for db");
                    Thread.Sleep(1000);
                }
                catch (DbException ex)
                {
                    Console.Error.WriteLine($"Waiting for db: {ex.Message}");
                    Thread.Sleep(1000);
                }
            }

            Console.Error.WriteLine("Connected to db");

            var command = connection.CreateCommand();
            command.CommandText = @"CREATE TABLE IF NOT EXISTS votes (
                                        id VARCHAR(255) NOT NULL UNIQUE,
                                        vote VARCHAR(255) NOT NULL
                                    )";
            command.ExecuteNonQuery();

            return connection;
        }

        private static ConnectionMultiplexer OpenRedisConnection(string hostname, int port = 6379)
        {
            var connectionString = $"{hostname}:{port}";
            Console.WriteLine($"Connecting to redis at {connectionString}");

            while (true)
            {
                try
                {
                    Console.Error.WriteLine("Connecting to redis");
                    return ConnectionMultiplexer.Connect(connectionString);
                }
                catch (RedisConnectionException)
                {
                    Console.Error.WriteLine("Waiting for redis");
                    Thread.Sleep(1000);
                }
            }
        }

        private static void UpdateVote(NpgsqlConnection connection, string voterId, string vote)
        {
            var command = connection.CreateCommand();
            try
            {
                command.CommandText = "INSERT INTO votes (id, vote) VALUES (@id, @vote)";
                command.Parameters.AddWithValue("@id", voterId);
                command.Parameters.AddWithValue("@vote", vote);
                command.ExecuteNonQuery();
            }
            catch (DbException)
            {
                command.CommandText = "UPDATE votes SET vote = @vote WHERE id = @id";
                command.ExecuteNonQuery();
            }
            finally
            {
                command.Dispose();
            }
        }
    }
}
