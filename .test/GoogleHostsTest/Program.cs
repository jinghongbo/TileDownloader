using TileDownloader.Services;

var svc = new GoogleHostsService();
var lastMsg = "";
var progress = new Progress<string>(msg =>
{
    if (msg != lastMsg)
    {
        lastMsg = msg;
        Console.WriteLine($"[状态] {msg}");
    }
});
var lastPct = -1.0;
var scanProgress = new Progress<GoogleHostScanProgress>(p =>
{
    // 控制台按 5% 步长打印，避免刷屏
    if (p.Percent - lastPct >= 5 || p.Percent >= 100)
    {
        lastPct = p.Percent;
        Console.WriteLine($"[进度] {p.Percent,5:F1}%  {p.Done}/{p.Total}  可达 {p.Reachable}");
    }
});
using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));

var sw = System.Diagnostics.Stopwatch.StartNew();
var best = await svc.FindBestAsync(progress, cts.Token, scanProgress);
sw.Stop();

Console.WriteLine();
Console.WriteLine($"总耗时 {sw.ElapsedMilliseconds / 1000.0:F1}s");
Console.WriteLine(best != null
    ? $"最快 IP：{best.Ip}（{best.Milliseconds}ms）"
    : "无可用 IP");
