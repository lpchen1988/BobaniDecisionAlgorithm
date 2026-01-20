using System.Text;
using System.Collections.Concurrent;
using Bobani.Engine;

var builder = WebApplication.CreateBuilder(args);

// Seeded engine so results are reproducible (change seed if you want variety)
builder.Services.AddSingleton(new DecisionEngine(seed: 12345));

// Service to store and manage moods (thread-safe)
var moodsService = new ConcurrentDictionary<string, MoodRow>();
var initialMoods = new List<MoodRow>
{
    new("Energy", 3, 2),
    new("Hangover", 2, 1),
    new("Boredom", 4, 3),
    new("Loneliness", 5, 4),
    new("Drunk", 0, 6),
    new("High", 1, 5),
    new("Satiaity", 3, 3),
    new("Enlightenment", 2, 2),
};
foreach (var mood in initialMoods)
{
    moodsService[mood.MoodName] = mood;
}

builder.Services.AddSingleton(moodsService);

var app = builder.Build();

// Get all moods (for editing)
app.MapGet("/api/moods", (ConcurrentDictionary<string, MoodRow> moods) =>
{
    return Results.Ok(moods.Values.OrderBy(m => m.MoodName).ToList());
});

// Update a mood's ranking
app.MapPut("/api/moods/{moodName}/ranking", async (string moodName, HttpRequest request, ConcurrentDictionary<string, MoodRow> moods) =>
{
    if (!moods.TryGetValue(moodName, out var existingMood))
    {
        return Results.NotFound($"Mood '{moodName}' not found");
    }

    using var reader = new StreamReader(request.Body);
    var body = await reader.ReadToEndAsync();
    if (!int.TryParse(body, out var ranking))
    {
        return Results.BadRequest("Invalid ranking value");
    }

    var updatedMood = new MoodRow(existingMood.MoodName, existingMood.CurrentMood, ranking);
    moods[moodName] = updatedMood;
    return Results.Ok(updatedMood);
});

// Get editable mood table page
app.MapGet("/edit-moods", () =>
{
    var html = RenderEditableMoodsTable();
    return Results.Content(html, "text/html");
});

List<MoodRow> GetMoodsTable(ConcurrentDictionary<string, MoodRow> moods) => moods.Values.ToList();

// Human-readable report page
app.MapGet("/", (DecisionEngine engine, ConcurrentDictionary<string, MoodRow> moods) =>
{
    var result = engine.SelectMood(GetMoodsTable(moods));
    var html = RenderReportHtml(result);
    return Results.Content(html, "text/html");
});

// Keep JSON endpoint for detailed debugging
app.MapGet("/step", (DecisionEngine engine, ConcurrentDictionary<string, MoodRow> moods) =>
{
    var result = engine.SelectMood(GetMoodsTable(moods));
    return Results.Ok(result);
});

app.Run();

static string RenderReportHtml(MoodSelectionResult r)
{
    string Row(MoodRow m) =>
        $"<tr><td>{Escape(m.MoodName)}</td><td>{m.CurrentMood:0.##}</td><td>{m.StartingRankMorning}</td></tr>";

    string Table(string title, List<MoodRow> rows)
    {
        if (rows.Count == 0)
            return $@"<div class='card'><h3>{title}</h3><div class='muted'>None</div></div>";

        var sb = new StringBuilder();
        sb.Append($@"<div class='card'><h3>{title}</h3><table><thead>
            <tr><th>Mood</th><th>Current Mood</th><th>Starting Rank (AM)</th></tr>
            </thead><tbody>");
        foreach (var m in rows) sb.Append(Row(m));
        sb.Append("</tbody></table></div>");
        return sb.ToString();
    }

    var modeLabel = r.SelectionMode == "OUTLIER" ? "Outlier path" : "IQR random path";

    var summary = $@"
      <div class='card'>
        <h3>Step 1 Summary</h3>
        <div class='grid2'>
          <div><b>Mode:</b> {modeLabel}</div>
          <div><b>Selected Mood:</b> <span class='pill'>{Escape(r.SelectedMood)}</span></div>
          <div><b>Q1:</b> {r.Q1:0.###}</div>
          <div><b>Q3:</b> {r.Q3:0.###}</div>
          <div><b>IQR:</b> {r.IQR:0.###}</div>
          <div><b>Lower Fence:</b> {r.LowerFence:0.###}</div>
        </div>
      </div>";

    var explanation = r.SelectionMode == "OUTLIER"
        ? $@"<div class='callout good'>
              <b>Outliers detected.</b> Bobani considered only the low outliers, then picked the top one by Starting Rank (AM).
            </div>"
        : $@"<div class='callout'>
              <b>No outliers detected.</b> Bobani picked randomly from the IQR pool (moods with Current Mood between Q1 and Q3).
            </div>";

    var sortedTable = Table("Sorted Moods (low → high)", r.SortedMoodsLowToHigh);
    var outliersTable = Table("Outliers (low)", r.OutliersLow);
    var topCandidatesTable = Table("Top Outlier Candidates (by Starting Rank AM)", r.TopOutlierCandidates);
    var iqrPoolTable = Table("IQR Pool (random pick group)", r.IqrPool);

    var html = $@"
<!doctype html>
<html>
<head>
  <meta charset='utf-8' />
  <meta name='viewport' content='width=device-width, initial-scale=1' />
  <title>Bobani Mood Debug</title>
  <style>
    body {{ font-family: ui-monospace, Menlo, Consolas, monospace; background:#0b0d12; color:#e9ecf1; padding:18px; }}
    a {{ color:#6ea8ff; }}
    .top {{ display:flex; align-items:center; justify-content:space-between; gap:12px; }}
    .card {{ background:#121624; border:1px solid #232b3d; border-radius:14px; padding:14px; margin:12px 0; }}
    table {{ width:100%; border-collapse:collapse; }}
    th, td {{ text-align:left; padding:8px; border-bottom:1px solid #232b3d; }}
    th {{ color:#a7b0c0; font-weight:600; }}
    .muted {{ color:#a7b0c0; }}
    .pill {{ display:inline-block; padding:3px 8px; border-radius:999px; border:1px solid #6ea8ff55; background:#1a2033; }}
    .grid2 {{ display:grid; grid-template-columns: 1fr 1fr; gap:8px 14px; margin-top:10px; }}
    .callout {{ border:1px solid #232b3d; background:#0f1422; border-radius:14px; padding:12px; }}
    .callout.good {{ border-color:#3aa67566; }}
    .btn {{ display:inline-block; padding:8px 10px; border-radius:12px; border:1px solid #232b3d; background:#1a2033; color:#e9ecf1; text-decoration:none; }}
    .btn:hover {{ border-color:#6ea8ff; }}
    .cols {{ display:grid; grid-template-columns: 1fr 1fr; gap:12px; }}
    @media (max-width: 900px) {{ .cols {{ grid-template-columns: 1fr; }} .grid2 {{ grid-template-columns: 1fr; }} }}
  </style>
</head>
<body>
  <div class='top'>
    <div>
      <h1 style='margin:0;'>Bobani Mood Debug</h1>
      <div class='muted'>Human-readable Step 1 output. JSON still at <a href='/step'>/step</a>. <a href='/edit-moods'>Edit mood rankings</a></div>
    </div>
    <a class='btn' href='/'>Run Again</a>
  </div>

  {summary}
  {explanation}

  <div class='cols'>
    {sortedTable}
    {outliersTable}
  </div>

  <div class='cols'>
    {topCandidatesTable}
    {iqrPoolTable}
  </div>
</body>
</html>";

    return html;
}

static string Escape(string s) =>
    s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

static string RenderEditableMoodsTable()
{
    var html = $@"
<!doctype html>
<html>
<head>
  <meta charset='utf-8' />
  <meta name='viewport' content='width=device-width, initial-scale=1' />
  <title>Edit Mood Rankings</title>
  <style>
    body {{ font-family: ui-monospace, Menlo, Consolas, monospace; background:#0b0d12; color:#e9ecf1; padding:18px; }}
    a {{ color:#6ea8ff; text-decoration:none; }}
    a:hover {{ text-decoration:underline; }}
    .top {{ display:flex; align-items:center; justify-content:space-between; gap:12px; margin-bottom:20px; }}
    .card {{ background:#121624; border:1px solid #232b3d; border-radius:14px; padding:14px; margin:12px 0; }}
    table {{ width:100%; border-collapse:collapse; }}
    th, td {{ text-align:left; padding:8px; border-bottom:1px solid #232b3d; }}
    th {{ color:#a7b0c0; font-weight:600; }}
    .muted {{ color:#a7b0c0; }}
    .btn {{ display:inline-block; padding:8px 10px; border-radius:12px; border:1px solid #232b3d; background:#1a2033; color:#e9ecf1; text-decoration:none; cursor:pointer; }}
    .btn:hover {{ border-color:#6ea8ff; }}
    .btn-primary {{ background:#6ea8ff; border-color:#6ea8ff; color:#0b0d12; }}
    .btn-primary:hover {{ background:#5d9eff; }}
    input[type='number'] {{ background:#1a2033; border:1px solid #232b3d; color:#e9ecf1; padding:6px; border-radius:6px; width:60px; }}
    input[type='number']:focus {{ outline:none; border-color:#6ea8ff; }}
    .save-status {{ margin-top:12px; padding:8px; border-radius:6px; display:none; }}
    .save-status.success {{ background:#3aa67533; border:1px solid #3aa67566; color:#3aa675; }}
    .save-status.error {{ background:#aa3a3a33; border:1px solid #aa3a3a66; color:#aa3a3a; }}
  </style>
</head>
<body>
  <div class='top'>
    <div>
      <h1 style='margin:0;'>Edit Mood Rankings</h1>
      <div class='muted'>Update the Starting Rank (AM) for each mood. Lower numbers = higher priority.</div>
    </div>
    <div>
      <a class='btn' href='/'>Decision Dashboard</a>
      <button class='btn btn-primary' onclick='saveAll()'>Save All Changes</button>
    </div>
  </div>

  <div class='card'>
    <table id='moodsTable'>
      <thead>
        <tr>
          <th>Mood</th>
          <th>Current Mood</th>
          <th>Starting Rank (AM)</th>
        </tr>
      </thead>
      <tbody id='moodsTableBody'>
        <tr><td colspan='3'>Loading...</td></tr>
      </tbody>
    </table>
    <div id='saveStatus' class='save-status'></div>
  </div>

  <script>
    let moods = [];

    async function loadMoods() {{
      try {{
        const response = await fetch('/api/moods');
        moods = await response.json();
        renderTable();
      }} catch (error) {{
        console.error('Error loading moods:', error);
        document.getElementById('moodsTableBody').innerHTML = '<tr><td colspan=""3"">Error loading moods</td></tr>';
      }}
    }}

    function renderTable() {{
      const tbody = document.getElementById('moodsTableBody');
      tbody.innerHTML = moods.map(mood => `
        <tr>
          <td>${{escapeHtml(mood.moodName)}}</td>
          <td>${{mood.currentMood}}</td>
          <td>
            <input type='number' 
                   id='rank_${{mood.moodName}}' 
                   value='${{mood.startingRankMorning}}' 
                   min='1' 
                   data-original='${{mood.startingRankMorning}}'
                   onchange='markChanged(this)' />
          </td>
        </tr>
      `).join('');
    }}

    function markChanged(input) {{
      const original = parseInt(input.dataset.original);
      const current = parseInt(input.value);
      if (current !== original) {{
        input.style.borderColor = '#6ea8ff';
      }} else {{
        input.style.borderColor = '#232b3d';
      }}
    }}

    async function saveAll() {{
      const saveStatus = document.getElementById('saveStatus');
      saveStatus.style.display = 'none';
      
      const inputs = document.querySelectorAll('input[type=""number""]');
      const updates = [];
      
      for (const input of inputs) {{
        const moodName = input.id.replace('rank_', '');
        const newRanking = parseInt(input.value);
        const original = parseInt(input.dataset.original);
        
        if (newRanking !== original) {{
          updates.push({{ moodName, newRanking }});
        }}
      }}

      if (updates.length === 0) {{
        showStatus('No changes to save', 'success');
        return;
      }}

      try {{
        const promises = updates.map(update => 
          fetch(`/api/moods/${{update.moodName}}/ranking`, {{
            method: 'PUT',
            headers: {{ 'Content-Type': 'application/json' }},
            body: JSON.stringify(update.newRanking)
          }})
        );

        await Promise.all(promises);
        
        // Update original values
        updates.forEach(update => {{
          const input = document.getElementById(`rank_${{update.moodName}}`);
          input.dataset.original = update.newRanking;
          input.style.borderColor = '#232b3d';
        }});

        showStatus(`Successfully updated ${{updates.length}} mood(s)`, 'success');
        
        // Reload moods to get updated data
        await loadMoods();
      }} catch (error) {{
        console.error('Error saving:', error);
        showStatus('Error saving changes. Please try again.', 'error');
      }}
    }}

    function showStatus(message, type) {{
      const saveStatus = document.getElementById('saveStatus');
      saveStatus.textContent = message;
      saveStatus.className = `save-status ${{type}}`;
      saveStatus.style.display = 'block';
      
      if (type === 'success') {{
        setTimeout(() => {{
          saveStatus.style.display = 'none';
        }}, 3000);
      }}
    }}

    function escapeHtml(text) {{
      const div = document.createElement('div');
      div.textContent = text;
      return div.innerHTML;
    }}

    // Load moods on page load
    loadMoods();
  </script>
</body>
</html>";

    return html;
}

