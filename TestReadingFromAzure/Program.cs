using Azure.Data.Tables;
using System;

class Program
{
    static void Main()
    {
        GetData("CpuUsage", (row) => $"CPU={row.GetDouble("CpuPercent")}");
        GetData("MemoryUsage", (row) => $"Mem={row.GetDouble("MemUsedMb")}/{row.GetDouble("MemTotalMb")}");
        GetData("PingTime", (row) => $"Ping={row.GetInt32("PingMs")}");
    }
    static void GetData(string tableName, Func<TableEntity, string> extractMessage)
    {
        var connectionString = "";
        var tableClient = new TableClient(connectionString, tableName);

        var machine = Environment.MachineName;
        Console.WriteLine($"Fetching last 10 rows for machine {machine}...");

        var query = tableClient.Query<TableEntity>(
            filter: $"PartitionKey eq '{machine}'"
        );

        int count = 0;
        foreach (TableEntity row in query)
        {
            Console.WriteLine($"{row.RowKey} | {extractMessage.Invoke(row)}");
            if (++count >= 10) break;
        }
    }
}
