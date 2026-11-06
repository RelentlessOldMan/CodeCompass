<#
.SYNOPSIS
  How many tokens an agent spends answering the same code questions with CodeCompass and with only the built-in
  tools - the numbers behind the docs' token comparison table, on any repo, reproducibly.

.DESCRIPTION
  The questions come from the repo itself: the names `codecompass selftest` picks, with the answers it checked
  against the files. For each one it asks a fresh headless Claude Code session (`claude -p`) in two arms:

    codecompass  the built-in tools (-BuiltinTools) plus the CodeCompass MCP server, with the plugin's own hooks
                 (the session-start note and the Grep redirect) - what a session with the plugin installed sees
    builtin      the built-in tools only

  Questions:
    definition   "where is X defined?" - scored right when the path matches and the line is within 2 of the
                 declaration (an attribute or doc comment above it counts)
    occurrences  "every line where X appears as a whole word" - scored on recall and precision against every
                 such line in the index

  Each session is a separate process with nothing carried over: no session saved (--no-session-persistence), no
  settings files, CLAUDE.md or memory (--setting-sources "" and CLAUDE_CODE_DISABLE_AUTO_MEMORY=1), no MCP servers
  but the one the arm names (--strict-mcp-config), and a fixed tool list (--tools). Only the harness version and the
  model should change the numbers, and both are written into the report. Models don't answer the same way twice, so
  each question runs -Runs times per arm (arms interleaved) and the table shows medians.

  Tokens are the session's own usage report, summed over every model it used: input = fresh + cache-written +
  cache-read input tokens (the context sent, whatever the cache did with it), output includes thinking. Cost is the
  harness's list-price figure for the same usage.

  Writes runs.jsonl (one line per session), summary.md, summary.html, summary.json and each session's stream.

  This spends real tokens: tasks x 2 arms x runs sessions per model (5 x 2 x 3 = 30 by default). Sessions log in
  the way `claude` already does on this machine (a subscription login or ANTHROPIC_API_KEY).

.PARAMETER Repo
  The repo folder (local or UNC).

.PARAMETER Corpus
  Or a pinned corpus id (e.g. requests), fetched via fetch-corpus.ps1 if not present.

.PARAMETER Model
  Model id(s), e.g. claude-sonnet-5-5. Several give one table each.

.PARAMETER Runs
  Sessions per question per arm. Default 3.

.PARAMETER SelfTestJson
  A `codecompass selftest --json` result for this repo to take the questions from (e.g. from selftest-matrix.ps1).
  Without it the script runs the self-test first.

.PARAMETER Definitions / Occurrences
  How many questions of each kind (up to what the self-test picked). Default 3 and 2.

.PARAMETER BuiltinTools
  The built-in tools both arms get. Default Read,Grep,Glob (no shell, nothing that writes).

.PARAMETER MaxBudgetUsd
  Per-session spending cap passed to claude. Default 1.

.PARAMETER TimeoutSec
  Per-session wall-clock limit. Default 600.

.PARAMETER Exe
  CodeCompass.Cli.exe (its CodeCompass.Mcp.exe next to it). Default: plugin\bin next to this repo.

.PARAMETER Claude
  The claude CLI. Default: the one on PATH.

.PARAMETER OutDir
  Default: %LOCALAPPDATA%\CodeCompass\token-compare\<repo>-<yyyyMMdd-HHmmss>.

.EXAMPLE
  .\scripts\token-compare.ps1 -Corpus requests -Model claude-sonnet-5-5 -Runs 1 -Definitions 1 -Occurrences 1
  .\scripts\token-compare.ps1 -Repo D:\work\big-repo -Model claude-sonnet-5-5,claude-opus-5-5
#>
[CmdletBinding()]
param(
    [string]$Repo,
    [string]$Corpus,
    [Parameter(Mandatory)][string[]]$Model,
    [int]$Runs = 3,
    [string]$SelfTestJson,
    [int]$Definitions = 3,
    [int]$Occurrences = 2,
    [string]$BuiltinTools = 'Read,Grep,Glob',
    [double]$MaxBudgetUsd = 1,
    [int]$TimeoutSec = 600,
    [string]$Exe,
    [string]$Claude,
    [string]$OutDir
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$Model = @($Model | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })

# ---- the repo, the tools ----
if ($Corpus) {
    $base = Join-Path $root ".corpus\$Corpus"
    if (-not (Test-Path $base)) { & (Join-Path $root 'fetch-corpus.ps1') $Corpus }
    $Repo = (Get-ChildItem $base -Directory | Select-Object -First 1).FullName
}
if (-not $Repo -or -not (Test-Path $Repo)) { throw "pass -Repo <folder> or -Corpus <id>" }
$Repo = (Resolve-Path $Repo).ProviderPath.TrimEnd('\')
$label = if ($Corpus) { $Corpus } else { Split-Path $Repo -Leaf }
if (-not $Exe) { $Exe = Join-Path $root 'plugin\bin\CodeCompass.Cli.exe' }
$Exe = (Resolve-Path $Exe).ProviderPath
$mcpExe = Join-Path (Split-Path $Exe) 'CodeCompass.Mcp.exe'
if (-not (Test-Path $mcpExe)) { throw "CodeCompass.Mcp.exe not next to $Exe" }
if (-not $Claude) {   # an .exe or .cmd; an npm install also puts a claude.ps1 first on PATH, which can't be started directly
    $Claude = (Get-Command claude -CommandType Application -All -ErrorAction Stop | Where-Object { $_.Source -match '\.(exe|cmd)$' } | Select-Object -First 1).Source
    if (-not $Claude) { throw "no claude.exe or claude.cmd on PATH; pass -Claude" }
}
if (-not $OutDir) { $OutDir = Join-Path $env:LOCALAPPDATA ("CodeCompass\token-compare\$label-" + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
New-Item -ItemType Directory -Force (Join-Path $OutDir 'streams') | Out-Null
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Run-Native([string]$file, [string[]]$argv, [string]$stdin, [string]$cwd, [hashtable]$env, [int]$timeoutSec) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    if ($file -match '\.(cmd|bat)$') { $psi.FileName = $env:ComSpec; $argv = @('/d', '/c', $file) + $argv } else { $psi.FileName = $file }
    $psi.Arguments = ($argv | ForEach-Object { if ($_ -eq '' ) { '""' } elseif ($_ -match '[\s"]') { '"' + ($_ -replace '(\\*)"', '$1$1\"' -replace '(\\+)$', '$1$1') + '"' } else { $_ } }) -join ' '
    $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
    $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
    $psi.StandardOutputEncoding = $utf8; $psi.StandardErrorEncoding = $utf8
    if ($cwd) { $psi.WorkingDirectory = $cwd }
    if ($env) {
        foreach ($k in @($psi.EnvironmentVariables.Keys)) { if ($env.ContainsKey("-$k")) { $psi.EnvironmentVariables.Remove($k) } }
        foreach ($k in $env.Keys) { if (-not $k.StartsWith('-')) { $psi.EnvironmentVariables[$k] = $env[$k] } }
    }
    $p = [System.Diagnostics.Process]::Start($psi)
    $out = $p.StandardOutput.ReadToEndAsync(); $err = $p.StandardError.ReadToEndAsync()
    if ($stdin) { $w = New-Object System.IO.StreamWriter($p.StandardInput.BaseStream, $utf8); $w.Write($stdin); $w.Close() } else { $p.StandardInput.Close() }
    $timedOut = -not $p.WaitForExit($timeoutSec * 1000)
    if ($timedOut) { try { $p.Kill() } catch {} ; $p.WaitForExit() }
    [pscustomobject]@{ Exit = $p.ExitCode; Out = $out.Result; Err = $err.Result; TimedOut = $timedOut }
}

# ---- the questions: from the self-test's picks and checked answers ----
if (-not $SelfTestJson) {
    $SelfTestJson = Join-Path $OutDir 'selftest.json'
    Write-Host "running the self-test for the questions (indexes the repo)..." -ForegroundColor DarkCyan
    $r = Run-Native $Exe @('selftest', $Repo, '--json', $SelfTestJson, '--html', (Join-Path $OutDir 'selftest.html')) $null $null $null 21600
    if (-not (Test-Path $SelfTestJson)) { throw "self-test wrote no JSON (exit $($r.Exit)): $($r.Err | Select-Object -Last 1)" }
} else {
    Copy-Item $SelfTestJson (Join-Path $OutDir 'selftest.json')
    $r = Run-Native $Exe @('update', $Repo) $null $null $null 21600   # the server needs an index, and a current one
    if ($r.Exit -ne 0) { throw "codecompass update failed: $($r.Err)" }
}
$st = Get-Content $SelfTestJson -Raw -Encoding UTF8 | ConvertFrom-Json
if (-not $st.inputs) { throw "$SelfTestJson has no inputs (an anonymized self-test can't be used: its names are hashed)" }

$tasks = New-Object System.Collections.Generic.List[object]
$n = 0
foreach ($d in @($st.inputs.definitions) | Select-Object -First $Definitions) {
    $n++
    $tasks.Add([pscustomobject]@{ id = "def$n"; kind = 'definition'; name = $d.name; symbolKind = $d.kind
        expected = [pscustomobject]@{ path = $d.path; line = [int]$d.line }
        question = "Where is ``$($d.name)`` ($($d.kind.ToLowerInvariant())) defined?`n" +
                   "Answer JSON: {""path"": ""<path relative to the current directory, / separators>"", ""line"": <1-based line of its declaration>}" })
}
$n = 0
foreach ($s in @($st.inputs.searches) | Select-Object -First $Occurrences) {
    $n++
    $tasks.Add([pscustomobject]@{ id = "occ$n"; kind = 'occurrences'; name = $s.name; symbolKind = $s.kind
        expected = @($s.lines | ForEach-Object { [pscustomobject]@{ path = $_.path; line = [int]$_.line } })
        question = "Find every line in this repository where ``$($s.name)`` appears as a whole word - in code, comments and strings alike.`n" +
                   "Answer JSON: {""lines"": [{""path"": ""<path relative to the current directory, / separators>"", ""line"": <1-based line>}, ...]}" })
}
if ($tasks.Count -eq 0) { throw "the self-test picked no names to ask about" }
$preamble = "Answer a question about the code in the current directory, using the tools you have. Don't change any file. " +
            "End your reply with one line holding only the JSON answer, with nothing after it.`n`n"

# ---- the two arms ----
$q = '"' + $Exe + '"'
$mcpCfg = Join-Path $OutDir 'mcp-codecompass.json'
$emptyCfg = Join-Path $OutDir 'mcp-none.json'
$settings = Join-Path $OutDir 'settings-codecompass.json'
[IO.File]::WriteAllText($mcpCfg, (@{ mcpServers = @{ codecompass = @{ type = 'stdio'; command = $mcpExe; args = @($Repo) } } } | ConvertTo-Json -Depth 5), $utf8)
[IO.File]::WriteAllText($emptyCfg, '{"mcpServers":{}}', $utf8)
# The plugin's hooks (plugin\hooks\hooks.json), with the plugin root spelled out.
[IO.File]::WriteAllText($settings, (@{ hooks = @{
    PreToolUse   = @(@{ matcher = 'Grep'; hooks = @(@{ type = 'command'; command = "$q hook-block" }) })
    SessionStart = @(@{ matcher = ''; hooks = @(@{ type = 'command'; command = "$q hook-context" }) })
} } | ConvertTo-Json -Depth 8), $utf8)
$builtinList = @($BuiltinTools -split '[,\s]+' | Where-Object { $_ })
$arms = @(
    [pscustomobject]@{ id = 'codecompass'; mcp = $mcpCfg; settings = $settings; allowed = ($builtinList + 'mcp__codecompass') -join ' ' },
    [pscustomobject]@{ id = 'builtin'; mcp = $emptyCfg; settings = $null; allowed = $builtinList -join ' ' }
)
# Nothing from the shell this runs in: a parent Claude Code session's variables are dropped (auth ones kept).
$envFix = @{ CLAUDE_CODE_DISABLE_AUTO_MEMORY = '1' }
foreach ($k in [Environment]::GetEnvironmentVariables().Keys) {
    if (($k -like 'CLAUDE_CODE_*' -or $k -eq 'CLAUDECODE') -and $k -notin 'CLAUDE_CODE_OAUTH_TOKEN', 'CLAUDE_CODE_USE_BEDROCK', 'CLAUDE_CODE_USE_VERTEX') { $envFix["-$k"] = $true }
}

$harness = ((Run-Native $Claude @('--version') $null $null $envFix 60).Out).Trim()
Write-Host "repo $label | CodeCompass $($st.version) | $harness | models $($Model -join ', ') | $($tasks.Count) questions x 2 arms x $Runs runs" -ForegroundColor Cyan

# ---- scoring ----
function Norm-Path([string]$p) {
    if (-not $p) { return '' }
    $p = $p.Trim().Replace('\', '/')
    $rootFwd = $Repo.Replace('\', '/').TrimEnd('/') + '/'
    if ($p.StartsWith($rootFwd, [StringComparison]::OrdinalIgnoreCase)) { $p = $p.Substring($rootFwd.Length) }
    while ($p.StartsWith('./')) { $p = $p.Substring(2) }
    $p.TrimStart('/').ToLowerInvariant()
}
function Last-Json([string]$text) {
    if (-not $text) { return $null }
    for ($i = $text.LastIndexOf('{'); $i -ge 0; $i = $text.LastIndexOf('{', [Math]::Max($i - 1, 0))) {
        try { return ($text.Substring($i) -replace '(?s)\}[^}]*$', '}' | ConvertFrom-Json) } catch {}
        if ($i -eq 0) { break }
    }
    $null
}
function Score($task, [string]$text) {
    $a = Last-Json $text
    if ($task.kind -eq 'definition') {
        $ok = $false
        if ($a -and $a.path) { $ok = (Norm-Path $a.path) -eq (Norm-Path $task.expected.path) -and [Math]::Abs([int]$a.line - $task.expected.line) -le 2 }
        return [pscustomobject]@{ correct = $ok; parsed = [bool]$a; recall = $null; precision = $null }
    }
    $want = @{}; foreach ($l in $task.expected) { $want["$(Norm-Path $l.path):$($l.line)"] = 1 }
    $got = @{}; if ($a -and $a.lines) { foreach ($l in @($a.lines)) { $got["$(Norm-Path $l.path):$($l.line)"] = 1 } }
    $hit = @($got.Keys | Where-Object { $want.ContainsKey($_) }).Count
    $rec = if ($want.Count) { $hit / $want.Count } else { 1 }
    $pre = if ($got.Count) { $hit / $got.Count } else { 0 }
    [pscustomobject]@{ correct = ($rec -eq 1 -and $pre -eq 1); parsed = [bool]$a; recall = [Math]::Round($rec, 3); precision = [Math]::Round($pre, 3) }
}

# ---- the sessions ----
$runsFile = Join-Path $OutDir 'runs.jsonl'
$all = New-Object System.Collections.Generic.List[object]
$total = $Model.Count * $tasks.Count * $arms.Count * $Runs; $done = 0
foreach ($m in $Model) {
    foreach ($t in $tasks) {
        for ($run = 1; $run -le $Runs; $run++) {
            $order = if ($run % 2) { $arms } else { @($arms[1], $arms[0]) }   # alternate which arm goes first
            foreach ($arm in $order) {
                $done++
                $argv = @('-p', '--no-session-persistence', '--output-format', 'stream-json', '--verbose', '--model', $m,
                          '--setting-sources', '', '--strict-mcp-config', '--mcp-config', $arm.mcp,
                          '--tools', ($builtinList -join ','), '--permission-mode', 'dontAsk', '--allowedTools', $arm.allowed,
                          '--max-budget-usd', ([string]$MaxBudgetUsd))
                if ($arm.settings) { $argv += @('--settings', $arm.settings) }
                $sw = [Diagnostics.Stopwatch]::StartNew()
                $res = Run-Native $Claude $argv ($preamble + $t.question) $Repo $envFix $TimeoutSec
                $sw.Stop()
                $stream = Join-Path $OutDir ("streams\{0}-{1}-{2}-{3}.jsonl" -f ($m -replace '[^\w.-]', '_'), $t.id, $arm.id, $run)
                [IO.File]::WriteAllText($stream, $res.Out, $utf8)

                $final = $null; $tools = @{}
                foreach ($line in ($res.Out -split "`n")) {
                    if (-not $line.Trim().StartsWith('{')) { continue }
                    try { $e = $line | ConvertFrom-Json } catch { continue }
                    if ($e.type -eq 'assistant') { foreach ($c in @($e.message.content)) { if ($c.type -eq 'tool_use') { $tools[$c.name] = 1 + [int]$tools[$c.name] } } }
                    elseif ($e.type -eq 'result') { $final = $e }
                }
                $inTok = 0; $outTok = 0; $cost = $null; $models = @()
                if ($final -and $final.modelUsage) {
                    foreach ($p in $final.modelUsage.PSObject.Properties) {
                        $u = $p.Value; $models += $p.Name
                        $inTok += [long]$u.inputTokens + [long]$u.cacheCreationInputTokens + [long]$u.cacheReadInputTokens
                        $outTok += [long]$u.outputTokens
                    }
                    $cost = $final.total_cost_usd
                }
                $answer = if ($final) { [string]$final.result } else { '' }
                $score = Score $t $answer
                $row = [pscustomobject]@{
                    model = $m; task = $t.id; kind = $t.kind; name = $t.name; arm = $arm.id; run = $run
                    ok = [bool]($final -and -not $final.is_error -and -not $res.TimedOut)
                    error = if ($res.TimedOut) { "timed out after $TimeoutSec s" } elseif (-not $final) { ($res.Err + $res.Out).Trim() -replace '\s+', ' ' | ForEach-Object { $_.Substring(0, [Math]::Min(300, $_.Length)) } } elseif ($final.is_error) { [string]$final.result } else { $null }
                    inputTokens = $inTok; outputTokens = $outTok; costUsd = $cost
                    turns = if ($final) { $final.num_turns } else { $null }
                    seconds = [Math]::Round($sw.Elapsed.TotalSeconds, 1)
                    tools = $tools; modelsUsed = $models
                    correct = $score.correct; recall = $score.recall; precision = $score.precision; parsed = $score.parsed
                    answer = $answer
                }
                $all.Add($row)
                Add-Content -Path $runsFile -Value ($row | ConvertTo-Json -Depth 5 -Compress) -Encoding UTF8
                $toolsTxt = ($tools.GetEnumerator() | ForEach-Object { "$($_.Key -replace '^mcp__codecompass__', 'cc:')x$($_.Value)" }) -join ' '
                Write-Host ("[{0}/{1}] {2} {3} {4,-11} run {5}: {6,8:N0} in / {7,6:N0} out, {8} turns, {9,5:N1} s, {10}  {11}" -f $done, $total, $m, $t.id, $arm.id, $run,
                    $inTok, $outTok, $row.turns, $row.seconds, $(if (-not $row.ok) { "ERROR: $($row.error)" } elseif ($row.correct) { 'right' } else { 'WRONG' + $(if ($null -ne $row.recall) { " (recall $($row.recall), precision $($row.precision))" } else { '' }) }), $toolsTxt)
            }
        }
    }
}

# ---- the summary ----
function Median($xs) {
    $s = @($xs | Where-Object { $null -ne $_ } | Sort-Object { [double]$_ }); $c = $s.Count
    if ($c -eq 0) { return $null }
    if ($c % 2) { [double]$s[($c - 1) / 2] } else { ([double]$s[$c / 2 - 1] + [double]$s[$c / 2]) / 2 }
}
function Cell($rows) {
    $ok = @($rows | Where-Object ok)
    [pscustomobject]@{
        runs = @($rows).Count; errors = @($rows).Count - $ok.Count
        right = @($ok | Where-Object correct).Count
        inputTokens = Median ($ok | ForEach-Object inputTokens); outputTokens = Median ($ok | ForEach-Object outputTokens)
        costUsd = Median ($ok | ForEach-Object costUsd); turns = Median ($ok | ForEach-Object turns); seconds = Median ($ok | ForEach-Object seconds)
        recall = Median ($ok | ForEach-Object recall)
    }
}
function K($v) { if ($null -eq $v) { '-' } elseif ($v -ge 1000) { '{0:N1}k' -f ($v / 1000) } else { '{0:N0}' -f $v } }
function Usd($v) { if ($null -eq $v) { '-' } else { '${0:N3}' -f $v } }
function Pct($a, $b) { if (-not $a -or -not $b) { '-' } else { '{0:N0}%' -f (100 * (1 - $a / $b)) } }

$summary = New-Object System.Collections.Generic.List[object]
$md = New-Object System.Text.StringBuilder
[void]$md.AppendLine("Token use answering $($tasks.Count) code questions about **$label**: CodeCompass $($st.version), $harness, $(Get-Date -Format 'yyyy-MM-dd').")
[void]$md.AppendLine("Medians over $Runs fresh session(s) per question per arm. Input = all input tokens sent (fresh + cache), output includes thinking. Right = answers matching the index's checked answer.")
foreach ($m in $Model) {
    $head = '| Question | CodeCompass in / out | Built-in in / out | Input saved | CodeCompass cost | Built-in cost | Turns (CC / built-in) | Seconds (CC / built-in) | Right (CC / built-in) |'
    [void]$md.AppendLine(); [void]$md.AppendLine("**$m**"); [void]$md.AppendLine(); [void]$md.AppendLine($head)
    [void]$md.AppendLine('|---|---|---|---|---|---|---|---|---|')
    $sumA = @{ i = 0.0; o = 0.0; c = 0.0 }; $sumB = @{ i = 0.0; o = 0.0; c = 0.0 }
    foreach ($t in $tasks) {
        $a = Cell ($all | Where-Object { $_.model -eq $m -and $_.task -eq $t.id -and $_.arm -eq 'codecompass' })
        $b = Cell ($all | Where-Object { $_.model -eq $m -and $_.task -eq $t.id -and $_.arm -eq 'builtin' })
        $summary.Add([pscustomobject]@{ model = $m; task = $t.id; kind = $t.kind; name = $t.name; codecompass = $a; builtin = $b })
        $sumA.i += [double]$a.inputTokens; $sumA.o += [double]$a.outputTokens; $sumA.c += [double]$a.costUsd
        $sumB.i += [double]$b.inputTokens; $sumB.o += [double]$b.outputTokens; $sumB.c += [double]$b.costUsd
        $q = if ($t.kind -eq 'definition') { "where is ``$($t.name)`` defined" } else { "every line with ``$($t.name)`` ($(@($t.expected).Count))" }
        $err = if ($a.errors + $b.errors) { " ($($a.errors)/$($b.errors) errored)" } else { '' }
        [void]$md.AppendLine("| $q | $(K $a.inputTokens) / $(K $a.outputTokens) | $(K $b.inputTokens) / $(K $b.outputTokens) | $(Pct $a.inputTokens $b.inputTokens) | $(Usd $a.costUsd) | $(Usd $b.costUsd) | $($a.turns) / $($b.turns) | $($a.seconds) / $($b.seconds) | $($a.right)/$($a.runs) / $($b.right)/$($b.runs)$err |")
    }
    [void]$md.AppendLine("| **all questions** | **$(K $sumA.i) / $(K $sumA.o)** | **$(K $sumB.i) / $(K $sumB.o)** | **$(Pct $sumA.i $sumB.i)** | **$(Usd $sumA.c)** | **$(Usd $sumB.c)** | | | |")
}
$md.ToString() | Set-Content (Join-Path $OutDir 'summary.md') -Encoding UTF8
[pscustomobject]@{
    repo = $label; codecompass = $st.version; harness = $harness; models = $Model; runs = $Runs; date = (Get-Date -Format 'yyyy-MM-dd')
    builtinTools = $builtinList; tasks = @($tasks | ForEach-Object { [pscustomobject]@{ id = $_.id; kind = $_.kind; name = $_.name; question = $_.question } })
    results = $summary
} | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $OutDir 'summary.json') -Encoding UTF8

# The same table as a page.
$enc = { param($s) [System.Net.WebUtility]::HtmlEncode([string]$s) }
$body = New-Object System.Text.StringBuilder
foreach ($line in ($md.ToString() -split "`r?`n")) {
    if ($line -match '^\|---') { continue }
    if ($line -match '^\| Question') { [void]$body.Append('<div class="scroll"><table><thead><tr>' + (($line.Trim('|').Split('|') | ForEach-Object { "<th>$(& $enc $_.Trim())</th>" }) -join '') + '</tr></thead><tbody>'); continue }
    if ($line -match '^\|') {
        $cells = $line.Trim().Trim('|').Split('|') | ForEach-Object { (& $enc $_.Trim()) -replace '\*\*(.+?)\*\*', '<b>$1</b>' -replace '`(.+?)`', '<code>$1</code>' }
        [void]$body.Append('<tr>' + (($cells | ForEach-Object { "<td>$_</td>" }) -join '') + '</tr>')
        continue
    }
    if ($body.ToString().EndsWith('</tr>')) { [void]$body.Append('</tbody></table></div>') }
    if ($line.Trim()) { [void]$body.Append('<p>' + ((& $enc $line) -replace '\*\*(.+?)\*\*', '<b>$1</b>') + '</p>') }
}
if ($body.ToString().EndsWith('</tr>')) { [void]$body.Append('</tbody></table></div>') }
$page = @"
<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Token comparison</title><style>
:root{--bg:#f6f8fa;--card:#fff;--ink:#1f2328;--muted:#59636e;--line:#d1d9e0;--head:#eef1f4;--code:#eef1f4}
@media (prefers-color-scheme:dark){:root:not([data-theme="light"]){--bg:#0f1720;--card:#1b2430;--ink:#e6edf3;--muted:#9fb0c0;--line:#2b3947;--head:#141d28;--code:#111a24}}
:root[data-theme="dark"]{--bg:#0f1720;--card:#1b2430;--ink:#e6edf3;--muted:#9fb0c0;--line:#2b3947;--head:#141d28;--code:#111a24}
body{margin:0;background:var(--bg);color:var(--ink);font:14px/1.5 -apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif;padding:28px 16px}
.wrap{max-width:1200px;margin:0 auto} h1{font-size:22px;margin:0 0 8px} p{color:var(--muted)} p b{color:var(--ink)}
.scroll{overflow-x:auto;border:1px solid var(--line);border-radius:10px;background:var(--card);margin:0 0 18px}
table{width:100%;border-collapse:collapse} th,td{padding:8px 10px;text-align:left;border-bottom:1px solid var(--line);white-space:nowrap}
th{background:var(--head);color:var(--muted);font-weight:600;font-size:12px} code{background:var(--code);padding:1px 4px;border-radius:4px}
</style></head><body><div class="wrap"><h1>Token comparison: $(& $enc $label)</h1>$($body.ToString())</div></body></html>
"@
[IO.File]::WriteAllText((Join-Path $OutDir 'summary.html'), $page, $utf8)

Write-Host ""
Write-Host $md.ToString()
Write-Host "written: $OutDir (summary.md, summary.html, summary.json, runs.jsonl, streams\)"
