using Azure;
using Azure.Data.Tables;

var builder = WebApplication.CreateBuilder(args);

// --- CONFIG ---
var connectionString =
    Environment.GetEnvironmentVariable("AZURE_STORAGE_CONNECTION_STRING")
    ?? ""; // Set via env var/app setting in production
// --------------

var app = builder.Build();

// Health
app.MapGet("/api/health", () => Results.Ok(new { ok = true, time = DateTimeOffset.UtcNow }));

// List tables for the dropdown
app.MapGet("/api/tables", async () =>
{
    if (string.IsNullOrWhiteSpace(connectionString))
        return Results.Problem("Storage connection string is not configured.", statusCode: 500);

    var svc = new TableServiceClient(connectionString);
    var names = new List<string>();
    await foreach (var t in svc.QueryAsync()) names.Add(t.Name);
    names.Sort(StringComparer.OrdinalIgnoreCase);
    return Results.Ok(names);
});

// Return dynamic column list for a table (optionally for a specific machine/partition)
app.MapGet("/api/schema/{table}", async (string table, string? machine, int? sample) =>
{
    if (string.IsNullOrWhiteSpace(connectionString))
        return Results.Problem("Storage connection string is not configured.", statusCode: 500);

    var client = new TableClient(connectionString, table);
    var take = Math.Clamp(sample ?? 50, 1, 500);
    string? filter = null;

    if (!string.IsNullOrWhiteSpace(machine))
    {
        var safeMachine = machine.Replace("'", "''");
        filter = $"PartitionKey eq '{safeMachine}'";
    }

    var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    try
    {
        await foreach (var page in client.QueryAsync<TableEntity>(filter).AsPages(pageSizeHint: take))
        {
            foreach (var e in page.Values)
            {
                // dynamic properties
                foreach (var kv in e)
                    keys.Add(kv.Key);
                // system fields
                keys.Add("PartitionKey");
                keys.Add("RowKey");
                keys.Add("Timestamp");
            }
            break; // only first page is enough for schema
        }

        // Preferred ordering per known tables; others fall back to sensible defaults
        string[] preferred = table.ToLowerInvariant() switch
        {
            "cpuusage" => new[] { "Timestamp", "CpuPercent", "PartitionKey", "RowKey" },
            "memoryusage" => new[] { "Timestamp", "MemUsedMb", "MemTotalMb", "PartitionKey", "RowKey" },
            "pingtime" => new[] { "Timestamp", "Host", "PingMs", "PartitionKey", "RowKey" },
            _ => new[] { "Timestamp", "PartitionKey", "RowKey" }
        };

        // Order: preferred (if present) first, then the rest alphabetically
        var rest = keys.Except(preferred, StringComparer.OrdinalIgnoreCase)
                       .OrderBy(k => k, StringComparer.OrdinalIgnoreCase);
        var ordered = preferred.Where(keys.Contains).Concat(rest).ToArray();

        return Results.Ok(ordered);
    }
    catch (RequestFailedException ex)
    {
        return Results.Problem(ex.Message, statusCode: 500);
    }
});

// Paged history (returns rows as dictionaries so columns are fully dynamic)
// GET /api/metrics/{table}/{machine}/page?take=50&ct=<token>
app.MapGet("/api/metrics/{table}/{machine}/page", async (string table, string machine, int? take, string? ct) =>
{
    if (string.IsNullOrWhiteSpace(connectionString))
        return Results.Problem("Storage connection string is not configured.", statusCode: 500);

    var client = new TableClient(connectionString, table);
    var pageSize = Math.Clamp(take ?? 25, 1, 500);
    var safeMachine = machine.Replace("'", "''");
    var filter = $"PartitionKey eq '{safeMachine}'";

    try
    {
        var pages = client.QueryAsync<TableEntity>(filter).AsPages(ct, pageSize);

        await foreach (var p in pages)
        {
            var items = p.Values.Select(ToDict).ToList();
            return Results.Ok(new { items, continuationToken = p.ContinuationToken });
        }

        return Results.Ok(new { items = Array.Empty<object>(), continuationToken = (string?)null });
    }
    catch (RequestFailedException ex)
    {
        return Results.Problem(ex.Message, statusCode: 500);
    }
});

// Convert TableEntity -> dictionary (dynamic)
static IDictionary<string, object?> ToDict(TableEntity e)
{
    var d = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    foreach (var kv in e) d[kv.Key] = kv.Value;
    d["PartitionKey"] = e.PartitionKey;
    d["RowKey"] = e.RowKey;
    d["Timestamp"] = e.Timestamp;
    return d;
}

// ----------------- UI -----------------
app.MapGet("/", () => Results.Content(@"
<!doctype html>
<html>
<head>
  <meta charset='utf-8'/>
  <meta name='viewport' content='width=device-width, initial-scale=1'/>
  <title>Metrics Browser</title>
  <style>
    :root { font-family: ui-sans-serif, system-ui, -apple-system, Segoe UI, Roboto, Arial; }
    body { margin: 0; padding: 24px; background:#0b1020; color:#e9edf5; }
    .card { max-width: 1100px; margin: 0 auto; background:#131a33; border-radius:16px; padding:24px; box-shadow: 0 10px 30px rgba(0,0,0,.35); }
    h1 { margin:0 0 16px; font-size:22px; font-weight:800; }
    .row { display:flex; flex-wrap:wrap; gap:12px; margin:10px 0 16px; align-items:center; }
    input, button, select { padding:10px 12px; border-radius:10px; border:1px solid #2a365f; background:#0f1530; color:#e9edf5; }
    button { cursor:pointer; }
    table { width:100%; border-collapse:collapse; background:#0f1530; border-radius:12px; overflow:hidden; }
    th, td { padding:10px 12px; border-bottom:1px solid #2a365f; font-size:14px; }
    th { text-align:left; background:#0c1229; position:sticky; top:0; }
    .muted { color:#9fb0d4; font-size:13px; }
    .controls { display:flex; gap:10px; align-items:center; }
    .pill { padding:6px 10px; background:#0c1229; border-radius:999px; }
  </style>
</head>
<body>
  <div class='card'>
    <h1>Metrics Browser</h1>
    <div class='row'>
      <label>Table: <select id='tableSel'></select></label>
      <input id='machine' placeholder='Machine name (PartitionKey)'/>
      <label>Page size: <input id='take' type='number' min='1' max='500' value='25' style='width:80px'/></label>
      <button id='load'>Load</button>
      <div class='controls'>
        <button id='prev' disabled>Prev</button>
        <button id='next' disabled>Next</button>
        <span class='pill' id='pageInfo'>Page 0</span>
      </div>
    </div>
    <div id='status' class='muted'>Pick a table & enter machine name, then click Load.</div>

    <div style='overflow:auto; max-height:70vh; margin-top:12px'>
      <table id='grid'>
        <thead><tr id='hdr'></tr></thead>
        <tbody id='rows'></tbody>
      </table>
    </div>
  </div>

<script>
  const status   = document.getElementById('status');
  const tableSel = document.getElementById('tableSel');
  const machine  = document.getElementById('machine');
  const take     = document.getElementById('take');
  const loadBtn  = document.getElementById('load');
  const prevBtn  = document.getElementById('prev');
  const nextBtn  = document.getElementById('next');
  const pageInfo = document.getElementById('pageInfo');
  const hdr      = document.getElementById('hdr');
  const rows     = document.getElementById('rows');

  // pagination state
  let tokens = [null];
  let pageIndex = 0;
  let lastNextToken = null;

  // schema/columns for the current table
  let columns = [];

  async function listTables() {
    const res = await fetch('/api/tables');
    const names = await res.json();
    tableSel.innerHTML = '';
    for (const n of names) {
      const opt = document.createElement('option');
      opt.value = n; opt.textContent = n;
      tableSel.appendChild(opt);
    }
    // try to preselect
    for (const want of ['CpuUsage','MemoryUsage','PingTime']) {
      const o = [...tableSel.options].find(x => x.value.toLowerCase() === want.toLowerCase());
      if (o) { tableSel.value = o.value; break; }
    }
  }

  function setBusy(msg){ status.textContent = msg || 'Loading…'; }
  function setReady(ok){ if(ok) status.textContent='OK'; }

  function buildHeader() {
    hdr.innerHTML = '';
    for (const c of columns) {
      const th = document.createElement('th');
      th.textContent = c;
      hdr.appendChild(th);
    }
  }

  function render(items) {
    rows.innerHTML = '';
    for (const it of items) {
      const tr = document.createElement('tr');
      for (const c of columns) {
        const td = document.createElement('td');
        let v = it[c];
        if (c === 'Timestamp' && v) v = new Date(v).toLocaleString();
        if (v === null || v === undefined) v = '';
        td.textContent = (typeof v === 'number' && !Number.isInteger(v)) ? v.toFixed(2) : v;
        tr.appendChild(td);
      }
      rows.appendChild(tr);
    }
  }

  async function fetchSchema() {
    const tbl = tableSel.value;
    const m = machine.value.trim();
    const url = m ? `/api/schema/${encodeURIComponent(tbl)}?machine=${encodeURIComponent(m)}` 
                  : `/api/schema/${encodeURIComponent(tbl)}`;
    const res = await fetch(url);
    columns = await res.json();
    buildHeader();
  }

  async function fetchPage(idx) {
    const tbl = tableSel.value;
    const m = machine.value.trim();
    const takeNum = Math.max(1, Math.min(500, parseInt(take.value || '25', 10)));
    if (!tbl || !m) { status.textContent = 'Please choose table and enter machine name.'; return; }

    setBusy('Loading…');
    const ct = tokens[idx];
    const url = `/api/metrics/${encodeURIComponent(tbl)}/${encodeURIComponent(m)}/page?take=${takeNum}` + (ct ? `&ct=${encodeURIComponent(ct)}` : '');
    const res = await fetch(url);
    if (!res.ok) { status.textContent = 'Error ' + res.status; return; }
    const data = await res.json();
    render(data.items || []);
    lastNextToken = data.continuationToken || null;

    prevBtn.disabled = idx === 0;
    nextBtn.disabled = !lastNextToken;
    pageInfo.textContent = 'Page ' + idx;

    setReady(true);
  }

  prevBtn.addEventListener('click', async () => {
    if (pageIndex === 0) return;
    pageIndex--;
    await fetchPage(pageIndex);
  });

  nextBtn.addEventListener('click', async () => {
    if (!lastNextToken) return;
    tokens[pageIndex + 1] = lastNextToken;
    pageIndex++;
    await fetchPage(pageIndex);
  });

  loadBtn.addEventListener('click', async () => {
    tokens = [null]; pageIndex = 0; lastNextToken = null;
    await fetchSchema();
    await fetchPage(0);
  });

  tableSel.addEventListener('change', async () => {
    tokens = [null]; pageIndex = 0; lastNextToken = null;
    await fetchSchema();
    rows.innerHTML = '';
    pageInfo.textContent = 'Page 0';
    status.textContent = 'Pick machine and click Load.';
  });

  listTables().then(() => machine.focus());
</script>
</body>
</html>
", "text/html"));

app.Run();
